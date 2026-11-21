// SPDX-License-Identifier: LGPL-2.1-or-later
// Prospero Sender Lite: colours, fonts, small drawn icons and text helpers.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PSLite
{
    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(0x12, 0x14, 0x18);
        public static readonly Color Panel = Color.FromArgb(0x1A, 0x1D, 0x23);
        public static readonly Color Panel2 = Color.FromArgb(0x23, 0x27, 0x2F);
        public static readonly Color Border = Color.FromArgb(0x30, 0x35, 0x40);
        public static readonly Color Text = Color.FromArgb(0xE6, 0xE9, 0xEE);
        public static readonly Color Dim = Color.FromArgb(0x8B, 0x93, 0xA1);
        public static readonly Color Accent = Color.FromArgb(0x3D, 0x8B, 0xFD);
        public static readonly Color Selection = Color.FromArgb(0x2F, 0x4A, 0x78);   // a selected row, focused or not
        public static readonly Color Good = Color.FromArgb(0x35, 0xC2, 0x8A);
        public static readonly Color Warn = Color.FromArgb(0xF2, 0xB3, 0x3D);
        public static readonly Color Bad = Color.FromArgb(0xF2, 0x54, 0x5B);
        public static readonly Color[] LinkColors = {
            Color.FromArgb(0x3D, 0x8B, 0xFD), Color.FromArgb(0x35, 0xC2, 0x8A), Color.FromArgb(0xF2, 0xB3, 0x3D),
            Color.FromArgb(0xC6, 0x78, 0xDD), Color.FromArgb(0x4D, 0xD0, 0xE1), Color.FromArgb(0xFF, 0x8A, 0x65),
            Color.FromArgb(0x9C, 0xCC, 0x65), Color.FromArgb(0xF2, 0x54, 0x5B) };

        public static float Scale = 1f;
        public static Font Ui, UiBold, Small, Title, Big, Mono;

        // icons in the image list
        public const int IcoFolder = 0, IcoFile = 1, IcoDrive = 2, IcoPc = 3, IcoConsole = 4, IcoSsd = 5, IcoUsb = 6;

        public static void Init()
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) Scale = g.DpiX / 96f;
            }
            catch (Exception) { Scale = 1f; }
            Ui = new Font("Segoe UI", 9f);
            UiBold = new Font("Segoe UI Semibold", 9f);
            Small = new Font("Segoe UI", 8f);
            Title = new Font("Segoe UI Semibold", 12f);
            Big = new Font("Segoe UI Light", 26f);
            Mono = new Font("Consolas", 9f);
        }

        public static int S(int px)
        {
            return (int)Math.Round(px * Scale);
        }

        public static string Size(long bytes)
        {
            if (bytes < 0) bytes = 0;
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1048576.0).ToString("0.#") + " MB";
            return (bytes / 1073741824.0).ToString("0.00") + " GB";
        }

        public static string Time(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "--";
            int s = (int)Math.Round(seconds);
            if (s < 60) return s + " s";
            if (s < 3600) return string.Format("{0}:{1:00}", s / 60, s % 60);
            return string.Format("{0}:{1:00}:{2:00}", s / 3600, s / 60 % 60, s % 60);
        }

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hWnd, string app, string idList);

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>Dark scroll bars and Explorer-style arrows and selection, where Windows has them.</summary>
        public static void Dark(Control c)
        {
            try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch (Exception) { }
        }

        public static void DarkTitle(Form f)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref on, 4);
            }
            catch (Exception) { }
        }

        public static Button MakeButton(string text, bool accent)
        {
            Button b = new Button();
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = accent ? Accent : Border;
            b.FlatAppearance.MouseOverBackColor = accent ? Color.FromArgb(0x5A, 0x9E, 0xFF) : Color.FromArgb(0x2D, 0x32, 0x3C);
            b.FlatAppearance.MouseDownBackColor = accent ? Color.FromArgb(0x2F, 0x73, 0xDD) : Border;
            b.BackColor = accent ? Accent : Panel2;
            b.ForeColor = accent ? Color.White : Text;
            b.Font = accent ? UiBold : Ui;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.Padding = new Padding(S(8), S(2), S(8), S(2));
            b.Margin = new Padding(S(4), S(3), S(4), S(3));
            b.UseVisualStyleBackColor = false;
            b.Cursor = Cursors.Hand;
            return b;
        }

        public static TextBox MakeBox(string text, int width)
        {
            TextBox t = new TextBox();
            t.Text = text;
            t.Width = S(width);
            t.BackColor = Bg;
            t.ForeColor = Text;
            t.BorderStyle = BorderStyle.FixedSingle;
            t.Font = Ui;
            t.Margin = new Padding(S(4), S(5), S(4), S(3));
            return t;
        }

        public static Label MakeLabel(string text, Color color, Font font)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.ForeColor = color;
            l.Font = font;
            l.BackColor = Color.Transparent;
            l.Margin = new Padding(S(6), S(8), S(2), S(3));
            return l;
        }

        public static CheckBox MakeCheck(string text, bool on)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.Checked = on;
            c.AutoSize = true;
            c.ForeColor = Text;
            c.Font = Ui;
            c.BackColor = Color.Transparent;
            c.Margin = new Padding(S(8), S(6), S(4), S(3));
            return c;
        }

        /// <summary>A details list in the app's colours, with its own header drawing.</summary>
        public static void StyleList(ListView list)
        {
            list.View = View.Details;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.BorderStyle = BorderStyle.None;
            list.BackColor = Panel;
            list.ForeColor = Text;
            list.Font = Ui;
            list.OwnerDraw = true;
            list.DrawColumnHeader += delegate(object sender, DrawListViewColumnHeaderEventArgs e)
            {
                using (SolidBrush b = new SolidBrush(Panel2)) e.Graphics.FillRectangle(b, e.Bounds);
                using (Pen p = new Pen(Border))
                {
                    e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                    e.Graphics.DrawLine(p, e.Bounds.Right - 1, e.Bounds.Top + 4, e.Bounds.Right - 1, e.Bounds.Bottom - 5);
                }
                TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                    (e.Header.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
                Rectangle r = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, Math.Max(1, e.Bounds.Width - 12), e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, Small, r, Dim, flags);
            };
            // 0.16: a selected row is drawn here, white on Selection, whether or not the list has the
            // focus (Windows drew an unfocused selection as dark text on dark grey)
            list.DrawItem += delegate(object sender, DrawListViewItemEventArgs e) { e.DrawDefault = !e.Item.Selected; };
            list.DrawSubItem += delegate(object sender, DrawListViewSubItemEventArgs e)
            {
                if (!e.Item.Selected)
                {
                    e.DrawDefault = true;
                    return;
                }
                ListView lv = (ListView)sender;
                Rectangle cell = new Rectangle(e.Bounds.X, e.Bounds.Y, e.Header.Width, e.Bounds.Height);
                using (SolidBrush fill = new SolidBrush(Selection)) e.Graphics.FillRectangle(fill, cell);
                int x = cell.X + S(6);
                if (e.ColumnIndex == 0)
                {
                    x = cell.X + S(4);
                    if (lv.CheckBoxes)
                    {
                        System.Windows.Forms.VisualStyles.CheckBoxState state = e.Item.Checked
                            ? System.Windows.Forms.VisualStyles.CheckBoxState.CheckedNormal
                            : System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal;
                        System.Drawing.Size glyph = CheckBoxRenderer.GetGlyphSize(e.Graphics, state);
                        CheckBoxRenderer.DrawCheckBox(e.Graphics, new Point(x, cell.Y + (cell.Height - glyph.Height) / 2), state);
                        x += glyph.Width + S(4);
                    }
                    if (lv.SmallImageList != null)
                    {
                        int index = e.Item.ImageIndex;
                        if (index < 0 && !string.IsNullOrEmpty(e.Item.ImageKey)) index = lv.SmallImageList.Images.IndexOfKey(e.Item.ImageKey);
                        if (index >= 0 && index < lv.SmallImageList.Images.Count)
                            lv.SmallImageList.Draw(e.Graphics, x, cell.Y + (cell.Height - lv.SmallImageList.ImageSize.Height) / 2, index);
                        x += lv.SmallImageList.ImageSize.Width + S(4);
                    }
                }
                Rectangle words = new Rectangle(x, cell.Y, Math.Max(1, cell.Right - x - S(6)), cell.Height);
                TextFormatFlags how = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
                    (e.Header.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, lv.Font, words, Color.White, how);
            };
            list.HandleCreated += delegate { Dark(list); };
        }

        /// <summary>0.16: a folder tree whose selected node stays white on Selection without the focus.</summary>
        public static void StyleTree(TreeView tree)
        {
            tree.DrawMode = TreeViewDrawMode.OwnerDrawText;
            tree.DrawNode += delegate(object sender, DrawTreeNodeEventArgs e)
            {
                if ((e.State & TreeNodeStates.Selected) == 0 || e.Bounds.Width <= 0 || e.Bounds.Height <= 0)
                {
                    e.DrawDefault = true;
                    return;
                }
                TreeView tv = (TreeView)sender;
                Rectangle row = new Rectangle(e.Bounds.X - 2, e.Bounds.Y, Math.Max(1, tv.ClientSize.Width - e.Bounds.X + 2), e.Bounds.Height);
                using (SolidBrush fill = new SolidBrush(Selection)) e.Graphics.FillRectangle(fill, row);
                Rectangle words = new Rectangle(e.Bounds.X + 1, e.Bounds.Y, Math.Max(1, row.Right - e.Bounds.X - 4), e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, e.Node.Text, tv.Font, words, Color.White,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            };
        }

        /// <summary>The last column takes whatever width the others leave, so no blank header strip shows.</summary>
        public static void FillColumn(ListView list, int column)
        {
            if (list.Columns.Count == 0) return;
            int used = 0;
            for (int i = 0; i < list.Columns.Count; i++)
                if (i != column) used += list.Columns[i].Width;
            int width = list.ClientSize.Width - used;
            if (width < S(120)) width = S(120);
            if (list.Columns[column].Width != width) list.Columns[column].Width = width;
        }

        static GraphicsPath Rounded(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>The small icons, drawn here so the exe carries no image files. A file named
        /// ps5.png beside the exe replaces the console icon.</summary>
        public static ImageList Icons()
        {
            int n = S(16);
            ImageList list = new ImageList();
            list.ColorDepth = ColorDepth.Depth32Bit;
            list.ImageSize = new System.Drawing.Size(n, n);
            for (int i = 0; i <= IcoUsb; i++)
            {
                Bitmap bmp = new Bitmap(n, n);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.ScaleTransform(n / 16f, n / 16f);
                    DrawIcon(g, i);
                }
                list.Images.Add(bmp);
            }
            try
            {
                string custom = Path.Combine(Application.StartupPath, "ps5.png");
                if (File.Exists(custom))
                    using (Image img = Image.FromFile(custom))
                        list.Images[IcoConsole] = new Bitmap(img, n, n);
            }
            catch (Exception) { }
            return list;
        }

        static void DrawIcon(Graphics g, int which)
        {
            Color folder = Color.FromArgb(0xF4, 0xC2, 0x4D), folderDark = Color.FromArgb(0xD9, 0x9E, 0x2B);
            Color metal = Color.FromArgb(0xB8, 0xC0, 0xCC), dark = Color.FromArgb(0x4A, 0x52, 0x60);
            if (which == IcoFolder)
            {
                using (SolidBrush b = new SolidBrush(folderDark)) using (GraphicsPath p = Rounded(new RectangleF(1, 3, 7, 4), 1)) g.FillPath(b, p);
                using (SolidBrush b = new SolidBrush(folder)) using (GraphicsPath p = Rounded(new RectangleF(1, 5, 14, 9), 1.5f)) g.FillPath(b, p);
            }
            else if (which == IcoFile)
            {
                using (SolidBrush b = new SolidBrush(metal)) using (GraphicsPath p = Rounded(new RectangleF(3, 1, 10, 14), 1.5f)) g.FillPath(b, p);
                using (Pen pen = new Pen(dark, 1f))
                {
                    g.DrawLine(pen, 5.5f, 6, 10.5f, 6);
                    g.DrawLine(pen, 5.5f, 8.5f, 10.5f, 8.5f);
                    g.DrawLine(pen, 5.5f, 11, 9, 11);
                }
            }
            else if (which == IcoDrive || which == IcoSsd)
            {
                using (SolidBrush b = new SolidBrush(metal)) using (GraphicsPath p = Rounded(new RectangleF(1, 5, 14, 7), 1.5f)) g.FillPath(b, p);
                using (SolidBrush b = new SolidBrush(which == IcoSsd ? Accent : Good)) g.FillEllipse(b, 11, 8, 2.4f, 2.4f);
                using (Pen pen = new Pen(dark, 1f)) g.DrawLine(pen, 3, 9.2f, which == IcoSsd ? 9 : 7, 9.2f);
            }
            else if (which == IcoPc)
            {
                using (SolidBrush b = new SolidBrush(Accent)) using (GraphicsPath p = Rounded(new RectangleF(1, 2, 14, 9), 1.5f)) g.FillPath(b, p);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(0xBF, 0xDC, 0xFF))) g.FillRectangle(b, 2.5f, 3.5f, 11, 6);
                using (SolidBrush b = new SolidBrush(metal))
                {
                    g.FillRectangle(b, 7, 11, 2, 2);
                    g.FillRectangle(b, 4.5f, 13, 7, 1.3f);
                }
            }
            else if (which == IcoConsole)
            {
                // a plain upright console: two light side plates around a dark core
                using (SolidBrush b = new SolidBrush(Color.FromArgb(0x1C, 0x1F, 0x26))) using (GraphicsPath p = Rounded(new RectangleF(6, 1.5f, 4, 13), 1)) g.FillPath(b, p);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(0xF2, 0xF4, 0xF7)))
                {
                    using (GraphicsPath p = Rounded(new RectangleF(3, 1, 3.6f, 14), 1.6f)) g.FillPath(b, p);
                    using (GraphicsPath p = Rounded(new RectangleF(9.4f, 1, 3.6f, 14), 1.6f)) g.FillPath(b, p);
                }
                using (SolidBrush b = new SolidBrush(Accent)) g.FillRectangle(b, 7.4f, 3, 1.2f, 6);
            }
            else if (which == IcoUsb)
            {
                using (SolidBrush b = new SolidBrush(metal)) using (GraphicsPath p = Rounded(new RectangleF(4, 5, 8, 10), 1.5f)) g.FillPath(b, p);
                using (SolidBrush b = new SolidBrush(dark)) g.FillRectangle(b, 5.5f, 1, 5, 4);
                using (SolidBrush b = new SolidBrush(metal))
                {
                    g.FillRectangle(b, 6.5f, 2.2f, 1, 1.4f);
                    g.FillRectangle(b, 8.5f, 2.2f, 1, 1.4f);
                }
            }
        }
    }
}
