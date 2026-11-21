// SPDX-License-Identifier: LGPL-2.1-or-later
// Prospero Sender Lite: one side of the split screen. A tree of drives on the left
// (This PC, or the PS5 with its internal drive, M.2 SSD and USB drives), the open
// folder's contents on the right, as in Windows Explorer.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PSLite
{
    class Entry
    {
        public string Name, Path, Type = "";
        public bool IsDir;
        public long Size;
        public DateTime Modified;
        public int Icon;
    }

    abstract class FileSource
    {
        public abstract string RootLabel { get; }
        public abstract int RootIcon { get; }
        public abstract bool Remote { get; }
        /// <summary>The drives shown under the root.</summary>
        public abstract List<Entry> Places();
        public abstract List<Entry> ListFolder(string path);
        /// <summary>The folder above path; null when path is a drive (the root is above it).</summary>
        public abstract string Parent(string path);
        public abstract string Combine(string path, string name);
    }

    class LocalSource : FileSource
    {
        public override string RootLabel { get { return "This PC"; } }
        public override int RootIcon { get { return Theme.IcoPc; } }
        public override bool Remote { get { return false; } }

        public override List<Entry> Places()
        {
            List<Entry> found = new List<Entry>();
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    Entry e = new Entry();
                    string letter = d.Name.TrimEnd('\\');
                    string label = d.VolumeLabel;
                    if (string.IsNullOrEmpty(label)) label = d.DriveType == DriveType.Removable ? "USB Drive" : d.DriveType == DriveType.Network ? "Network Drive" : "Local Disk";
                    e.Name = label + " (" + letter + ")";
                    e.Path = d.Name;
                    e.IsDir = true;
                    e.Size = d.TotalSize;
                    e.Type = Theme.Size(d.AvailableFreeSpace) + " free of " + Theme.Size(d.TotalSize);
                    e.Icon = d.DriveType == DriveType.Removable ? Theme.IcoUsb : Theme.IcoDrive;
                    found.Add(e);
                }
                catch (Exception) { }
            }
            return found;
        }

        public override List<Entry> ListFolder(string path)
        {
            List<Entry> found = new List<Entry>();
            foreach (FileSystemInfo info in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                try
                {
                    FileAttributes a = info.Attributes;
                    if ((a & FileAttributes.System) != 0 && (a & FileAttributes.Hidden) != 0) continue;
                    Entry e = new Entry();
                    e.Name = info.Name;
                    e.Path = info.FullName;
                    e.IsDir = (a & FileAttributes.Directory) != 0;
                    e.Modified = info.LastWriteTime;
                    e.Icon = e.IsDir ? Theme.IcoFolder : Theme.IcoFile;
                    if (e.IsDir) e.Type = "File folder";
                    else
                    {
                        e.Size = ((FileInfo)info).Length;
                        string ext = info.Extension.TrimStart('.').ToUpperInvariant();
                        e.Type = ext.Length > 0 ? ext + " file" : "File";
                    }
                    found.Add(e);
                }
                catch (Exception) { }
            }
            return found;
        }

        public override string Parent(string path)
        {
            try
            {
                DirectoryInfo parent = Directory.GetParent(path.TrimEnd('\\').Length <= 2 ? path : path.TrimEnd('\\'));
                return parent == null ? null : parent.FullName;
            }
            catch (Exception) { return null; }
        }

        public override string Combine(string path, string name)
        {
            return System.IO.Path.Combine(path, name);
        }
    }

    /// <summary>The PS5's folders, listed by the receiver's service port. Its drives: the internal
    /// drive ("/"), the M.2 SSD (/mnt/ext1), extended USB storage (/mnt/ext0) and USB drives
    /// (/mnt/usb0-7). A mount point with nothing in it is a drive that isn't there.</summary>
    class Ps5Source : FileSource
    {
        readonly Func<string> host;
        public const string Internal = "/";
        public const string M2 = "/mnt/ext1";

        public Ps5Source(Func<string> host) { this.host = host; }

        public override string RootLabel { get { return "PS5"; } }
        public override int RootIcon { get { return Theme.IcoConsole; } }
        public override bool Remote { get { return true; } }

        static Entry Drive(string label, string path, int icon, string type)
        {
            Entry e = new Entry();
            e.Name = label; e.Path = path; e.IsDir = true; e.Icon = icon; e.Type = type;
            return e;
        }

        public override List<Entry> Places()
        {
            List<Entry> found = new List<Entry>();
            found.Add(Drive("Internal drive", Internal, Theme.IcoDrive, "Internal storage"));
            List<string> ask = new List<string>();
            ask.Add(M2);
            ask.Add("/mnt/ext0");
            for (int i = 0; i < 8; i++) ask.Add("/mnt/usb" + i);
            Dictionary<string, List<RemoteEntry>> all;
            using (Svc svc = new Svc(host())) all = svc.ListMany(ask);
            int usb = 0;
            foreach (string path in ask)
            {
                List<RemoteEntry> inside;
                if (!all.TryGetValue(path, out inside) || inside == null || inside.Count == 0) continue;
                if (path == M2) found.Add(Drive("M.2 SSD", path, Theme.IcoSsd, "M.2 SSD storage"));
                else if (path == "/mnt/ext0") found.Add(Drive("Extended storage", path, Theme.IcoUsb, "USB extended storage"));
                else found.Add(Drive("USB drive" + (++usb > 1 ? " " + usb : "") + " (" + path.Substring(5) + ")", path, Theme.IcoUsb, "USB drive"));
            }
            return found;
        }

        public override List<Entry> ListFolder(string path)
        {
            List<RemoteEntry> raw;
            using (Svc svc = new Svc(host())) raw = svc.ListOne(path);
            List<Entry> found = new List<Entry>();
            foreach (RemoteEntry r in raw)
            {
                Entry e = new Entry();
                e.Name = r.Name;
                e.Path = Combine(path, r.Name);
                e.IsDir = r.IsDir;
                e.Size = r.IsDir ? 0 : r.Size;
                e.Modified = r.Modified;
                e.Icon = r.IsDir ? Theme.IcoFolder : Theme.IcoFile;
                if (r.IsDir) e.Type = "File folder";
                else if (r.IsLink) e.Type = "Link";
                else
                {
                    int dot = r.Name.LastIndexOf('.');
                    e.Type = dot > 0 && dot < r.Name.Length - 1 ? r.Name.Substring(dot + 1).ToUpperInvariant() + " file" : "File";
                }
                found.Add(e);
            }
            return found;
        }

        public override string Parent(string path)
        {
            if (path == Internal || path == M2 || path == "/mnt/ext0") return null;
            if (path.StartsWith("/mnt/usb") && path.IndexOf('/', 5) < 0) return null;
            int slash = path.TrimEnd('/').LastIndexOf('/');
            return slash <= 0 ? "/" : path.Substring(0, slash);
        }

        public override string Combine(string path, string name)
        {
            return Engine.Join(path, name);
        }
    }

    class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.Panel2; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Panel2; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Panel2; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Panel2; } }
        public override Color MenuBorder { get { return Theme.Border; } }
        public override Color MenuItemBorder { get { return Theme.Accent; } }
        public override Color MenuItemSelected { get { return Color.FromArgb(0x2F, 0x4A, 0x78); } }
        public override Color SeparatorDark { get { return Theme.Border; } }
        public override Color SeparatorLight { get { return Theme.Border; } }
    }

    class FilePane : UserControl
    {
        public readonly FileSource Source;
        public string Current;                           // null: the root (the list of drives)
        public event Action<FilePane, string> Command;   // copy, paste, delete, rename, newfolder, send
        public event Action<FilePane, string[]> Dropped; // files dropped on this pane

        readonly TreeView tree = new TreeView();
        readonly ListView list = new ListView();
        readonly TextBox pathBox;
        readonly Label footer = new Label();
        readonly Label title;
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        TreeNode rootNode;
        int generation, sortColumn;
        bool sortDown, selecting;
        List<Entry> shown = new List<Entry>();

        public FilePane(FileSource source, ImageList icons)
        {
            Source = source;
            BackColor = Theme.Panel;
            Font = Theme.Ui;

            Panel header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = Theme.S(34);
            header.BackColor = Theme.Panel2;
            header.Padding = new Padding(Theme.S(8), Theme.S(5), Theme.S(6), Theme.S(5));
            title = new Label();
            title.Text = source.RootLabel;
            title.Font = Theme.UiBold;
            title.ForeColor = Theme.Text;
            title.Dock = DockStyle.Left;
            title.Width = Theme.S(62);
            title.TextAlign = ContentAlignment.MiddleLeft;
            Button up = Theme.MakeButton("Up", false);
            Button refresh = Theme.MakeButton("Refresh", false);
            up.Dock = DockStyle.Right;
            refresh.Dock = DockStyle.Right;
            up.AutoSize = false; refresh.AutoSize = false;
            up.Width = Theme.S(44); refresh.Width = Theme.S(64);
            up.Click += delegate { GoUp(); };
            refresh.Click += delegate { Reload(); };
            pathBox = Theme.MakeBox("", 100);
            pathBox.Dock = DockStyle.Fill;
            pathBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                string typed = pathBox.Text.Trim();
                Navigate(typed.Length == 0 || typed == source.RootLabel ? null : typed);
            };
            Panel boxHolder = new Panel();
            boxHolder.Dock = DockStyle.Fill;
            boxHolder.Padding = new Padding(Theme.S(4), Theme.S(1), Theme.S(6), 0);
            boxHolder.Controls.Add(pathBox);
            header.Controls.Add(boxHolder);
            header.Controls.Add(title);
            header.Controls.Add(refresh);
            header.Controls.Add(up);
            boxHolder.BringToFront();

            footer.Dock = DockStyle.Bottom;
            footer.Height = Theme.S(22);
            footer.ForeColor = Theme.Dim;
            footer.BackColor = Theme.Panel2;
            footer.Font = Theme.Small;
            footer.TextAlign = ContentAlignment.MiddleLeft;
            footer.Padding = new Padding(Theme.S(8), 0, 0, 0);

            tree.Dock = DockStyle.Fill;
            tree.BorderStyle = BorderStyle.None;
            tree.BackColor = Theme.Panel;
            tree.ForeColor = Theme.Text;
            tree.Font = Theme.Ui;
            tree.ImageList = icons;
            tree.ShowLines = false;
            tree.FullRowSelect = true;
            tree.HideSelection = false;
            tree.ItemHeight = Theme.S(24);
            Theme.StyleTree(tree);
            tree.Indent = Theme.S(18);
            tree.HandleCreated += delegate { Theme.Dark(tree); };
            tree.BeforeExpand += delegate(object sender, TreeViewCancelEventArgs e) { Expand(e.Node); };
            tree.AfterSelect += delegate(object sender, TreeViewEventArgs e)
            {
                if (selecting || e.Action == TreeViewAction.Unknown) return;
                Navigate((string)e.Node.Tag);
            };

            Theme.StyleList(list);
            list.Dock = DockStyle.Fill;
            list.SmallImageList = icons;
            list.MultiSelect = true;
            list.AllowColumnReorder = false;
            list.Columns.Add("Name", Theme.S(240));
            list.Columns.Add("Size", Theme.S(84), HorizontalAlignment.Right);
            list.Columns.Add("Type", Theme.S(110));
            list.Columns.Add("Date modified", Theme.S(128));
            list.Resize += delegate { Theme.FillColumn(list, 0); };
            list.ItemActivate += delegate { OpenSelected(); };
            list.ColumnClick += delegate(object sender, ColumnClickEventArgs e)
            {
                sortDown = sortColumn == e.Column && !sortDown;
                sortColumn = e.Column;
                Fill(shown, null);
            };
            list.SelectedIndexChanged += delegate { ShowCount(); };
            list.KeyDown += ListKey;
            list.ContextMenuStrip = menu;
            list.AllowDrop = true;
            list.DragEnter += delegate(object sender, DragEventArgs e)
            {
                e.Effect = Source.Remote && Current != null && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            };
            list.DragDrop += delegate(object sender, DragEventArgs e)
            {
                string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length > 0 && Dropped != null) Dropped(this, files);
            };
            list.ItemDrag += delegate
            {
                if (Source.Remote) return;
                string[] paths = SelectedPaths();
                if (paths.Length > 0) list.DoDragDrop(new DataObject(DataFormats.FileDrop, paths), DragDropEffects.Copy);
            };

            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.BackColor = Theme.Border;
            split.SplitterWidth = Math.Max(1, Theme.S(1));
            split.Panel1.BackColor = Theme.Panel;
            split.Panel2.BackColor = Theme.Panel;
            split.Panel1.Controls.Add(tree);
            split.Panel2.Controls.Add(list);
            split.Panel1.Padding = new Padding(Theme.S(4), Theme.S(4), 0, 0);
            split.FixedPanel = FixedPanel.Panel1;
            bool placed = false;
            Resize += delegate
            {
                if (placed || split.Width < Theme.S(420)) return;
                placed = true;
                try { split.SplitterDistance = Theme.S(190); } catch (Exception) { }
            };

            Controls.Add(split);
            Controls.Add(footer);
            Controls.Add(header);
            split.BringToFront();

            BuildMenu();
            rootNode = new TreeNode(source.RootLabel, source.RootIcon, source.RootIcon);
            rootNode.Tag = null;
            tree.Nodes.Add(rootNode);
        }

        void BuildMenu()
        {
            menu.Renderer = new ToolStripProfessionalRenderer(new DarkColors());
            menu.ForeColor = Theme.Text;
            menu.BackColor = Theme.Panel2;
            menu.ShowImageMargin = false;
            menu.Font = Theme.Ui;
            if (!Source.Remote) AddItem("Send to PS5", "send", "");
            AddItem("Copy", "copy", "Ctrl+C");
            AddItem("Paste", "paste", "Ctrl+V");
            menu.Items.Add(new ToolStripSeparator());
            AddItem("New folder", "newfolder", "");
            AddItem("Rename", "rename", "F2");
            AddItem("Delete", "delete", "Del");
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem refresh = new ToolStripMenuItem("Refresh");
            refresh.ShortcutKeyDisplayString = "F5";
            refresh.ForeColor = Theme.Text;
            refresh.Click += delegate { Reload(); };
            menu.Items.Add(refresh);
        }

        void AddItem(string text, string command, string keys)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.ForeColor = Theme.Text;
            if (keys.Length > 0) item.ShortcutKeyDisplayString = keys;
            item.Click += delegate { Raise(command); };
            menu.Items.Add(item);
        }

        void Raise(string command)
        {
            if (Command != null) Command(this, command);
        }

        void ListKey(object sender, KeyEventArgs e)
        {
            string command = null;
            if (e.KeyCode == Keys.Back) GoUp();
            else if (e.KeyCode == Keys.F5) Reload();
            else if (e.KeyCode == Keys.Delete) command = "delete";
            else if (e.KeyCode == Keys.F2) command = "rename";
            else if (e.Control && e.KeyCode == Keys.C) command = "copy";
            else if (e.Control && e.KeyCode == Keys.V) command = "paste";
            else if (e.Control && e.KeyCode == Keys.A)
            {
                foreach (ListViewItem item in list.Items) item.Selected = true;
            }
            else return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            if (command != null) Raise(command);
        }

        public List<Entry> Selected()
        {
            List<Entry> found = new List<Entry>();
            foreach (ListViewItem item in list.SelectedItems)
                if (item.Tag is Entry) found.Add((Entry)item.Tag);
            return found;
        }

        public string[] SelectedPaths()
        {
            List<string> paths = new List<string>();
            foreach (Entry e in Selected()) paths.Add(e.Path);
            return paths.ToArray();
        }

        void OpenSelected()
        {
            List<Entry> picked = Selected();
            if (picked.Count == 1 && picked[0].IsDir) Navigate(picked[0].Path);
        }

        public void GoUp()
        {
            if (Current == null) return;
            Navigate(Source.Parent(Current));
        }

        public void Reload()
        {
            Navigate(Current);
            LoadPlaces();
        }

        public void SetMessage(string text, bool bad)
        {
            footer.ForeColor = bad ? Theme.Bad : Theme.Dim;
            footer.Text = text;
        }

        void ShowCount()
        {
            int picked = list.SelectedItems.Count;
            long size = 0;
            foreach (Entry e in Selected()) size += e.Size;
            string text = shown.Count + (shown.Count == 1 ? " item" : " items");
            if (picked > 0) text += "    " + picked + " selected" + (size > 0 ? "  (" + Theme.Size(size) + ")" : "");
            SetMessage(text, false);
        }

        /// <summary>Opens a folder (null: the list of drives). The listing is read off the UI thread.</summary>
        public void Navigate(string path)
        {
            int mine = ++generation;
            pathBox.Text = path ?? Source.RootLabel;
            SetMessage("Loading...", false);
            FileSource source = Source;
            Task.Factory.StartNew(delegate
            {
                List<Entry> found = null;
                string error = null;
                try { found = path == null ? source.Places() : source.ListFolder(path); }
                catch (Exception e) { error = e.Message; }
                Post(delegate
                {
                    if (mine != generation) return;
                    if (error != null)
                    {
                        // the folder that was open stays in the path box, so Refresh tries it again
                        Fill(new List<Entry>(), error);
                        Current = path;
                        return;
                    }
                    Current = path;
                    sortColumn = path == null ? -1 : sortColumn < 0 ? 0 : sortColumn;
                    Fill(found, null);
                    if (path == null) SetPlaces(found);
                });
            });
        }

        void Post(MethodInvoker action)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(action);
            }
            catch (Exception) { }
        }

        void Fill(List<Entry> entries, string error)
        {
            shown = entries;
            List<Entry> sorted = new List<Entry>(entries);
            if (sortColumn >= 0)
            {
                int column = sortColumn;
                bool down = sortDown;
                sorted.Sort(delegate(Entry a, Entry b)
                {
                    if (a.IsDir != b.IsDir) return a.IsDir ? -1 : 1;
                    int c;
                    if (column == 1) c = a.Size.CompareTo(b.Size);
                    else if (column == 2) c = string.Compare(a.Type, b.Type, StringComparison.OrdinalIgnoreCase);
                    else if (column == 3) c = a.Modified.CompareTo(b.Modified);
                    else c = 0;
                    if (c == 0) c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                    return down ? -c : c;
                });
            }
            list.BeginUpdate();
            list.Items.Clear();
            foreach (Entry e in sorted)
            {
                ListViewItem item = new ListViewItem(e.Name, e.Icon);
                item.Tag = e;
                item.SubItems.Add(e.IsDir ? "" : Theme.Size(e.Size));
                item.SubItems.Add(e.Type);
                item.SubItems.Add(e.Modified.Year > 1980 ? e.Modified.ToString("yyyy-MM-dd HH:mm") : "");
                list.Items.Add(item);
            }
            list.EndUpdate();
            Theme.FillColumn(list, 0);
            if (error != null) SetMessage(error, true); else ShowCount();
        }

        /// <summary>Reads the drives again for the tree (a USB drive plugged in since).</summary>
        public void LoadPlaces()
        {
            FileSource source = Source;
            Task.Factory.StartNew(delegate
            {
                List<Entry> found;
                try { found = source.Places(); }
                catch (Exception) { return; }
                Post(delegate { SetPlaces(found); });
            });
        }

        void SetPlaces(List<Entry> places)
        {
            HashSet<string> open = new HashSet<string>();
            foreach (TreeNode node in rootNode.Nodes)
                if (node.IsExpanded) open.Add((string)node.Tag);
            selecting = true;
            tree.BeginUpdate();
            rootNode.Nodes.Clear();
            foreach (Entry e in places)
            {
                TreeNode node = FolderNode(e);
                rootNode.Nodes.Add(node);
            }
            rootNode.Expand();
            tree.EndUpdate();
            selecting = false;
            foreach (TreeNode node in rootNode.Nodes)
                if (open.Contains((string)node.Tag)) node.Expand();
        }

        static TreeNode FolderNode(Entry e)
        {
            TreeNode node = new TreeNode(e.Name, e.Icon, e.Icon);
            node.Tag = e.Path;
            node.Nodes.Add(new TreeNode("...") { Name = "pending" });
            return node;
        }

        /// <summary>A tree node opened for the first time: its folders are read then.</summary>
        void Expand(TreeNode node)
        {
            if (node.Nodes.Count != 1 || node.Nodes[0].Name != "pending") return;
            string path = (string)node.Tag;
            FileSource source = Source;
            Task.Factory.StartNew(delegate
            {
                List<Entry> found;
                try { found = source.ListFolder(path); }
                catch (Exception) { found = new List<Entry>(); }
                found.RemoveAll(delegate(Entry e) { return !e.IsDir; });
                found.Sort(delegate(Entry a, Entry b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
                Post(delegate
                {
                    tree.BeginUpdate();
                    node.Nodes.Clear();
                    foreach (Entry e in found) node.Nodes.Add(FolderNode(e));
                    tree.EndUpdate();
                });
            });
        }
    }
}
