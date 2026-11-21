// SPDX-License-Identifier: LGPL-2.1-or-later
// Prospero Sender Lite: the payload queue. Any number of .elf / .bin payloads, sent to the
// PS5's ELF loader one after another. After each, the queue waits that payload's own
// "wait after" time, then checks the PS5 still answers before sending the next (two
// payloads landing at once can crash the console; Turbo's default gap is 10 s).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;

namespace PSLite
{
    class PayloadItem
    {
        public string Path;
        public int Wait = PayloadsForm.DefaultWait;      // seconds after this one, before the next
        public bool On = true;
        public string Status = "";
    }

    class PayloadsForm : Form
    {
        public const int DefaultWait = 10;
        readonly Settings settings;
        readonly Func<string> host;
        readonly Func<int> port;
        readonly Action<string> log;
        readonly List<PayloadItem> items = new List<PayloadItem>();
        readonly ListView list = new ListView();
        readonly NumericUpDown waitBox = new NumericUpDown();
        readonly Label note = new Label();
        Button addButton, removeButton, upButton, downButton, injectButton, stopButton;
        volatile bool stop;
        bool running, filling;

        public PayloadsForm(Settings settings, Func<string> host, Func<int> port, Action<string> log)
        {
            this.settings = settings;
            this.host = host;
            this.port = port;
            this.log = log;
            Text = "Payloads";
            BackColor = Theme.Panel;
            ForeColor = Theme.Text;
            Font = Theme.Ui;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            ClientSize = new Size(Theme.S(780), Theme.S(430));
            MinimumSize = new Size(Theme.S(640), Theme.S(320));

            note.Dock = DockStyle.Top;
            note.Height = Theme.S(44);
            note.ForeColor = Theme.Dim;
            note.Font = Theme.Small;
            note.Padding = new Padding(Theme.S(10), Theme.S(6), Theme.S(10), 0);
            note.Text = "Ticked payloads go to the PS5's ELF loader from top to bottom. After each one the queue waits its own " +
                        "\"Wait after\" time, then checks the PS5 still answers before the next. The loader port is the one in the main window.";

            Theme.StyleList(list);
            list.Dock = DockStyle.Fill;
            list.CheckBoxes = true;
            list.MultiSelect = false;
            list.Columns.Add("Payload", Theme.S(360));
            list.Columns.Add("Wait after", Theme.S(86), HorizontalAlignment.Right);
            list.Columns.Add("Status", Theme.S(280));
            list.Resize += delegate { Theme.FillColumn(list, 2); };
            list.ItemChecked += delegate(object sender, ItemCheckedEventArgs e)
            {
                PayloadItem item = e.Item.Tag as PayloadItem;
                if (filling || item == null) return;
                item.On = e.Item.Checked;
                Save();
            };
            list.SelectedIndexChanged += delegate
            {
                PayloadItem item = Picked();
                if (item == null) return;
                filling = true;
                waitBox.Value = Math.Max(waitBox.Minimum, Math.Min(waitBox.Maximum, item.Wait));
                filling = false;
            };
            list.AllowDrop = true;
            list.DragEnter += delegate(object sender, DragEventArgs e)
            {
                e.Effect = !running && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            };
            list.DragDrop += delegate(object sender, DragEventArgs e)
            {
                string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null) AddFiles(files);
            };

