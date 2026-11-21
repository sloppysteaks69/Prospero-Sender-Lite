// SPDX-License-Identifier: LGPL-2.1-or-later
// Prospero Sender Lite: the receiver's own settings on the PS5
// (/data/prospero-win/receiver.conf), read and written through its service port.
// Turbo and Lite share this file: it belongs to the receiver, not to a sender.
// "cpus" is read when the receiver starts, so saving it restarts the receiver.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PSLite
{
    static class ReceiverConf
    {
        public const string Path = "/data/prospero-win/receiver.conf";
        public const string Log = "/data/prospero-win/ax-link.log";      // the receiver's own log

        static bool IsKey(string line, string key)
        {
            string t = line.TrimStart();
            if (!t.StartsWith(key, StringComparison.Ordinal)) return false;
            string rest = t.Substring(key.Length).TrimStart();
            return rest.StartsWith("=");
        }

        /// <summary>text with key set to value (its line replaced, or one added); value null removes it.</summary>
        public static string With(string text, string key, string value)
        {
            List<string> kept = new List<string>();
            bool done = false;
            foreach (string raw in (text ?? "").Replace("\r", "").Split('\n'))
            {
                if (!IsKey(raw, key)) { kept.Add(raw); continue; }
                if (value != null && !done) kept.Add(key + "=" + value);
                done = true;
            }
            while (kept.Count > 0 && kept[kept.Count - 1].Trim().Length == 0) kept.RemoveAt(kept.Count - 1);
            if (value != null && !done) kept.Add(key + "=" + value);
            return kept.Count == 0 ? "" : string.Join("\n", kept.ToArray()) + "\n";
        }

        public static string Value(string text, string key)
        {
            foreach (string raw in (text ?? "").Replace("\r", "").Split('\n'))
                if (IsKey(raw, key)) return raw.Substring(raw.IndexOf('=') + 1).Trim();
            return null;
        }

        /// <summary>The receiver's line about the CPU cores it runs on, from its log: the newest
        /// "receiver: on N CPUs, mask ..." ("" when the log has none).</summary>
        public static string CpuLine(string host)
        {
            try
            {
                using (Svc svc = new Svc(host))
                {
                    byte[] tail = svc.Read(Log, 256 * 1024);
                    if (tail == null) return "";
                    string found = "";
                    foreach (string line in Encoding.UTF8.GetString(tail).Split('\n'))
                    {
                        int at = line.IndexOf("receiver: on ", StringComparison.Ordinal);
                        if (at >= 0 && line.IndexOf(" CPUs", at, StringComparison.Ordinal) > 0) found = line.Substring(at).Trim();
                    }
                    return found;
                }
            }
            catch (Exception)
            {
                return "";
            }
        }
    }

    class ReceiverForm : Form
    {
        readonly Func<string> host;
        readonly Func<bool> busy;
        readonly Action restart;
        readonly Action<string> log;
        readonly TextBox box = new TextBox();
        readonly Label cpuLine = new Label();
        readonly Label note = new Label();
        Button saveButton;

        public ReceiverForm(Func<string> host, Func<bool> busy, Action restart, Action<string> log)
        {
            this.host = host;
            this.busy = busy;
            this.restart = restart;
            this.log = log;
            Text = "Receiver settings";
            BackColor = Theme.Panel;
            ForeColor = Theme.Text;
            Font = Theme.Ui;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            ClientSize = new Size(Theme.S(720), Theme.S(440));
            MinimumSize = new Size(Theme.S(560), Theme.S(320));

            note.Dock = DockStyle.Top;
            note.Height = Theme.S(58);
            note.ForeColor = Theme.Dim;
            note.Font = Theme.Small;
            note.Padding = new Padding(Theme.S(10), Theme.S(6), Theme.S(10), 0);
            note.Text = "The receiver's own settings file on the PS5 (" + ReceiverConf.Path + "), shared by Turbo and Lite. " +
                        "cpus=root runs the receiver on every CPU core instead of the few the ELF loader gave it; it is read when the receiver starts, " +
                        "so saving restarts the receiver. Leave a \"survey\" line out: the CPU survey is a separate test.";

            cpuLine.Dock = DockStyle.Top;
            cpuLine.Height = Theme.S(40);
            cpuLine.ForeColor = Theme.Text;
            cpuLine.Font = Theme.Small;
            cpuLine.Padding = new Padding(Theme.S(10), Theme.S(4), Theme.S(10), 0);
            cpuLine.Text = "Reading from the PS5...";

            box.Multiline = true;
            box.AcceptsReturn = true;
            box.ScrollBars = ScrollBars.Vertical;
            box.Dock = DockStyle.Fill;
            box.BackColor = Theme.Bg;
            box.ForeColor = Theme.Text;
            box.BorderStyle = BorderStyle.FixedSingle;
            box.Font = Theme.Mono;
            box.Enabled = false;

            FlowLayoutPanel bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Bottom;
            bar.Height = Theme.S(44);
            bar.WrapContents = false;
            bar.BackColor = Theme.Panel2;
            bar.Padding = new Padding(Theme.S(6), Theme.S(5), 0, 0);
            Button all = Theme.MakeButton("All CPU cores", false);
            Button loader = Theme.MakeButton("Default cores", false);
            Button reload = Theme.MakeButton("Reload", false);
            saveButton = Theme.MakeButton("Save and restart receiver", true);
            saveButton.Enabled = false;
            all.Click += delegate { if (box.Enabled) box.Text = ReceiverConf.With(box.Text, "cpus", "root").Replace("\n", "\r\n"); };
            loader.Click += delegate { if (box.Enabled) box.Text = ReceiverConf.With(box.Text, "cpus", null).Replace("\n", "\r\n"); };
            reload.Click += delegate { LoadConf(); };
            saveButton.Click += delegate { Save(); };
            bar.Controls.Add(all);
            bar.Controls.Add(loader);
            bar.Controls.Add(reload);
            bar.Controls.Add(saveButton);

            Panel holder = new Panel();
            holder.Dock = DockStyle.Fill;
            holder.Padding = new Padding(Theme.S(10), Theme.S(4), Theme.S(10), Theme.S(8));
            holder.Controls.Add(box);
            Controls.Add(holder);
            Controls.Add(bar);
            Controls.Add(cpuLine);
            Controls.Add(note);
            holder.BringToFront();

            Shown += delegate { Theme.DarkTitle(this); LoadConf(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (e.CloseReason != CloseReason.UserClosing) return;
                e.Cancel = true;
                Hide();
            };
            VisibleChanged += delegate { if (Visible && IsHandleCreated && saveButton.Enabled) LoadConf(); };
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

        void LoadConf()
        {
            string address = host();
            box.Enabled = false;
            saveButton.Enabled = false;
            cpuLine.Text = "Reading from the PS5...";
            Task.Factory.StartNew(delegate
            {
                string text = null, error = null, cpus = "";
                try
                {
                    using (Svc svc = new Svc(address))
                    {
                        byte[] data = svc.Read(ReceiverConf.Path, 0);
                        text = data == null ? "" : Encoding.UTF8.GetString(data);
                    }
                    cpus = ReceiverConf.CpuLine(address);
                }
                catch (Exception e)
                {
                    error = e.Message;
                }
                Post(delegate
                {
                    if (error != null)
                    {
                        cpuLine.Text = "The receiver's settings could not be read: " + error + " Connect first.";
                        return;
                    }
                    box.Text = text.Replace("\r", "").Replace("\n", "\r\n");
                    box.Enabled = true;
                    saveButton.Enabled = true;
                    cpuLine.Text = cpus.Length > 0 ? "Now: " + cpus : "Now: the receiver's log has no line about its CPU cores.";
                });
            });
        }

        void Save()
        {
            if (busy())
            {
                MessageBox.Show(this, "A transfer is running. Stop it first: saving restarts the receiver.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string address = host();
            string text = box.Text.Replace("\r", "");
            if (text.Length > 0 && !text.EndsWith("\n")) text += "\n";
            saveButton.Enabled = false;
            Task.Factory.StartNew(delegate
            {
                string error = null;
                try
                {
                    using (Svc svc = new Svc(address))
                    {
                        if (!svc.Write(ReceiverConf.Path, Encoding.UTF8.GetBytes(text))) error = "the receiver refused the file";
                    }
                }
                catch (Exception e)
                {
                    error = e.Message;
                }
                Post(delegate
                {
                    saveButton.Enabled = true;
                    if (error != null)
                    {
                        MessageBox.Show(this, "receiver.conf was not saved: " + error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    string cpus = ReceiverConf.Value(text, "cpus");
                    log("receiver.conf saved (cpus=" + (cpus ?? "not set") + "); restarting the receiver");
                    Hide();
                    restart();
                });
            });
        }
    }
}
