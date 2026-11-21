// SPDX-License-Identifier: LGPL-2.1-or-later
// Prospero Sender Lite: the window. This PC on the left, the PS5 on the right, the
// transfer queue and the per-link speed graph underneath.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PSLite
{
    class Settings
    {
        readonly Dictionary<string, string> values = new Dictionary<string, string>();
        public static string Folder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "prospero-sender-lite"); }
        }
        static string FilePath { get { return Path.Combine(Folder, "settings.ini"); } }

        public void Load()
        {
            try
            {
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) values[line.Substring(0, eq)] = line.Substring(eq + 1);
                }
            }
            catch (Exception) { }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                List<string> lines = new List<string>();
                foreach (KeyValuePair<string, string> pair in values) lines.Add(pair.Key + "=" + pair.Value);
                lines.Sort(StringComparer.Ordinal);
                File.WriteAllLines(FilePath, lines.ToArray());
            }
            catch (Exception) { }
        }

        public string Get(string key, string fallback)
        {
            string v;
            return values.TryGetValue(key, out v) ? v : fallback;
        }

        public bool GetBool(string key, bool fallback) { return Get(key, fallback ? "1" : "0") == "1"; }
        public void Set(string key, string value) { values[key] = value; }
        public void SetBool(string key, bool value) { values[key] = value ? "1" : "0"; }
        public bool Has(string key) { return values.ContainsKey(key); }

        /// <summary>The log: logs\lite-log.txt beside the exe (as Turbo keeps its own), or in the
        /// settings folder when that can't be written.</summary>
        public static string LogPath
        {
            get
            {
                try
                {
                    string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                    Directory.CreateDirectory(folder);
                    return Path.Combine(folder, "lite-log.txt");
                }
                catch (Exception)
                {
                    try { Directory.CreateDirectory(Folder); } catch (Exception) { }
                    return Path.Combine(Folder, "lite-log.txt");
                }
            }
        }
    }

    class Meter
    {
        public Series Line = new Series();
        readonly Queue<long[]> window = new Queue<long[]>();     // { timestamp, bytes }, the last second

        public float Push(long stamp, long bytes)
        {
            window.Enqueue(new long[] { stamp, bytes });
            while (window.Count > 5) window.Dequeue();
            long[] first = window.Peek();
            double seconds = (stamp - first[0]) / (double)Stopwatch.Frequency;
            if (seconds <= 0 || bytes < first[1]) return 0;
            return (float)((bytes - first[1]) / 1048576.0 / seconds);
        }
    }

    class MainForm : Form
    {
        readonly Engine eng = new Engine();
        readonly Settings settings = new Settings();
        readonly Dictionary<string, Meter> meters = new Dictionary<string, Meter>();
        readonly List<string> logLines = new List<string>();
        FilePane local, remote;
        ListView queueList, linkList;
        SpeedGraph graph;
        TextBox hostBox, loaderBox;
        Button connectButton, injectButton;
        CheckBox autoBox, lz4Box, ethBox, wifiBox;
        Label status, logLine;
        SplitContainer mainSplit, browserSplit, lowerSplit;
        System.Windows.Forms.Timer tick, standby;
        string[] clip;
        string lastStatus = "";
        PayloadsForm payloads;
        ReceiverForm receiverForm;
        NotifyIcon tray;
        CheckBox notifyBox;
        Button cancelButton, cancelAllButton, slipButton;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool FlashWindow(IntPtr hWnd, bool invert);
        bool clipRemote, connected, working, fillingLinks, placedSplits;
        int connectRound;

        public MainForm()
        {
            settings.Load();
            Text = Proto.AppName + " " + Proto.Version;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Ui;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(Theme.S(1480), Theme.S(900));
            MinimumSize = new Size(Theme.S(1000), Theme.S(640));
            ImageList icons = Theme.Icons();
            try
            {
                Bitmap bmp = new Bitmap(icons.Images[Theme.IcoConsole]);
                Icon = Icon.FromHandle(bmp.GetHicon());
            }
            catch (Exception) { }

            tray = new NotifyIcon();
            tray.Icon = Icon ?? SystemIcons.Application;
            tray.Text = Proto.AppName;
            tray.Visible = true;
            tray.Click += delegate
            {
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                Activate();
            };

            eng.Log = Log;
            eng.Finished = JobFinished;
            eng.Lz4On = settings.GetBool("lz4", true);
            int capMb;
            if (int.TryParse(settings.Get("inflight_mb", "700"), out capMb) && capMb >= 64 && capMb <= 8192) eng.InFlightCapMb = capMb;
            if (!settings.Has("inflight_mb")) settings.Set("inflight_mb", eng.InFlightCapMb.ToString());
            int hugeWriters;
            if (int.TryParse(settings.Get("huge_writers", "8"), out hugeWriters) && hugeWriters >= 1 && hugeWriters <= 16) eng.HugeWriters = hugeWriters;
            if (!settings.Has("huge_writers")) settings.Set("huge_writers", eng.HugeWriters.ToString());
            eng.EthernetToo = settings.GetBool("ethernet", false);
            eng.WifiToo = settings.GetBool("wifi", false);
            foreach (string ext in settings.Get("lz4skip", "").Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                eng.LzSkipExt.Add(ext);

            Controls.Add(BuildBody(icons));
            Controls.Add(BuildLogLine());
            Controls.Add(BuildTopBar());
            mainSplit.BringToFront();

            tick = new System.Windows.Forms.Timer();
            tick.Interval = 250;
            tick.Tick += delegate { OnTick(); };
            tick.Start();
            standby = new System.Windows.Forms.Timer();
            standby.Interval = 20000;
            standby.Tick += delegate { if (connected && !working && !eng.Busy) StartConnect(false, false, true); };
            standby.Start();

            Shown += delegate
            {
                Theme.DarkTitle(this);
                PlaceSplits();
                local.Navigate(settings.Has("localPath") && Directory.Exists(settings.Get("localPath", "")) ? settings.Get("localPath", "") : null);
                local.LoadPlaces();
                remote.SetMessage("Not connected. Type the PS5's IP address above and press Connect.", false);
                if (hostBox.Text.Trim().Length > 0) StartConnect(autoBox.Checked, false, false);
            };
            Resize += delegate { PlaceSplits(); };
            FormClosing += delegate
            {
                SaveSettings();
                try { tray.Visible = false; tray.Dispose(); } catch (Exception) { }
                try { UsbPipe.CloseAll(); } catch (Exception) { }
            };
        }

        // ---------------------------------------------------------------- layout

        Control BuildTopBar()
        {
            Panel bar = new Panel();
            bar.Dock = DockStyle.Top;
            bar.Height = Theme.S(46);
            bar.BackColor = Theme.Panel2;

            FlowLayoutPanel flow = new FlowLayoutPanel();
            flow.Dock = DockStyle.Fill;
            flow.WrapContents = false;
            flow.Padding = new Padding(Theme.S(10), Theme.S(6), 0, 0);
            flow.BackColor = Theme.Panel2;
            Label name = Theme.MakeLabel("PROSPERO SENDER LITE", Theme.Text, Theme.Title);
            name.Margin = new Padding(0, Theme.S(4), Theme.S(2), 0);
            flow.Controls.Add(name);
            Label version = Theme.MakeLabel(Proto.Version, Theme.Accent, Theme.UiBold);
            version.Margin = new Padding(0, Theme.S(10), Theme.S(18), 0);
            flow.Controls.Add(version);
            flow.Controls.Add(Theme.MakeLabel("PS5 IP address", Theme.Dim, Theme.Ui));
            hostBox = Theme.MakeBox(settings.Get("host", ""), 120);
            hostBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; StartConnect(autoBox.Checked, false, false); }
            };
            flow.Controls.Add(hostBox);
            flow.Controls.Add(Theme.MakeLabel("ELF loader port", Theme.Dim, Theme.Ui));
            loaderBox = Theme.MakeBox(settings.Get("loaderPort", Proto.LoaderPort.ToString()), 52);
            flow.Controls.Add(loaderBox);
            connectButton = Theme.MakeButton("Connect", true);
            connectButton.Click += delegate { StartConnect(autoBox.Checked, false, false); };
            flow.Controls.Add(connectButton);
            injectButton = Theme.MakeButton("Inject receiver", false);
            injectButton.Click += delegate { StartConnect(true, true, false); };
            flow.Controls.Add(injectButton);
            Button payloadsButton = Theme.MakeButton("Payloads...", false);
            payloadsButton.Click += delegate { ShowPayloads(); };
            flow.Controls.Add(payloadsButton);
            Button receiverButton = Theme.MakeButton("Receiver...", false);
            receiverButton.Click += delegate
            {
                if (receiverForm == null || receiverForm.IsDisposed)
                    receiverForm = new ReceiverForm(delegate { return hostBox.Text.Trim(); }, delegate { return eng.Busy; },
                                                    delegate { StartConnect(true, true, false); }, Log);
                if (!receiverForm.Visible) receiverForm.Show(this);
                receiverForm.Activate();
            };
            flow.Controls.Add(receiverButton);
            autoBox = Theme.MakeCheck("Inject automatically", settings.GetBool("autoInject", true));
            flow.Controls.Add(autoBox);

            status = new Label();
            status.Dock = DockStyle.Right;
            status.Width = Theme.S(420);
            status.TextAlign = ContentAlignment.MiddleRight;
            status.Font = Theme.UiBold;
            status.ForeColor = Theme.Dim;
            status.Text = "Not connected";
            status.Padding = new Padding(0, 0, Theme.S(14), 0);
            status.BackColor = Theme.Panel2;

            bar.Controls.Add(flow);
            bar.Controls.Add(status);
            flow.BringToFront();
            return bar;
        }

        Control BuildLogLine()
        {
            logLine = new Label();
            logLine.Dock = DockStyle.Bottom;
            logLine.Height = Theme.S(24);
            logLine.BackColor = Theme.Panel2;
            logLine.ForeColor = Theme.Dim;
            logLine.Font = Theme.Small;
            logLine.TextAlign = ContentAlignment.MiddleLeft;
            logLine.Padding = new Padding(Theme.S(10), 0, 0, 0);
            logLine.AutoEllipsis = true;
            logLine.Cursor = Cursors.Hand;
            logLine.Text = "Ready. Click this line to open the log.";
            logLine.Click += delegate
            {
                try { Process.Start(Settings.LogPath); } catch (Exception) { }
            };
            return logLine;
        }

        static SplitContainer MakeSplit(Orientation orientation)
        {
            SplitContainer s = new SplitContainer();
            s.Dock = DockStyle.Fill;
            s.Orientation = orientation;
            s.BackColor = Theme.Bg;
            s.SplitterWidth = Theme.S(5);
            s.Panel1.BackColor = Theme.Panel;
            s.Panel2.BackColor = Theme.Panel;
            return s;
        }

        Control BuildBody(ImageList icons)
        {
            mainSplit = MakeSplit(Orientation.Horizontal);
            browserSplit = MakeSplit(Orientation.Vertical);
            lowerSplit = MakeSplit(Orientation.Vertical);
            mainSplit.Panel1.Controls.Add(browserSplit);
            mainSplit.Panel2.Controls.Add(lowerSplit);

            local = new FilePane(new LocalSource(), icons);
            remote = new FilePane(new Ps5Source(delegate { return eng.Host; }), icons);
            local.Dock = DockStyle.Fill;
            remote.Dock = DockStyle.Fill;
            local.Command += OnCommand;
            remote.Command += OnCommand;
            remote.Dropped += delegate(FilePane pane, string[] files) { Upload(files, remote.Current); };
            browserSplit.Panel1.Controls.Add(local);
            browserSplit.Panel2.Controls.Add(remote);

            // the queue
            queueList = new ListView();
            Theme.StyleList(queueList);
            queueList.Dock = DockStyle.Fill;
            queueList.Columns.Add("Transfer", Theme.S(200));
            queueList.Columns.Add("To", Theme.S(170));
            queueList.Columns.Add("Size", Theme.S(80), HorizontalAlignment.Right);
            queueList.Columns.Add("Progress", Theme.S(150));
            queueList.Columns.Add("Status", Theme.S(240));
            queueList.Resize += delegate { Theme.FillColumn(queueList, 4); };
            FlowLayoutPanel queueBar = MakeBar("TRANSFERS");
            cancelButton = Theme.MakeButton("Cancel transfer", false);
            cancelButton.FlatAppearance.BorderColor = Theme.Bad;
            cancelButton.Click += delegate { CancelJobs(false); };
            cancelAllButton = Theme.MakeButton("Cancel all", false);
            cancelAllButton.Click += delegate { CancelJobs(true); };
            Button clear = Theme.MakeButton("Clear finished", false);
            clear.Click += delegate { eng.ClearFinished(); };
            notifyBox = Theme.MakeCheck("Notify when finished", settings.GetBool("notify", true));
            queueBar.Controls.Add(cancelButton);
            queueBar.Controls.Add(cancelAllButton);
            queueBar.Controls.Add(clear);
            queueBar.Controls.Add(notifyBox);
            ContextMenuStrip queueMenu = new ContextMenuStrip();
            queueMenu.Renderer = new ToolStripProfessionalRenderer(new DarkColors());
            queueMenu.BackColor = Theme.Panel2;
            queueMenu.ShowImageMargin = false;
            ToolStripMenuItem cancelItem = new ToolStripMenuItem("Cancel transfer");
            cancelItem.ForeColor = Theme.Text;
            cancelItem.ShortcutKeyDisplayString = "Del";
            cancelItem.Click += delegate { CancelJobs(false); };
            queueMenu.Items.Add(cancelItem);
            queueList.ContextMenuStrip = queueMenu;
            queueList.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Delete) { e.Handled = true; CancelJobs(false); }
            };
            lowerSplit.Panel1.Controls.Add(queueList);
            lowerSplit.Panel1.Controls.Add(queueBar);
            queueList.BringToFront();

            // the links
            graph = new SpeedGraph();
            graph.Dock = DockStyle.Fill;
            linkList = new ListView();
            Theme.StyleList(linkList);
            linkList.Dock = DockStyle.Bottom;
            linkList.Height = Theme.S(176);            // 0.16: room for 6 links; FitLinks sizes it to the rows
            linkList.CheckBoxes = true;
            linkList.MultiSelect = false;
            linkList.Columns.Add("Link", Theme.S(150));
            linkList.Columns.Add("Type", Theme.S(80));
            linkList.Columns.Add("PS5 address", Theme.S(96));
            linkList.Columns.Add("Typical", Theme.S(70), HorizontalAlignment.Right);
            linkList.Columns.Add("Conns", Theme.S(52), HorizontalAlignment.Right);
            linkList.Columns.Add("Now", Theme.S(76), HorizontalAlignment.Right);
            linkList.Columns.Add("Peak", Theme.S(76), HorizontalAlignment.Right);
            linkList.Columns.Add("Sent", Theme.S(78), HorizontalAlignment.Right);
            linkList.Columns.Add("State", Theme.S(200));
            linkList.Resize += delegate { Theme.FillColumn(linkList, 8); };
            linkList.ItemChecked += delegate(object sender, ItemCheckedEventArgs e)
            {
                Route r = e.Item.Tag as Route;
                if (fillingLinks || r == null) return;
                r.Enabled = e.Item.Checked;
                settings.SetBool("use." + r.Remote, r.Enabled);
            };
            ContextMenuStrip linkMenu = new ContextMenuStrip();
            linkMenu.Renderer = new ToolStripProfessionalRenderer(new DarkColors());
            linkMenu.BackColor = Theme.Panel2;
            linkMenu.ShowImageMargin = false;
            foreach (int count in new int[] { 1, 2, 4, 6, 8, 12, 16 })
            {
                int n = count;
                ToolStripMenuItem item = new ToolStripMenuItem(n + (n == 1 ? " connection" : " connections"));
                item.ForeColor = Theme.Text;
                item.Click += delegate
                {
                    if (linkList.SelectedItems.Count == 0) return;
                    Route r = linkList.SelectedItems[0].Tag as Route;
                    if (r == null) return;
                    r.Threads = n;
                    settings.Set("conns." + r.Remote, n.ToString());
                    Log(r.Name + ": " + n + " connections from the next send on");
                };
                linkMenu.Items.Add(item);
            }
            linkList.ContextMenuStrip = linkMenu;

            FlowLayoutPanel options = MakeBar("LINKS");
            options.Dock = DockStyle.Bottom;
            lz4Box = Theme.MakeCheck("LZ4 compression", eng.Lz4On);
            ethBox = Theme.MakeCheck("Also use PS5 Ethernet", eng.EthernetToo);
            wifiBox = Theme.MakeCheck("Also use PS5 Wi-Fi", eng.WifiToo);
            lz4Box.CheckedChanged += delegate { eng.Lz4On = lz4Box.Checked; };
            ethBox.CheckedChanged += delegate { eng.EthernetToo = ethBox.Checked; };
            wifiBox.CheckedChanged += delegate { eng.WifiToo = wifiBox.Checked; };
            options.Controls.Add(lz4Box);
            options.Controls.Add(ethBox);
            options.Controls.Add(wifiBox);
            Button dongles = Theme.MakeButton("Configure dongles", false);
            dongles.Click += delegate { ConfigureDongles(); };
            options.Controls.Add(dongles);
            slipButton = Theme.MakeButton("Quantum Slipstream", false);
            slipButton.Click += delegate { ActivateSlipstream(); };
            options.Controls.Add(slipButton);

            lowerSplit.Panel2.Controls.Add(graph);
            lowerSplit.Panel2.Controls.Add(options);
            lowerSplit.Panel2.Controls.Add(linkList);
            graph.BringToFront();
            return mainSplit;
        }

        static FlowLayoutPanel MakeBar(string caption)
        {
            FlowLayoutPanel bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Top;
            bar.Height = Theme.S(34);
            bar.WrapContents = false;
            bar.BackColor = Theme.Panel2;
            bar.Padding = new Padding(Theme.S(6), 0, 0, 0);
            Label label = Theme.MakeLabel(caption, Theme.Dim, Theme.UiBold);
            label.Margin = new Padding(Theme.S(2), Theme.S(9), Theme.S(8), 0);
            bar.Controls.Add(label);
            return bar;
        }

        void PlaceSplits()
        {
            if (placedSplits || mainSplit.Height < Theme.S(300) || mainSplit.Width < Theme.S(600)) return;
            placedSplits = true;
            try
            {
                mainSplit.SplitterDistance = (int)(mainSplit.Height * 0.56);
                browserSplit.SplitterDistance = browserSplit.Width / 2;
                lowerSplit.SplitterDistance = (int)(lowerSplit.Width * 0.44);
            }
            catch (Exception) { }
        }

        // ---------------------------------------------------------------- log and settings

        void Log(string text)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + text;
            lock (logLines)
            {
                logLines.Add(line);
                try { File.AppendAllText(Settings.LogPath, line + Environment.NewLine); }
                catch (Exception) { }
            }
            Post(delegate { logLine.Text = text; });
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

        void SaveSettings()
        {
            settings.Set("host", hostBox.Text.Trim());
            settings.Set("loaderPort", loaderBox.Text.Trim());
            settings.SetBool("autoInject", autoBox.Checked);
            settings.SetBool("lz4", lz4Box.Checked);
            settings.SetBool("ethernet", ethBox.Checked);
            settings.SetBool("wifi", wifiBox.Checked);
            settings.SetBool("notify", notifyBox.Checked);
            if (local.Current != null) settings.Set("localPath", local.Current);
            lock (eng.LzSkipExt) settings.Set("lz4skip", string.Join(",", new List<string>(eng.LzSkipExt).ToArray()));
            settings.Save();
        }

        void SetStatus(string text, Color color)
        {
            status.Text = text;
            status.ForeColor = color;
        }

        void Info(string text)
        {
            MessageBox.Show(this, text, Proto.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ---------------------------------------------------------------- connecting

        /// <summary>Checks the receiver, injects it when asked, and finds the links. quiet: the
        /// 20-second standby check, which changes nothing on screen unless something changed.</summary>
        void StartConnect(bool inject, bool force, bool quiet)
        {
            string host = hostBox.Text.Trim();
            IPAddress parsed;
            int loaderPort;
            if (!IPAddress.TryParse(host, out parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
            {
                if (!quiet) Info("Type the PS5's IP address first, for example 192.168.0.20.");
                return;
            }
            if (!int.TryParse(loaderBox.Text.Trim(), out loaderPort) || loaderPort < 1 || loaderPort > 65535) loaderPort = Proto.LoaderPort;
            if (working) return;
            if (force && eng.Busy)
            {
                Info("A transfer is running. Stop it before replacing the receiver.");
                return;
            }
            working = true;
            int round = ++connectRound;
            if (!quiet)
            {
                connectButton.Enabled = false;
                injectButton.Enabled = false;
                SetStatus(force ? "Injecting the receiver..." : "Connecting...", Theme.Warn);
                SaveSettings();
            }
            Task.Factory.StartNew(delegate { Connect(host, loaderPort, inject, force, quiet, round); });
        }

        void Connect(string host, int loaderPort, bool inject, bool force, bool quiet, int round)
        {
            int version;
            string line, note = "";
            bool up = Proto.Status(host, Proto.Port, 2500, out version, out line);
            if (force || (!up && inject))
            {
                try
                {
                    byte[] elf = Proto.ReceiverElf();
                    if (elf == null) throw new IOException("this exe carries no receiver");
                    if (up)
                    {
                        Log("asking receiver " + version + " to quit so the built-in receiver " + Proto.ReceiverVersion + " can take its place");
                        Quit(host);
                        for (int i = 0; i < 12 && Proto.Status(host, Proto.Port, 800, out version, out line); i++) Thread.Sleep(300);
                    }
                    Log("sending " + Proto.ReceiverResource + " to the ELF loader at " + host + ":" + loaderPort);
                    Proto.SendPayload(host, loaderPort, elf);
                    up = false;
                    for (int waited = 0; waited < Proto.StartupWaitMs && !up; waited += 500)
                    {
                        Thread.Sleep(500);
                        up = Proto.Status(host, Proto.Port, 1000, out version, out line);
                    }
                    if (up)
                    {
                        Log("receiver " + version + " started");
                        Thread.Sleep(2500);               // its USB adapter lanes come up a few seconds after it does
                        Proto.Status(host, Proto.Port, 1500, out version, out line);
                    }
                    else note = "The receiver was sent but doesn't answer: check the PS5's notifications.";
                }
                catch (Exception e)
                {
                    note = "The ELF loader at " + host + ":" + loaderPort + " did not take the receiver (" + e.Message + "). Is the loader running?";
                    up = Proto.Status(host, Proto.Port, 1500, out version, out line);
                }
                if (note.Length > 0) Log(note);
            }
            List<Route> routes = new List<Route>();
            if (up)
            {
                List<string> trace = new List<string>();
                try
                {
                    routes = Links.Detect(host, line, trace);
                    Links.Check(routes);
                    foreach (Route r in routes) trace.Add("link " + r.Name + (r.Up ? ": answers" : ": does not answer"));
                }
                catch (Exception e)
                {
                    trace.Add("the links could not be listed: " + e);
                }
                if (!quiet)
                {
                    foreach (string step in trace) Log("links: " + step);
                    string cpus = ReceiverConf.CpuLine(host);
                    if (cpus.Length > 0) Log(cpus);
                }
            }
            Post(delegate { Connected(host, up, version, line, routes, note, quiet, round); });
        }

        static void Quit(string host)
        {
            Socket s = null;
            try
            {
                s = Proto.Connect(null, host, Proto.Port, 2000);
                byte[] p = Proto.Packet(Proto.OpQuit, false, 0, 0, 0, 0, new byte[0]);
                Proto.SendAll(s, p, p.Length);
                Thread.Sleep(200);
            }
            catch (Exception) { }
            finally { Proto.Close(s); }
        }

        void Connected(string host, bool up, int version, string line, List<Route> routes, string note, bool quiet, int round)
        {
            working = false;
            connectButton.Enabled = true;
            injectButton.Enabled = true;
            if (round != connectRound) return;
            bool was = connected;
            if (eng.Busy && quiet) return;                // a send started meanwhile: its links stay as they are
            connected = up;
            lastStatus = up ? line : "";
            eng.Host = host;
            eng.ReceiverVersion = up ? version : 0;
            foreach (Route r in routes)
            {
                r.Stat = eng.StatFor(r.Remote);
                r.Enabled = settings.GetBool("use." + r.Remote, true);
                int chosen;
                if (int.TryParse(settings.Get("conns." + r.Remote, ""), out chosen) && chosen >= 1 && chosen <= 32) r.Threads = chosen;
            }
            lock (eng.Gate) eng.Routes = routes;
            FillLinks(routes);
            if (!up)
            {
                SetStatus("Receiver not running", Theme.Bad);
                remote.SetMessage(note.Length > 0 ? note : "The PS5's receiver doesn't answer at " + host + ". Press Inject receiver (the ELF loader must be running).", true);
                return;
            }
            int live = 0;
            foreach (Route r in routes) if (r.Up) live++;
            string text = "Receiver " + version + "  |  " + live + (live == 1 ? " link" : " links") + " up";
            if (version < Proto.ReceiverVersion) text += "  |  older than built-in " + Proto.ReceiverVersion;
            if (Slipstream.State(line) == "engaged") text += "  |  Slipstream engaged";
            SetStatus(text, version < Proto.ReceiverVersion ? Theme.Warn : Theme.Good);
            if (!quiet || !was)
            {
                Log("connected to receiver " + version + (line.Length > 0 ? ": " + line : ""));
                foreach (Route r in routes)
                    if (SmallFrames(r))
                        Log(r.Name + ": the PS5 is getting ~" + r.FrameAvg + "-byte frames from the PC, not " + r.Mtu + ". Press Configure dongles.");
                remote.Navigate(remote.Current);
                remote.LoadPlaces();
            }
        }

        void FillLinks(List<Route> routes)
        {
            fillingLinks = true;
            linkList.BeginUpdate();
            linkList.Items.Clear();
            foreach (Route r in routes)
            {
                Meter m = MeterFor(r);
                ListViewItem item = new ListViewItem(r.Name);
                item.Tag = r;
                item.Checked = r.Enabled;
                item.UseItemStyleForSubItems = false;
                item.ForeColor = m.Line.Color;
                item.Font = Theme.UiBold;
                for (int i = 0; i < 8; i++) item.SubItems.Add("");
                item.SubItems[1].Text = r.Kind;
                item.SubItems[2].Text = r.Usb ? "over the cable" : r.Remote;
                item.SubItems[3].Text = r.EstimateMb.ToString("0") + " MB/s";
                linkList.Items.Add(item);
            }
            linkList.EndUpdate();
            fillingLinks = false;
            FitLinks();
            Theme.FillColumn(linkList, 8);
        }

        /// <summary>0.16: the links table is as tall as its rows (6 at least, 8 at most), so every link
        /// shows without scrolling; the graph above it takes what is left.</summary>
        void FitLinks()
        {
            try
            {
                int row = linkList.Items.Count > 0 ? linkList.GetItemRect(0).Height : Theme.S(22);
                int rows = Math.Max(6, Math.Min(linkList.Items.Count, 8));
                int wanted = Theme.S(34) + rows * row + Theme.S(8);       // header, rows, a little air
                if (linkList.Height != wanted) linkList.Height = wanted;
            }
            catch (Exception) { }
        }

        Meter MeterFor(Route r)
        {
            Meter m;
            if (!meters.TryGetValue(r.Remote, out m))
            {
                m = new Meter();
                m.Line.Color = Theme.LinkColors[meters.Count % Theme.LinkColors.Length];
                meters[r.Remote] = m;
            }
            m.Line.Name = r.Name;
            return m;
        }

        // ---------------------------------------------------------------- Quantum Slipstream

        void ActivateSlipstream()
        {
            string host = hostBox.Text.Trim();
            System.Net.IPAddress parsed;
            if (!System.Net.IPAddress.TryParse(host, out parsed))
            {
                Info("Type the PS5's IP address first.");
                return;
            }
            slipButton.Enabled = false;
            string wasText = status.Text;
            Color wasColor = status.ForeColor;
            SetStatus("Activating Quantum Slipstream...", Theme.Warn);
            Task.Factory.StartNew(delegate
            {
                string message;
                bool ok;
                try { ok = Slipstream.Activate(host, Log, out message); }
                catch (Exception e)
                {
                    ok = false;
                    message = "Quantum Slipstream could not be activated: " + e.Message;
                }
                Log("slipstream: " + message);
                if (ok)
                {
                    // the install job has the pkg after a while: none is left for the next receiver start
                    Thread cleaner = new Thread(delegate()
                    {
                        Thread.Sleep(Slipstream.KeepSeconds * 1000);
                        Slipstream.Clear(host, Log);
                    });
                    cleaner.IsBackground = true;
                    cleaner.Start();
                }
                Post(delegate
                {
                    slipButton.Enabled = true;
                    SetStatus(wasText, wasColor);
                    MessageBox.Show(this, message, "Quantum Slipstream", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                    if (!working && !eng.Busy) StartConnect(false, false, true);
                });
            });
        }

        // ---------------------------------------------------------------- Configure dongles

        /// <summary>A jumbo lane whose frames reach the PS5 small: the PC port's Jumbo is off, or
        /// Windows' task offload is cutting the frames up.</summary>
        static bool SmallFrames(Route r)
        {
            return r.Lane && r.Mtu > 1500 && r.FrameAvg > 0 && r.FrameAvg < Dongles.SplitFrames;
        }

        void ConfigureDongles()
        {
            if (eng.Busy)
            {
                Info("A transfer is running. Stop it first: the ports restart and drop their links for a few seconds.");
                return;
            }
            string host = eng.Host, known = lastStatus, wasText = status.Text;
            Color wasColor = status.ForeColor;
            SetStatus("Checking the dongles...", Theme.Warn);
            Task.Factory.StartNew(delegate
            {
                int version;
                string line;
                if (host.Length == 0 || !Proto.Status(host, Proto.Port, 2000, out version, out line)) line = known;
                DonglePlan plan = null;
                string error = null;
                try { plan = Dongles.Plan(line); }
                catch (Exception e) { error = e.Message; }
                Post(delegate
                {
                    SetStatus(wasText, wasColor);
                    AskDongles(plan, error);
                });
            });
        }

        void AskDongles(DonglePlan plan, string error)
        {
            if (plan == null)
            {
                Info("The PC's network ports could not be read: " + error);
                return;
            }
            if (plan.Ports.Count == 0)
            {
                Info("No PC port on a Realtek 2.5 / 5 GbE lane was found.\n\nPlug the dongles' cables in and press Connect first: " +
                     "the PS5's receiver hands each PC port an address (10.0.2.x, 10.0.3.x or 10.0.4.x), and that is how the ports are found.");
                return;
            }
            StringBuilder text = new StringBuilder();
            text.AppendLine("Configure dongles changes these Windows settings, after one permission prompt:");
            text.AppendLine();
            text.AppendLine("  - PC-wide task offload (LSO): off");
            foreach (DonglePort port in plan.Ports)
            {
                text.AppendLine("  - " + port.Name + " (" + port.Ip + ", " + port.Lane + " lane):");
                text.AppendLine("        Jumbo Packet " + port.Jumbo + " bytes for " + port.Frame + "-byte frames" + (port.WasMtu > 0 ? " (the port's MTU is " + port.WasMtu + " now)" : ""));
                text.AppendLine("        Energy-Efficient Ethernet, green and power saving off; LSO off");
                if (port.Mac.Length > 0) text.AppendLine("        a new MAC address (it shares one with " + port.Why + ")");
                text.AppendLine("        the port restarted (its link drops for a few seconds)");
            }
            text.AppendLine();
            if (plan.Problems.Count == 0) text.AppendLine("Nothing looks wrong with them right now.");
            else
            {
                text.AppendLine("Found now:");
                foreach (string problem in plan.Problems) text.AppendLine("  - " + problem);
            }
            text.AppendLine();
            text.Append("Go ahead?");
            if (MessageBox.Show(this, text.ToString(), "Configure dongles", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            bool started;
            try { started = Dongles.Run(plan); }
            catch (Exception e)
            {
                Info("Configure dongles could not start: " + e.Message);
                return;
            }
            if (!started)
            {
                Info("Nothing was changed: Windows did not give permission.");
                return;
            }
            Log("Configure dongles started for " + plan.Ports.Count + " port(s)");
            SetStatus("Configuring the dongles...", Theme.Warn);
            Task.Factory.StartNew(delegate
            {
                string report = null;
                for (int i = 0; i < 180 && report == null; i++)                 // up to 90 s
                {
                    Thread.Sleep(500);
                    try
                    {
                        if (File.Exists(Dongles.ReportPath)) report = File.ReadAllText(Dongles.ReportPath).Trim();
                    }
                    catch (Exception) { }
                }
                if (report != null)
                    foreach (string row in report.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) Log("dongles: " + row);
                Thread.Sleep(8000);                                             // the ports come back and get their addresses
                Post(delegate
                {
                    Info(report != null ? "Configure dongles finished:\n\n" + report : "Configure dongles gave no report within 90 seconds. Check the ports in Windows.");
                    StartConnect(false, false, false);
                });
            });
        }

        // ---------------------------------------------------------------- the 4 Hz refresh

        static void SetCell(ListViewItem item, int column, string text)
        {
            if (item.SubItems[column].Text != text) item.SubItems[column].Text = text;
        }

        void OnTick()
        {
            long stamp = Stopwatch.GetTimestamp();
            List<Route> routes;
            lock (eng.Gate) routes = new List<Route>(eng.Routes);
            List<Series> lines = new List<Series>();
            float total = 0;
            foreach (Route r in routes)
            {
                Meter m = MeterFor(r);
                float rate = m.Push(stamp, Interlocked.Read(ref r.Stat.Bytes));
                Array.Copy(m.Line.Values, 1, m.Line.Values, 0, SpeedGraph.Points - 1);
                m.Line.Values[SpeedGraph.Points - 1] = rate;
                m.Line.Now = rate;
                if (rate > m.Line.Peak) m.Line.Peak = rate;
                total += rate;
                lines.Add(m.Line);
            }
            graph.Lines = lines;
            graph.TotalNow = total;
            if (total > graph.TotalPeak) graph.TotalPeak = total;

            Job current = null;
            List<Job> jobs;
            lock (eng.Gate) jobs = new List<Job>(eng.Jobs);
            foreach (Job j in jobs)
                if (j.State == "Sending" || j.State == "Scanning" || j.State == "Preparing") current = j;
            if (current != null && current.State == "Sending")
            {
                double seconds = (DateTime.Now - current.Started).TotalSeconds;
                long done = Interlocked.Read(ref current.DoneBytes), all = Interlocked.Read(ref current.TotalBytes);
                graph.Average = seconds > 0.5 ? (float)(done / 1048576.0 / seconds) : 0;
                double left = total > 1 ? (all - done) / 1048576.0 / total : double.NaN;
                graph.Caption = string.Format("{0}:  {1} of {2},  {3} left", current.Name, Theme.Size(done), Theme.Size(all), Theme.Time(left));
            }
            else if (current != null && current.State == "Preparing") graph.Caption = "Waiting for the PS5 to finish clearing deleted files before sending " + current.Name + "...";
            else if (current != null) graph.Caption = "Reading the folder list of " + current.Name + "...";
            else graph.Caption = connected ? "Idle. Drop files on the PS5 side, or copy and paste." : "";
            graph.Invalidate();

            foreach (ListViewItem item in linkList.Items)
            {
                Route r = item.Tag as Route;
                if (r == null) continue;
                Meter m = MeterFor(r);
                int open = Thread.VolatileRead(ref r.Stat.Connections);
                SetCell(item, 4, open > 0 ? open + " / " + r.Threads : r.Threads.ToString());
                SetCell(item, 5, m.Line.Now >= 0.05f ? m.Line.Now.ToString("0.0") + " MB/s" : "");
                SetCell(item, 6, m.Line.Peak >= 0.05f ? m.Line.Peak.ToString("0") + " MB/s" : "");
                long sent = Interlocked.Read(ref r.Stat.Bytes);
                SetCell(item, 7, sent > 0 ? Theme.Size(sent) : "");
                string state = !r.Up ? "not answering" : open > 0 ? "sending" : "ready";
                if (r.Mbit > 0) state += ", " + (r.Mbit >= 1000 ? (r.Mbit / 1000.0).ToString("0.#") + " Gbit/s line" : r.Mbit + " Mbit/s line");
                if (r.State.Length > 0 && r.Mbit == 0) state += ", " + r.State;
                bool small = SmallFrames(r);
                if (small) state = "small frames (" + r.FrameAvg + " of " + r.Mtu + " bytes): press Configure dongles";
                else if (r.Mtu > 0) state += ", " + r.Mtu + "-byte frames";
                SetCell(item, 8, state);
                Color stateColor = small ? Theme.Warn : !r.Up ? Theme.Bad : Theme.Text;
                if (item.SubItems[8].ForeColor != stateColor) item.SubItems[8].ForeColor = stateColor;
            }
            SyncQueue(jobs);
            bool unfinished = false;
            foreach (Job j in jobs) if (!j.Finished) unfinished = true;
            if (cancelButton.Enabled != unfinished) cancelButton.Enabled = unfinished;
            if (cancelAllButton.Enabled != unfinished) cancelAllButton.Enabled = unfinished;
        }

        void SyncQueue(List<Job> jobs)
        {
            bool same = queueList.Items.Count == jobs.Count;
            for (int i = 0; same && i < jobs.Count; i++) same = object.ReferenceEquals(queueList.Items[i].Tag, jobs[i]);
            if (!same)
            {
                queueList.BeginUpdate();
                queueList.Items.Clear();
                foreach (Job j in jobs)
                {
                    ListViewItem item = new ListViewItem(j.Name);
                    item.Tag = j;
                    item.UseItemStyleForSubItems = false;
                    for (int i = 0; i < 4; i++) item.SubItems.Add("");
                    item.SubItems[1].Text = j.RemoteDir;
                    queueList.Items.Add(item);
                }
                queueList.EndUpdate();
            }
            foreach (ListViewItem item in queueList.Items)
            {
                Job j = (Job)item.Tag;
                long done = Interlocked.Read(ref j.DoneBytes), all = Interlocked.Read(ref j.TotalBytes);
                SetCell(item, 2, j.State == "Queued" || j.State == "Scanning" ? "" : Theme.Size(all));
                string progress = "";
                if (j.State == "Sending" || j.Finished)
                    progress = (all > 0 ? (int)(done * 100 / all) : (j.State == "Done" ? 100 : 0)) + "%   " + j.FilesDone + " / " + j.Files + " files";
                SetCell(item, 3, progress);
                string state = j.State;
                if (j.State == "Sending") state = "Sending over " + j.LinksUsed;
                else if (j.State == "Preparing") state = "Waiting for the PS5 to finish clearing deleted files";
                else if (j.State == "Done") state = "Done in " + Theme.Time(j.Seconds) + ", " + (j.DoneBytes / 1048576.0 / Math.Max(j.Seconds, 0.001)).ToString("0") + " MB/s" + (j.Error.Length > 0 ? " (" + j.Error + ")" : "");
                else if (j.State == "Failed") state = "Failed: " + j.Error;
                else if (j.State == "Stopped") state = done > 0 ? "Cancelled at " + (all > 0 ? done * 100 / all : 0) + "% (what had arrived stays on the PS5)" : "Cancelled";
                SetCell(item, 4, state);
                Color color = j.State == "Failed" ? Theme.Bad : j.State == "Done" ? (j.Error.Length > 0 ? Theme.Warn : Theme.Good) : Theme.Text;
                if (item.SubItems[4].ForeColor != color) item.SubItems[4].ForeColor = color;
            }
        }

        void JobFinished(Job job)
        {
            Post(delegate
            {
                if (remote.Current != null && (remote.Current == job.RemoteDir || job.RemoteDir.StartsWith(remote.Current))) remote.Reload();
                if (job.State == "Done")
                {
                    string speed = (job.DoneBytes / 1048576.0 / Math.Max(job.Seconds, 0.001)).ToString("0") + " MB/s";
                    Notify(job.Name + " is on your PS5",
                           Theme.Size(job.DoneBytes) + " in " + Theme.Time(job.Seconds) + ", " + speed + " on average, to " + job.RemoteDir +
                           (job.Error.Length > 0 ? ". " + job.Error : ""), job.Error.Length > 0);
                }
                else if (job.State == "Failed")
                {
                    Notify("Transfer failed: " + job.Name, job.Error, true);
                }
            });
        }

        /// <summary>A Windows notification, a sound and a flashing taskbar button for a transfer
        /// that ended on its own (not one that was cancelled).</summary>
        void Notify(string title, string text, bool problem)
        {
            if (!notifyBox.Checked) return;
            try
            {
                if (text.Length > 250) text = text.Substring(0, 250);
                if (title.Length > 60) title = title.Substring(0, 60);
                tray.ShowBalloonTip(8000, title, text.Length > 0 ? text : title, problem ? ToolTipIcon.Warning : ToolTipIcon.Info);
                if (problem) System.Media.SystemSounds.Exclamation.Play(); else System.Media.SystemSounds.Asterisk.Play();
                if (Form.ActiveForm != this) FlashWindow(Handle, true);
            }
            catch (Exception) { }
        }

        /// <summary>Cancels the selected transfers, or with none selected the one running now
        /// (else the next queued). all: every transfer that hasn't finished.</summary>
        void CancelJobs(bool all)
        {
            List<Job> targets = new List<Job>();
            List<Job> jobs;
            lock (eng.Gate) jobs = new List<Job>(eng.Jobs);
            if (!all)
            {
                foreach (ListViewItem item in queueList.SelectedItems)
                {
                    Job j = item.Tag as Job;
                    if (j != null && !j.Finished) targets.Add(j);
                }
                if (targets.Count == 0)
                    foreach (Job j in jobs)
                        if (j.State == "Sending" || j.State == "Scanning") { targets.Add(j); break; }
                if (targets.Count == 0)
                    foreach (Job j in jobs)
                        if (!j.Finished) { targets.Add(j); break; }
            }
            else
            {
                foreach (Job j in jobs)
                    if (!j.Finished) targets.Add(j);
            }
            foreach (Job j in targets)
            {
                j.Cancel.Cancel();
                Log("cancelled: " + j.Name);
            }
        }

        void ShowPayloads()
        {
            if (payloads == null || payloads.IsDisposed)
            {
                payloads = new PayloadsForm(settings,
                    delegate { return hostBox.Text.Trim(); },
                    delegate
                    {
                        int loaderPort;
                        return int.TryParse(loaderBox.Text.Trim(), out loaderPort) && loaderPort >= 1 && loaderPort <= 65535 ? loaderPort : Proto.LoaderPort;
                    },
                    Log);
            }
            if (!payloads.Visible) payloads.Show(this);
            payloads.Activate();
        }

        // ---------------------------------------------------------------- file commands

        void Upload(string[] paths, string remoteDir)
        {
            if (paths == null || paths.Length == 0) return;
            if (!connected)
            {
                Info("Connect to the PS5 first.");
                return;
            }
            if (remoteDir == null)
            {
                Info("Open a drive or folder on the PS5 side first: that is where the files go.");
                return;
            }
            eng.Enqueue(paths, remoteDir);
        }

        void OnCommand(FilePane pane, string command)
        {
            bool onPs5 = pane.Source.Remote;
            List<Entry> picked = pane.Selected();
            if (command == "send")
            {
                Upload(pane.SelectedPaths(), remote.Current);
            }
            else if (command == "copy")
            {
                if (picked.Count == 0) return;
                clip = pane.SelectedPaths();
                clipRemote = onPs5;
                if (!onPs5)
                {
                    try
                    {
                        System.Collections.Specialized.StringCollection files = new System.Collections.Specialized.StringCollection();
                        files.AddRange(clip);
                        Clipboard.SetFileDropList(files);
                    }
                    catch (Exception) { }
                }
                Log(picked.Count + (picked.Count == 1 ? " item" : " items") + " copied");
            }
            else if (command == "paste")
            {
                if (onPs5)
                {
                    string[] sources = clip != null && !clipRemote ? clip : null;
                    if (clip != null && clipRemote)
                    {
                        Info("Copying from one PS5 folder to another needs receiver 86, which isn't built yet. Receiver " + Proto.ReceiverVersion + " only takes files from the PC.");
                        return;
                    }
                    try
                    {
                        if (sources == null && Clipboard.ContainsFileDropList())
                        {
                            System.Collections.Specialized.StringCollection files = Clipboard.GetFileDropList();
                            sources = new string[files.Count];
                            files.CopyTo(sources, 0);
                        }
                    }
                    catch (Exception) { }
                    if (sources != null) Upload(sources, pane.Current);
                }
                else if (clip != null && clipRemote)
                {
                    Info("Copying from the PS5 to the PC needs receiver 86, which isn't built yet. Receiver " + Proto.ReceiverVersion + " only takes files from the PC.");
                }
            }
            else if (command == "newfolder")
            {
                if (pane.Current == null) return;
                string name = Prompt("New folder", "Folder name", "New folder");
                if (name == null) return;
                string path = pane.Source.Combine(pane.Current, name);
                if (!onPs5)
                {
                    try { Directory.CreateDirectory(path); }
                    catch (Exception e) { Info("The folder could not be made: " + e.Message); }
                    pane.Reload();
                    return;
                }
                string host = eng.Host;
                Task.Factory.StartNew(delegate
                {
                    string error = null;
                    try
                    {
                        if (!Proto.MakeFolder(host, path)) error = "the PS5 refused (is the drive read-only?)";
                    }
                    catch (Exception e) { error = e.Message; }
                    Post(delegate
                    {
                        if (error != null) Info("The folder could not be made on the PS5: " + error);
                        pane.Reload();
                    });
                });
            }
            else if (command == "rename")
            {
                if (picked.Count != 1) return;
                if (onPs5)
                {
                    Info("Renaming on the PS5 needs receiver 86, which isn't built yet.");
                    return;
                }
                string name = Prompt("Rename", "New name", picked[0].Name);
                if (name == null || name == picked[0].Name) return;
                try
                {
                    string target = Path.Combine(Path.GetDirectoryName(picked[0].Path), name);
                    if (picked[0].IsDir) Directory.Move(picked[0].Path, target); else File.Move(picked[0].Path, target);
                }
                catch (Exception e) { Info("It could not be renamed: " + e.Message); }
                pane.Reload();
            }
            else if (command == "delete")
            {
                if (picked.Count == 0 || pane.Current == null) return;
                string what = picked.Count == 1 ? "\"" + picked[0].Name + "\"" : picked.Count + " items";
                string ask = onPs5
                    ? "Delete " + what + " from the PS5? This cannot be undone."
                    : "Move " + what + " to the Recycle Bin?";
                if (MessageBox.Show(this, ask, Proto.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                if (onPs5) DeleteOnPs5(pane, picked); else DeleteLocal(pane, picked);
            }
        }

        void DeleteLocal(FilePane pane, List<Entry> picked)
        {
            foreach (Entry e in picked)
            {
                try
                {
                    if (e.IsDir)
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(e.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    else
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(e.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
                catch (Exception error)
                {
                    Log("could not delete " + e.Path + ": " + error.Message);
                }
            }
            pane.Reload();
        }

        void DeleteOnPs5(FilePane pane, List<Entry> picked)
        {
            string host = eng.Host;
            Task.Factory.StartNew(delegate
            {
                List<string> refused = new List<string>();
                try
                {
                    using (Svc svc = new Svc(host))
                    {
                        foreach (Entry e in picked)
                        {
                            long files, bytes;
                            string message;
                            int result = svc.Delete(e.Path, out files, out bytes, out message);
                            if (result == 0) refused.Add(e.Name + ": " + (message.Length > 0 ? message : "not deleted"));
                            else Log("deleted " + e.Path + " from the PS5 (" + files + " files, " + Theme.Size(bytes) + ")");
                        }
                    }
                }
                catch (Exception error)
                {
                    refused.Add(error.Message);
                }
                Post(delegate
                {
                    if (refused.Count > 0)
                        Info("Not everything was deleted:\n\n" + string.Join("\n", refused.ToArray()) +
                             "\n\nThe receiver only deletes inside a drive's folders (for example /data/<folder>/<item> or /mnt/ext1/<folder>/<folder>/<item>), never a drive's top level.");
                    pane.Reload();
                });
            });
        }

        string Prompt(string caption, string label, string initial)
        {
            using (Form f = new Form())
            {
                f.Text = caption;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = false;
                f.MaximizeBox = false;
                f.ShowInTaskbar = false;
                f.BackColor = Theme.Panel;
                f.ForeColor = Theme.Text;
                f.Font = Theme.Ui;
                f.ClientSize = new Size(Theme.S(380), Theme.S(118));
                Label l = new Label();
                l.Text = label;
                l.AutoSize = true;
                l.Location = new Point(Theme.S(14), Theme.S(14));
                TextBox box = Theme.MakeBox(initial, 350);
                box.Location = new Point(Theme.S(14), Theme.S(38));
                Button ok = Theme.MakeButton("OK", true);
                Button cancel = Theme.MakeButton("Cancel", false);
                ok.AutoSize = false; cancel.AutoSize = false;
                ok.Size = new Size(Theme.S(84), Theme.S(28));
                cancel.Size = new Size(Theme.S(84), Theme.S(28));
                ok.Location = new Point(Theme.S(188), Theme.S(76));
                cancel.Location = new Point(Theme.S(280), Theme.S(76));
                ok.DialogResult = DialogResult.OK;
                cancel.DialogResult = DialogResult.Cancel;
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                f.Controls.Add(l);
                f.Controls.Add(box);
                f.Controls.Add(ok);
                f.Controls.Add(cancel);
                f.Shown += delegate { Theme.DarkTitle(f); box.SelectAll(); box.Focus(); };
                if (f.ShowDialog(this) != DialogResult.OK) return null;
                string name = box.Text.Trim();
                if (name.Length == 0 || name.IndexOfAny(new char[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) >= 0)
                {
                    Info("That name can't be used: leave out / \\ : * ? \" < > |");
                    return null;
                }
                return name;
            }
        }
    }
}