            FlowLayoutPanel bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Bottom;
            bar.Height = Theme.S(44);
            bar.WrapContents = false;
            bar.BackColor = Theme.Panel2;
            bar.Padding = new Padding(Theme.S(6), Theme.S(5), 0, 0);
            addButton = Theme.MakeButton("Add...", false);
            removeButton = Theme.MakeButton("Remove", false);
            upButton = Theme.MakeButton("Move up", false);
            downButton = Theme.MakeButton("Move down", false);
            injectButton = Theme.MakeButton("Inject ticked", true);
            stopButton = Theme.MakeButton("Stop", false);
            stopButton.Enabled = false;
            addButton.Click += delegate { Browse(); };
            removeButton.Click += delegate
            {
                PayloadItem item = Picked();
                if (item == null) return;
                items.Remove(item);
                Fill(null);
                Save();
            };
            upButton.Click += delegate { Shift(-1); };
            downButton.Click += delegate { Shift(1); };
            injectButton.Click += delegate { Start(); };
            stopButton.Click += delegate { stop = true; };
            waitBox.Minimum = 0;
            waitBox.Maximum = 600;
            waitBox.Value = DefaultWait;
            waitBox.Width = Theme.S(60);
            waitBox.BackColor = Theme.Bg;
            waitBox.ForeColor = Theme.Text;
            waitBox.BorderStyle = BorderStyle.FixedSingle;
            waitBox.Margin = new Padding(Theme.S(2), Theme.S(6), Theme.S(10), 0);
            waitBox.ValueChanged += delegate
            {
                PayloadItem item = Picked();
                if (filling || item == null) return;
                item.Wait = (int)waitBox.Value;
                Fill(item);
                Save();
            };
            bar.Controls.Add(addButton);
            bar.Controls.Add(removeButton);
            bar.Controls.Add(upButton);
            bar.Controls.Add(downButton);
            bar.Controls.Add(Theme.MakeLabel("Wait after selected (s)", Theme.Dim, Theme.Ui));
            bar.Controls.Add(waitBox);
            bar.Controls.Add(injectButton);
            bar.Controls.Add(stopButton);

            Controls.Add(list);
            Controls.Add(bar);
            Controls.Add(note);
            list.BringToFront();

            LoadItems();
            Fill(null);
            Shown += delegate { Theme.DarkTitle(this); Theme.FillColumn(list, 2); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                // closing only hides it: a running queue carries on, and the list is kept
                if (e.CloseReason != CloseReason.UserClosing) return;
                e.Cancel = true;
                Hide();
            };
        }

        PayloadItem Picked()
        {
            return list.SelectedItems.Count == 1 ? list.SelectedItems[0].Tag as PayloadItem : null;
        }

        void LoadItems()
        {
            foreach (string row in settings.Get("payloads", "").Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = row.Split(new char[] { '*' }, 3);
                if (parts.Length != 3) continue;
                PayloadItem item = new PayloadItem();
                item.On = parts[0] == "1";
                int wait;
                if (int.TryParse(parts[1], out wait) && wait >= 0 && wait <= 600) item.Wait = wait;
                item.Path = parts[2];
                items.Add(item);
            }
        }

        void Save()
        {
            List<string> rows = new List<string>();
            foreach (PayloadItem item in items) rows.Add((item.On ? "1" : "0") + "*" + item.Wait + "*" + item.Path);
            settings.Set("payloads", string.Join("|", rows.ToArray()));
            settings.Save();
        }

        void Fill(PayloadItem select)
        {
            filling = true;
            list.BeginUpdate();
            list.Items.Clear();
            foreach (PayloadItem item in items)
            {
                ListViewItem row = new ListViewItem(System.IO.Path.GetFileName(item.Path));
                row.Tag = item;
                row.Checked = item.On;
                row.ToolTipText = item.Path;
                row.SubItems.Add(item.Wait + " s");
                row.SubItems.Add(item.Status);
                list.Items.Add(row);
                if (item == select) row.Selected = true;
            }
            list.EndUpdate();
            filling = false;
        }

        void SetStatus(PayloadItem item, string text)
        {
            item.Status = text;
            foreach (ListViewItem row in list.Items)
                if (row.Tag == item && row.SubItems[2].Text != text) row.SubItems[2].Text = text;
        }

        void Browse()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "Add payloads";
                dialog.Filter = "Payloads (*.elf;*.bin)|*.elf;*.bin|All files (*.*)|*.*";
                dialog.Multiselect = true;
                if (dialog.ShowDialog(this) == DialogResult.OK) AddFiles(dialog.FileNames);
            }
        }

        void AddFiles(string[] files)
        {
            if (running) return;
            foreach (string file in files)
            {
                if (!File.Exists(file)) continue;
                PayloadItem item = new PayloadItem();
                item.Path = file;
                item.Wait = (int)waitBox.Value;
                items.Add(item);
            }
            Fill(null);
            Save();
        }

        void Shift(int by)
        {
            PayloadItem item = Picked();
            if (item == null || running) return;
            int at = items.IndexOf(item), to = at + by;
            if (to < 0 || to >= items.Count) return;
            items.RemoveAt(at);
            items.Insert(to, item);
            Fill(item);
            Save();
        }

        void Busy(bool on)
        {
            running = on;
            addButton.Enabled = removeButton.Enabled = upButton.Enabled = downButton.Enabled = injectButton.Enabled = !on;
            stopButton.Enabled = on;
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

        void Start()
        {
            if (running) return;
            string address = host();
            int loader = port();
            System.Net.IPAddress parsed;
            if (!System.Net.IPAddress.TryParse(address, out parsed))
            {
                MessageBox.Show(this, "Type the PS5's IP address in the main window first.", "Payloads", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            List<PayloadItem> queue = new List<PayloadItem>();
            foreach (PayloadItem item in items)
            {
                item.Status = "";
                if (item.On) queue.Add(item);
            }
            Fill(Picked());
            if (queue.Count == 0)
            {
                MessageBox.Show(this, "Tick at least one payload.", "Payloads", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            for (int i = 0; i < queue.Count; i++) SetStatus(queue[i], "queued (" + (i + 1) + " of " + queue.Count + ")");
            stop = false;
            Busy(true);
            Thread t = new Thread(delegate() { Run(address, loader, queue); });
            t.IsBackground = true;
            t.Name = "payloads";
            t.Start();
        }

        void Say(PayloadItem item, string text)
        {
            Post(delegate { SetStatus(item, text); });
        }

        void Run(string address, int loader, List<PayloadItem> queue)
        {
            int sent = 0;
            string ended = null;
            for (int i = 0; i < queue.Count && ended == null; i++)
            {
                PayloadItem item = queue[i];
                string name = System.IO.Path.GetFileName(item.Path);
                if (stop) { ended = "stopped"; break; }
                try
                {
                    byte[] data = File.ReadAllBytes(item.Path);
                    Say(item, "sending to " + address + ":" + loader + "...");
                    Proto.SendPayload(address, loader, data);
                    sent++;
                    Say(item, "sent (" + Theme.Size(data.Length) + ")");
                    log("payload " + name + " sent to " + address + ":" + loader + " (" + data.Length + " bytes)");
                }
                catch (Exception e)
                {
                    Say(item, "FAILED: " + e.Message);
                    log("payload " + name + " was not sent: the ELF loader at " + address + ":" + loader + " did not take it (" + e.Message + ")");
                    ended = "the ELF loader did not take " + name;
                    break;
                }
                if (i == queue.Count - 1) break;
                for (int left = item.Wait * 10; left > 0 && !stop; left--)
                {
                    if (left % 10 == 0) Say(item, "sent; next payload in " + left / 10 + " s");
                    Thread.Sleep(100);
                }
                if (stop) { ended = "stopped"; break; }
                Say(item, "sent; checking the PS5 still answers...");
                if (!Answers(address))
                {
                    Say(item, "sent, then the PS5 stopped answering");
                    ended = "the PS5 stopped answering after " + name + " (it may have crashed)";
                    break;
                }
                Say(item, "sent");
            }
            int total = queue.Count;
            string result = ended == null ? "all " + total + " payload(s) sent" : ended + ": " + sent + " of " + total + " sent";
            log("payloads: " + result);
            Post(delegate
            {
                foreach (PayloadItem item in queue)
                    if (item.Status.StartsWith("queued")) SetStatus(item, "not sent");
                Busy(false);
            });
        }

        /// <summary>Whether the PS5's network still answers within 30 s: a taken or a refused
        /// connection both mean it is up. It knocks on the receiver's port, never the loader's,
        /// which would read the knock as a payload.</summary>
        bool Answers(string address)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < until && !stop)
            {
                try
                {
                    Proto.Close(Proto.Connect(null, address, Proto.Port, 3000));
                    return true;
                }
                catch (SocketException e)
                {
                    if (e.SocketErrorCode == SocketError.ConnectionRefused) return true;
                }
                catch (Exception) { }
                Thread.Sleep(2000);
            }
            return stop;
        }
    }
}
