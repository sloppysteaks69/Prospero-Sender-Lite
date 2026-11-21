// SPDX-License-Identifier: LGPL-2.1-or-later
// Prospero Sender Lite: the bonded sender. A job's files go to the PS5 in pieces, each
// written where it belongs (pwrite on the PS5), over every connection of every link at
// once. Large files belong to the fast links, one file a connection, in order; small
// files travel packed together in bundles over the slow links (see BondedSend). A
// connection that drops or stalls puts its piece back for another and reconnects.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace PSLite
{
    class Job
    {
        public int Id;
        public string[] Sources;        // files and folders on the PC
        public string RemoteDir;        // where they go on the PS5
        public string Name;
        public string State = "Queued"; // Queued, Scanning, Sending, Done, Failed, Stopped
        public string Error = "";
        public long TotalBytes, DoneBytes;
        public int Files, FilesDone, Errors;
        public DateTime Started;
        public double Seconds;
        public string LinksUsed = "";
        public CancellationTokenSource Cancel = new CancellationTokenSource();
        public bool Finished { get { return State == "Done" || State == "Failed" || State == "Stopped"; } }
    }

    class SendFile
    {
        public string Local, Remote, Ext;
        public long Size, Left;
        public int LzMiss;              // pieces that didn't shrink
        public bool LzHit;              // one did
    }

    class Piece
    {
        public byte Op;
        public byte[] Path;
        public string PathText;
        public byte[] Data;
        public int Len;
        public long Offset, Total;
        public long Credit;             // file bytes this piece lands (a bundle's: its files' sizes)
        public SendFile File;           // null for a bundle
        public int Members;             // files in a bundle
        public bool Pooled;
        public int Fails;
        public int WireBytes;           // header + data as it went out
    }

    class Engine
    {
        public string Host = "";
        public int ReceiverVersion;
        public bool Lz4On = true;
        public int HugeWriters = 8;           // 0.19: connections the PS5 lets write into one huge file at once (4 before; settings.ini: huge_writers, 1-16)
        public bool HugeSend;                 // this send is one huge file (the receiver takes 4 writers on it)
        public int InFlightCapMb = 700;       // 0.17: all links together keep at most this much unanswered (settings.ini: inflight_mb)
        public bool EthernetToo, WifiToo;
        public int ReadAheadMb = 512;
        public List<Route> Routes = new List<Route>();
        public readonly List<Job> Jobs = new List<Job>();
        public readonly object Gate = new object();
        public Action<string> Log = delegate { };
        public Action<Job> Finished = delegate { };
        public readonly HashSet<string> LzSkipExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, int[]> lzExt = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, LinkStat> stats = new Dictionary<string, LinkStat>();
        int nextId;
        Thread worker;
        readonly AutoResetEvent wake = new AutoResetEvent(false);

        // formats packed already: never worth LZ4 (pw_bonded.LZ4_SKIP_EXT)
        static readonly string[] Packed = {
            ".pak", ".ucas", ".utoc", ".bk2", ".bik", ".usm", ".mp4", ".webm", ".mkv", ".avi", ".wmv", ".mov",
            ".wem", ".bnk", ".fsb", ".bank", ".awb", ".acb", ".ogg", ".opus", ".mp3", ".xwma", ".flac",
            ".rpf", ".forge", ".cpk", ".rmdblob", ".arc", ".zip", ".7z", ".rar", ".gz", ".xz", ".bz2", ".zst",
            ".lz4", ".cab", ".png", ".jpg", ".jpeg", ".webp", ".ktx2", ".basis", ".pkg" };

        public Engine()
        {
            foreach (string ext in Packed) LzSkipExt.Add(ext);
        }

        /// <summary>A link's counters, kept by the PS5's address on it.</summary>
        public LinkStat StatFor(string remote)
        {
            lock (stats)
            {
                LinkStat s;
                if (!stats.TryGetValue(remote, out s))
                {
                    s = new LinkStat();
                    stats[remote] = s;
                }
                return s;
            }
        }

        public bool Busy
        {
            get
            {
                lock (Gate)
                    foreach (Job j in Jobs)
                        if (!j.Finished) return true;
                return false;
            }
        }

        public Job Enqueue(string[] sources, string remoteDir)
        {
            Job job = new Job();
            job.Sources = sources;
            job.RemoteDir = remoteDir;
            job.Name = sources.Length == 1 ? System.IO.Path.GetFileName(sources[0].TrimEnd('\\', '/')) : sources.Length + " items";
            if (string.IsNullOrEmpty(job.Name)) job.Name = sources[0];
            lock (Gate)
            {
                job.Id = ++nextId;
                Jobs.Add(job);
                if (worker == null)
                {
                    worker = new Thread(Work);
                    worker.IsBackground = true;
                    worker.Name = "jobs";
                    worker.Start();
                }
            }
            wake.Set();
            return job;
        }

        public void ClearFinished()
        {
            lock (Gate) Jobs.RemoveAll(delegate(Job j) { return j.Finished; });
        }

        void Work()
        {
            while (true)
            {
                Job job = null;
                lock (Gate)
                    foreach (Job j in Jobs)
                        if (j.State == "Queued") { job = j; break; }
                if (job == null)
                {
                    wake.WaitOne(1000);
                    continue;
                }
                job.Started = DateTime.Now;
                DateTime began = DateTime.UtcNow;
                try
                {
                    if (job.Cancel.IsCancellationRequested) throw new OperationCanceledException();
                    Run(job);
                    job.State = "Done";
                    if (job.Errors > 0) job.Error = job.Errors + " file(s) could not be read and were skipped";
                }
                catch (OperationCanceledException)
                {
                    job.State = "Stopped";
                }
                catch (Exception e)
                {
                    job.Error = e.Message;
                    job.State = "Failed";
                    Log("send failed: " + e.Message);
                }
                job.Seconds = (DateTime.UtcNow - began).TotalSeconds;
                if (job.State == "Done")
                    Log(string.Format("{0}: {1} in {2}, {3:0} MB/s on average", job.Name, Theme.Size(job.DoneBytes),
                                      Theme.Time(job.Seconds), job.DoneBytes / 1048576.0 / Math.Max(job.Seconds, 0.001)));
                try { Finished(job); } catch (Exception) { }
            }
        }

        public static string Join(string folder, string name)
        {
            return folder.EndsWith("/") ? folder + name : folder + "/" + name;
        }

        void Scan(Job job, List<string> dirs, List<SendFile> files)
        {
            foreach (string source in job.Sources)
            {
                job.Cancel.Token.ThrowIfCancellationRequested();
                string name = System.IO.Path.GetFileName(source.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(name)) name = source.Substring(0, 1);     // a whole drive: "D"
                if (Directory.Exists(source)) ScanFolder(job, source, Join(job.RemoteDir, name), dirs, files);
                else if (File.Exists(source)) AddFile(new FileInfo(source), Join(job.RemoteDir, name), files);
                else job.Errors++;
            }
        }

        void ScanFolder(Job job, string local, string remote, List<string> dirs, List<SendFile> files)
        {
            Stack<KeyValuePair<string, string>> todo = new Stack<KeyValuePair<string, string>>();
            todo.Push(new KeyValuePair<string, string>(local, remote));
            while (todo.Count > 0)
            {
                job.Cancel.Token.ThrowIfCancellationRequested();
                KeyValuePair<string, string> at = todo.Pop();
                dirs.Add(at.Value);
                try
                {
                    DirectoryInfo info = new DirectoryInfo(at.Key);
                    foreach (FileSystemInfo child in info.EnumerateFileSystemInfos())
                    {
                        bool folder = (child.Attributes & FileAttributes.Directory) != 0;
                        if (folder && (child.Attributes & FileAttributes.ReparsePoint) != 0) continue;   // a junction: not followed
                        if (folder) todo.Push(new KeyValuePair<string, string>(child.FullName, Join(at.Value, child.Name)));
                        else AddFile((FileInfo)child, Join(at.Value, child.Name), files);
                    }
                }
                catch (Exception e)
                {
                    job.Errors++;
                    Log("can't read " + at.Key + ": " + e.Message);
                }
            }
        }

        static void AddFile(FileInfo info, string remote, List<SendFile> files)
        {
            SendFile f = new SendFile();
            f.Local = info.FullName;
            f.Remote = remote;
            f.Size = info.Length;
            f.Left = f.Size;
            f.Ext = info.Extension;
            files.Add(f);
        }

        void Run(Job job)
        {
            job.State = "Scanning";
            List<string> dirs = new List<string>();
            List<SendFile> files = new List<SendFile>();
            Scan(job, dirs, files);
            long total = 0;
            foreach (SendFile f in files) total += f.Size;
            job.TotalBytes = total;
            job.Files = files.Count;
            // large files first, the biggest leading (the PS5's writers get going on them);
            // the small ones follow in folder order and travel in bundles
            files.Sort(delegate(SendFile a, SendFile b)
            {
                bool smallA = a.Size <= Proto.BundleFileMax, smallB = b.Size <= Proto.BundleFileMax;
                if (smallA != smallB) return smallA ? 1 : -1;
                if (!smallA && a.Size != b.Size) return b.Size.CompareTo(a.Size);
                return string.CompareOrdinal(a.Remote, b.Remote);
            });

            List<Route> all;
            lock (Gate) all = new List<Route>(Routes);
            if (all.Count == 0) throw new IOException("not connected to a PS5: press Connect first");
            Links.Check(all);
            List<Route> routes = Links.Choose(all, EthernetToo, WifiToo);
            if (routes.Count == 0)
                throw new IOException("no link to the PS5's receiver answers: press Connect (the receiver may need injecting)");
            List<string> names = new List<string>();
            foreach (Route r in routes) names.Add(r.Name + " x" + r.Threads);
            job.LinksUsed = string.Join(", ", names.ToArray());
            Log(string.Format("sending {0} ({1} files, {2}) over {3}", job.Name, files.Count, Theme.Size(total), job.LinksUsed));
            WaitForTrash(job);
            job.State = "Sending";
            WriteProfile(files, total);
            new BondedSend(this, job, routes).Run(dirs, files, ReceiverVersion >= Proto.BundleVersion);
        }

        /// <summary>The trash folder of the drive a PS5 path is on (where the receiver puts what it
        /// deletes, and empties in the background); null for a path on neither kind of drive.</summary>
        static string TrashFor(string remoteDir)
        {
            if (remoteDir.StartsWith("/mnt/"))
            {
                int slash = remoteDir.IndexOf('/', 5);
                return (slash < 0 ? remoteDir : remoteDir.Substring(0, slash)) + "/.pw-trash";
            }
            if (remoteDir == "/data" || remoteDir.StartsWith("/data/")) return "/data/.pw-trash";
            return null;
        }

        /// <summary>Holds a send while the PS5 is still clearing files deleted from the drive it goes
        /// to. A send started into that stalls on every link at once (measured: 45 s of 20-460 MB/s
        /// after a 104 GB delete, then 1,100), and the receiver makes new large files wait for the
        /// deleted ones' blocks anyway. Two minutes at most.</summary>
        void WaitForTrash(Job job)
        {
            string trash = TrashFor(job.RemoteDir);
            if (trash == null || ReceiverVersion < 67) return;
            DateTime until = DateTime.UtcNow.AddSeconds(120), moved = DateTime.UtcNow;
            bool said = false;
            int before = -1;
            while (DateTime.UtcNow < until)
            {
                job.Cancel.Token.ThrowIfCancellationRequested();
                int left = 0;
                try
                {
                    using (Svc svc = new Svc(Host))
                    {
                        List<RemoteEntry> inside;
                        if (svc.ListMany(new string[] { trash }).TryGetValue(trash, out inside) && inside != null) left = inside.Count;
                    }
                }
                catch (Exception)
                {
                    return;                               // the service port doesn't answer: nothing to wait on
                }
                if (left == 0)
                {
                    if (said) Log("the PS5 has finished clearing deleted files");
                    return;
                }
                if (!said)
                {
                    said = true;
                    job.State = "Preparing";
                    Log("waiting for the PS5 to finish clearing deleted files (" + left + " in its trash) before sending");
                }
                if (left != before)
                {
                    before = left;
                    moved = DateTime.UtcNow;
                }
                else if ((DateTime.UtcNow - moved).TotalSeconds > 45)
                {
                    Log("the PS5's trash hasn't changed for 45 s (" + left + " left in it): sending anyway");
                    return;
                }
                Thread.Sleep(1000);
            }
            Log("the PS5 is still clearing deleted files after 2 minutes: sending anyway");
        }

        /// <summary>The receiver's settings for this send, as Turbo's profiles set them (6.25), into
        /// receiver-profile.conf: a folder of files is written one writer a file, front to back
        /// (a piece waits up to 50 ms for the one before it); one huge file takes four writers
        /// with no waiting, since every fast link then works on it at once.</summary>
        void WriteProfile(List<SendFile> files, long total)
        {
            long biggest = 0;
            foreach (SendFile f in files) biggest = Math.Max(biggest, f.Size);
            bool huge = biggest >= (4L << 30) && biggest >= 0.5 * total;
            HugeSend = huge;
            if (ReceiverVersion < 82) return;
            string name = huge ? "one huge file" : "a folder of files";
            string keys = huge ? "file_writers = " + Math.Max(1, Math.Min(16, HugeWriters)) + "\nin_order_ms = 0\nshare_fd = 0\n" : "file_writers = 1\nin_order_ms = 50\nshare_fd = 1\n";
            string text = "# " + Proto.AppName + " " + Proto.Version + "'s profile for this send: " + name + "\n" +
                          "# written at each send's start; a key in receiver.conf wins over these\n" + keys;
            try
            {
                using (Svc svc = new Svc(Host))
                {
                    if (svc.Write("/data/prospero-win/receiver-profile.conf", Encoding.UTF8.GetBytes(text)))
                        Log("profile: " + name + " (" + keys.Trim().Replace("\n", ", ") + ")");
                    else Log("profile: the receiver didn't take receiver-profile.conf");
                }
            }
            catch (Exception e)
            {
                Log("profile: receiver-profile.conf not written (" + e.Message + ")");
            }
        }

        /// <summary>Whether a piece of this file is worth trying LZ4 on.</summary>
        public bool LzWorth(SendFile f)
        {
            if (!Lz4On) return false;
            if (f == null) return true;                   // a bundle of small files
            if (f.LzHit) return true;
            if (f.LzMiss >= 2) return false;
            lock (LzSkipExt) return !LzSkipExt.Contains(f.Ext ?? "");
        }

        /// <summary>What a try did: a file whose first two pieces didn't shrink isn't tried again,
        /// and an extension whose first three files all went that way is skipped from then on.</summary>
        public void LzResult(SendFile f, bool shrank)
        {
            if (f == null) return;
            if (shrank) { f.LzHit = true; }
            else if (!f.LzHit && Interlocked.Increment(ref f.LzMiss) != 2) return;
            else if (f.LzHit) return;
            string ext = f.Ext ?? "";
            if (ext.Length == 0) return;
            lock (LzSkipExt)
            {
                int[] counts;
                if (!lzExt.TryGetValue(ext, out counts))
                {
                    counts = new int[2];
                    lzExt[ext] = counts;
                }
                if (shrank) counts[1]++; else counts[0]++;
                if (counts[1] == 0 && counts[0] >= 3 && LzSkipExt.Add(ext))
                    Log("LZ4: " + ext + " files don't shrink: they go as they are from now on");
            }
        }
    }

    /// <summary>Every 5 seconds of a send, one log line that says where the time goes: the whole
    /// send's speed, how fast the PC read, how the large files stand, and for each link its speed, its
    /// pieces, how long a piece took to go onto the wire and how long the PS5 took to answer
    /// it. The answer is the PS5 writing the piece, so it is the PS5's side of the story.</summary>
    class Traffic
    {
        readonly List<Route> routes;
        readonly long[] bytes, pieces, send, answer;
        long last, lastRead;
        public const double EverySeconds = 5.0;

        public Traffic(List<Route> routes)
        {
            this.routes = routes;
            int n = routes.Count;
            bytes = new long[n]; pieces = new long[n]; send = new long[n]; answer = new long[n];
            last = Stopwatch.GetTimestamp();
            Take();
        }

        void Take()
        {
            for (int i = 0; i < routes.Count; i++)
            {
                LinkStat s = routes[i].Stat;
                bytes[i] = Interlocked.Read(ref s.Bytes);
                pieces[i] = Interlocked.Read(ref s.Pieces);
                send[i] = Interlocked.Read(ref s.SendTicks);
                answer[i] = Interlocked.Read(ref s.AnswerTicks);
            }
        }

        /// <summary>The line when 5 s have passed since the last one, else null.</summary>
        public string Line(string standing, long readSoFar)
        {
            long now = Stopwatch.GetTimestamp();
            double seconds = (now - last) / (double)Stopwatch.Frequency;
            if (seconds < EverySeconds) return null;
            long[] b0 = (long[])bytes.Clone(), p0 = (long[])pieces.Clone(), s0 = (long[])send.Clone(), a0 = (long[])answer.Clone();
            Take();
            double total = 0;
            List<string> parts = new List<string>();
            double ms = 1000.0 / Stopwatch.Frequency;
            for (int i = 0; i < routes.Count; i++)
            {
                long count = pieces[i] - p0[i];
                double rate = (bytes[i] - b0[i]) / 1048576.0 / seconds;
                total += rate;
                parts.Add(string.Format("{0} x{1}: {2:0} MB/s, {3} pieces, wire {4:0} ms, PS5 answer {5:0} ms",
                    routes[i].Name, routes[i].Threads, rate, count,
                    count > 0 ? (send[i] - s0[i]) * ms / count : 0, count > 0 ? (answer[i] - a0[i]) * ms / count : 0));
            }
            double read = (readSoFar - lastRead) / 1048576.0 / seconds;
            last = now;
            lastRead = readSoFar;
            return string.Format("traffic: {0:0} MB/s landed, {1:0} MB/s read, {2} | ", total, read, standing) + string.Join(" | ", parts.ToArray());
        }
    }

    /// <summary>One connection to the receiver: TCP on a network link, or the USB bridge cable.
    /// Send puts one packet (its header, then size bytes of data) on it and waits for the
    /// receiver's answer: true when the PS5 did what was asked.</summary>
    abstract class Channel
    {
        public long SendTicks, AnswerTicks;       // the last Send: on the wire, then waiting for the answer
        /// <summary>Puts one packet on the connection without waiting for its answer (0.12: a fast
        /// link keeps a second piece on its way while the PS5 writes the first). The token goes to
        /// Collect; answers come back in the order the packets were posted.</summary>
        public abstract object Post(byte[] head, byte[] data, int size);
        public abstract bool Collect(object token);
        public bool Send(byte[] head, byte[] data, int size) { return Collect(Post(head, data, size)); }
        public abstract void Close();

        public static Channel Open(Route route)
        {
            if (route.Usb) return new UsbChannel(route.UsbIndex);
            return new TcpChannel(Proto.Connect(route.Local, route.Remote, route.Port, Proto.ConnectTimeoutMs));
        }
    }

    class TcpChannel : Channel
    {
        readonly Socket socket;
        public TcpChannel(Socket socket) { this.socket = socket; }

        public override object Post(byte[] head, byte[] data, int size)
        {
            long began = Stopwatch.GetTimestamp();
            Proto.SendAll(socket, head, head.Length);
            if (size > 0) Proto.SendAll(socket, data, size);
            long sent = Stopwatch.GetTimestamp();
            SendTicks = sent - began;
            return sent;
        }

        public override bool Collect(object token)
        {
            bool ok = Proto.Answer(socket);
            AnswerTicks = Stopwatch.GetTimestamp() - (long)token;
            return ok;
        }

        public override void Close() { Proto.Close(socket); }
    }

    /// <summary>The cable is opened once and shared by every connection on its route, so closing
    /// one of them leaves it open.</summary>
    class UsbChannel : Channel
    {
        readonly UsbPipe pipe;
        public UsbChannel(int index) { pipe = UsbPipe.Ping(index); }
        public override object Post(byte[] head, byte[] data, int size)
        {
            return pipe.Post(head, data, size, out SendTicks);
        }

        public override bool Collect(object token)
        {
            return pipe.Collect(token, 0, out AnswerTicks);
        }
        public override void Close() { }
    }

    /// <summary>A large file on its way: the part not yet handed out is [Front, Back). The link that
    /// owns it takes pieces from the front, in order; a link that helps takes them from the back.</summary>
    class FileRun
    {
        public SendFile File;
        public Route Owner;
        public long Front, Back;
        public int Users;               // connections working on it now
        public int Helpers;             // ... of them helping from its end
        public bool Empty, EmptySent;   // a zero-length file: one empty piece makes it on the PS5
        public bool Failed;             // the PC could not read it: the rest is skipped
        public bool Started;            // a connection has begun it (files begin one after another, not all at once)
        public bool HelpSaid;           // its first helper was logged
        public readonly object Gate = new object();

        public long Left
        {
            get { lock (Gate) return Failed ? 0 : Empty ? (EmptySent ? 0 : 1) : Back - Front; }
        }
    }

    /// <summary>One job's send over the chosen links.
    ///
    /// Files are given to links by size (0.8), as Turbo does it:
    ///   - Large files belong to the fast links (the Realtek 2.5 / 5 / 10 GbE lanes and the USB
    ///     bridge cable). Each connection owns one large file and sends its pieces in order, so the
    ///     PS5 writes that file from start to end with one writer instead of many links scattering
    ///     pieces into it. The largest files go to the fastest link first.
    ///   - Small files travel in bundles, and the slow links (the PS5's own Ethernet, Wi-Fi, an
    ///     AX88179) take those. A slow link takes a large file, the smallest one left, only when
    ///     no small file is waiting.
    ///   - A fast link with no file of its own left helps the file with the most still to go, taking
    ///     pieces from its end, so the send never waits on one slow link's last file.</summary>
    class BondedSend
    {
        readonly Engine eng;
        readonly Job job;
        readonly List<Route> routes;
        readonly CancellationToken cancel;
        readonly ConcurrentQueue<Piece> again = new ConcurrentQueue<Piece>();        // pieces to send again, any link
        readonly ConcurrentQueue<Piece> againBig = new ConcurrentQueue<Piece>();     // ... larger than a slow link should carry
        readonly Dictionary<int, ConcurrentBag<byte[]>> pools = new Dictionary<int, ConcurrentBag<byte[]>>();
        int poolKeep = 8, fastConnected;
        readonly object pick = new object();              // guards the two lists below, runs and every run's Users
        // 0.10: the files that carry the bytes (MainMin and up) go first and on their own. Every new
        // file costs the PS5 an open (reserving its blocks, or renaming and trimming a spare), and a
        // burst of opens stalls its drive: 0.8 opened some 100 files in the first 15 s of a send and
        // crawled until they were made. So main files begin one every MainGap, and the mid-size and
        // small files wait until the main ones are all on their way (Turbo 6.41 found the same with
        // many small files: made alongside the large ones they held the M.2 to 125-330 MB/s).
        const long MainMin = 32L * 1024 * 1024;
        static readonly long MainGap = TimeSpan.TicksPerMillisecond * 250, MidGap = TimeSpan.TicksPerMillisecond * 30;
        List<SendFile> big = new List<SendFile>();        // main files, the largest first; [bigHead, bigTail] not yet given out
        int bigHead, bigTail = -1;
        List<SendFile> mid = new List<SendFile>();        // too large for a bundle, smaller than MainMin; the same way
        int midHead, midTail = -1;
        volatile bool tailOpen;                           // the main files are all on their way: mid-size files and bundles go now
        long lastStart;                                   // ticks: when a file last began
        readonly List<FileRun> runs = new List<FileRun>();
        BlockingCollection<Piece> small;                  // bundles of small files, read ahead
        List<SendFile> smallFiles = new List<SendFile>();
        readonly object bundleGate = new object();
        List<SendFile> bundleFiles = new List<SendFile>();
        List<byte[]> bundleData = new List<byte[]>();
        int bundleBytes;
        int nextSmall, readersLeft, outstanding, connected;
        long readBytes;                                   // read from the PC's drives so far
        volatile bool stop, smallDone;
        volatile string fatal;
        long lastUp;                                      // ticks: when a connection last was up
        long began;                                       // ticks: when the send started

        class Conn
        {
            public byte[] Scratch, Check, Tried;
            public int[] Table;
            public FileRun Run;                           // the large file this connection works on
            public bool Helping;                          // ... from its end, as a helper
            public FileStream Stream;
            public FileRun StreamRun;
        }

        public BondedSend(Engine eng, Job job, List<Route> routes)
        {
            this.eng = eng;
            this.job = job;
            this.routes = routes;
            cancel = job.Cancel.Token;
        }

        public void Run(List<string> dirs, List<SendFile> files, bool bundles)
        {
            MakeFolders(dirs);
            if (files.Count == 0) return;
            foreach (SendFile f in files)
            {
                if (bundles && f.Size <= Proto.BundleFileMax) smallFiles.Add(f);
                else if (f.Size >= MainMin) big.Add(f);
                else mid.Add(f);
            }
            Comparison<SendFile> largestFirst = delegate(SendFile a, SendFile b)
            {
                return a.Size != b.Size ? b.Size.CompareTo(a.Size) : string.CompareOrdinal(a.Remote, b.Remote);
            };
            big.Sort(largestFirst);
            mid.Sort(largestFirst);
            bigTail = big.Count - 1;
            midTail = mid.Count - 1;
            tailOpen = big.Count == 0;
            small = new BlockingCollection<Piece>(6);
            began = DateTime.UtcNow.Ticks;
            int threads = 0;
            foreach (Route r in routes)
            {
                threads += r.Threads;
                if (!pools.ContainsKey(r.PieceBytes)) pools[r.PieceBytes] = new ConcurrentBag<byte[]>();
            }
            poolKeep = Math.Max(8, threads);
            lastUp = DateTime.UtcNow.Ticks;

            // the largest files to the fastest links before anything starts
            List<Route> fast = new List<Route>();
            foreach (Route r in routes) if (r.Fast) fast.Add(r);
            fast.Sort(delegate(Route a, Route b) { return b.EstimateMb.CompareTo(a.EstimateMb); });
            lock (pick)
            {
                foreach (Route r in fast)
                    for (int i = 0; i < Math.Min(r.BigSlots, r.Threads) && bigHead <= bigTail; i++) NewRun(r, true, false);
            }

            List<Thread> running = new List<Thread>();
            int readers = smallFiles.Count == 0 ? 0 : Math.Min(4, smallFiles.Count);
            readersLeft = readers;
            if (readers == 0) smallDone = true;
            for (int i = 0; i < readers; i++)
            {
                Thread t = new Thread(SmallReader);
                t.IsBackground = true;
                t.Name = "small files";
                running.Add(t);
                t.Start();
            }
            foreach (Route r in routes)
            {
                for (int i = 0; i < r.Threads; i++)
                {
                    Route route = r;
                    Thread t = new Thread(delegate() { Connection(route); });
                    t.IsBackground = true;
                    t.Name = "link " + r.Name;
                    running.Add(t);
                    t.Start();
                }
            }
            Traffic traffic = new Traffic(routes);
            while (fatal == null && !cancel.IsCancellationRequested)
            {
                if (AllDone()) break;
                if (!tailOpen && MainGivenOut())
                {
                    tailOpen = true;
                    if (mid.Count + smallFiles.Count > 0)
                        eng.Log("the large files are all on their way: " + mid.Count + " mid-size and " + smallFiles.Count + " small files go now");
                }
                string line = traffic.Line(Where(), Interlocked.Read(ref readBytes));
                if (line != null) eng.Log(line);
                if (Thread.VolatileRead(ref connected) > 0) lastUp = DateTime.UtcNow.Ticks;
                else if (DateTime.UtcNow.Ticks - lastUp > TimeSpan.TicksPerSecond * 60)
                    fatal = "no link to the PS5 has answered for a minute";
                Thread.Sleep(50);
            }
            stop = true;
            try { small.CompleteAdding(); } catch (Exception) { }
            foreach (Thread t in running) t.Join(3000);
            if (cancel.IsCancellationRequested) throw new OperationCanceledException();
            if (fatal != null) throw new IOException(fatal);
        }

        /// <summary>Nothing is left to hand out and every piece handed out has landed.</summary>
        bool AllDone()
        {
            if (!tailOpen || !smallDone || small.Count > 0) return false;
            lock (pick)
            {
                if (bigHead <= bigTail || midHead <= midTail) return false;
                foreach (FileRun run in runs)
                    if (run.Left > 0) return false;
            }
            return Thread.VolatileRead(ref outstanding) == 0;
        }

        /// <summary>Every main file has been given out to its last piece.</summary>
        bool MainGivenOut()
        {
            lock (pick)
            {
                if (bigHead <= bigTail) return false;
                foreach (FileRun run in runs)
                    if (run.Left > 0) return false;
            }
            return true;
        }

        /// <summary>For the traffic line: how the large files stand.</summary>
        string Where()
        {
            int open = 0, waiting, later;
            lock (pick)
            {
                waiting = bigTail - bigHead + 1;
                later = midTail - midHead + 1;
                foreach (FileRun run in runs)
                    if (run.Left > 0 && run.Started) open++;
            }
            return string.Format("{0} large files open, {1} waiting, {2} mid-size later, {3} bundles ready", open, Math.Max(0, waiting), Math.Max(0, later), small.Count);
        }

        /// <summary>Folders first, over one connection, the shallow ones leading.</summary>
        void MakeFolders(List<string> dirs)
        {
            if (dirs.Count == 0) return;
            dirs.Sort(delegate(string a, string b)
            {
                int da = a.Split('/').Length, db = b.Split('/').Length;
                return da != db ? da.CompareTo(db) : string.CompareOrdinal(a, b);
            });
            Exception last = null;
            foreach (Route r in routes)
            {
                Channel s = null;
                string refused = null;
                try
                {
                    s = Channel.Open(r);
                    foreach (string dir in dirs)
                    {
                        cancel.ThrowIfCancellationRequested();
                        byte[] p = Proto.Packet(Proto.OpMkdir, false, 0, 0, 0, 0, Encoding.UTF8.GetBytes(dir));
                        if (!s.Send(p, null, 0)) { refused = dir; break; }
                    }
                    if (refused == null) return;
                }
                catch (OperationCanceledException) { throw; }
                catch (SocketException e) { last = e; }
                catch (IOException e) { last = e; }          // the link dropped: the next one is tried
                finally { if (s != null) s.Close(); }
                if (refused != null) throw new IOException("the PS5 could not make the folder " + refused);
            }
            throw new IOException("the folders could not be made on the PS5: " + (last != null ? last.Message : "no link"));
        }

        byte[] Rent(int size)
        {
            byte[] b;
            return pools[size].TryTake(out b) ? b : new byte[size];
        }

        void Return(byte[] buffer)
        {
            ConcurrentBag<byte[]> pool;
            if (pools.TryGetValue(buffer.Length, out pool) && pool.Count < poolKeep) pool.Add(buffer);
        }

        /// <summary>A piece that has to go again: a large one waits for a fast link (on Wi-Fi it could
        /// take longer than the answer is waited for) unless no fast link is up.</summary>
        void Again(Piece piece)
        {
            if (piece.Len > Proto.ChunkSize) againBig.Enqueue(piece); else again.Enqueue(piece);
        }

        bool TakeAgain(Route route, out Piece piece)
        {
            if ((route.Fast || Thread.VolatileRead(ref fastConnected) == 0) && againBig.TryDequeue(out piece)) return true;
            return again.TryDequeue(out piece);
        }

        // ---------------------------------------------------------------- small files: bundles

        void PushSmall(Piece p)
        {
            Interlocked.Increment(ref outstanding);
            try
            {
                small.Add(p, cancel);
            }
            catch (Exception)
            {
                Interlocked.Decrement(ref outstanding);
                throw;
            }
        }

        void SmallReader()
        {
            try
            {
                while (!stop)
                {
                    int i = Interlocked.Increment(ref nextSmall) - 1;
                    if (i >= smallFiles.Count) break;
                    SendFile f = smallFiles[i];
                    try
                    {
                        ReadSmall(f);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (InvalidOperationException) { return; }             // the queue closed: the send is over
                    catch (Exception e)
                    {
                        Interlocked.Increment(ref job.Errors);
                        Interlocked.Add(ref job.TotalBytes, -f.Size);         // what never left the PC no longer counts
                        eng.Log("skipped " + f.Local + ": " + e.Message);
                    }
                }
            }
            finally
            {
                if (Interlocked.Decrement(ref readersLeft) == 0)
                {
                    try
                    {
                        Piece ready;
                        lock (bundleGate) ready = TakeBundle();
                        if (ready != null) PushSmall(ready);
                    }
                    catch (Exception) { }
                    smallDone = true;
                }
            }
        }

        void ReadSmall(SendFile f)
        {
            byte[] data = File.ReadAllBytes(f.Local);
            if (data.Length != f.Size)
            {
                Interlocked.Add(ref job.TotalBytes, data.Length - f.Size);
                f.Size = data.Length;
                f.Left = data.Length;
            }
            Interlocked.Add(ref readBytes, data.Length);
            if (data.Length > Proto.BundleFileMax)
            {
                // it grew past the bundle limit since the scan: plain pieces
                for (long offset = 0; offset < data.Length; offset += Proto.ChunkSize)
                {
                    int n = (int)Math.Min(Proto.ChunkSize, data.Length - offset);
                    Piece p = new Piece();
                    p.Op = Proto.OpFileChunk;
                    p.PathText = f.Remote;
                    p.Path = Encoding.UTF8.GetBytes(f.Remote);
                    p.Data = new byte[n];
                    Buffer.BlockCopy(data, (int)offset, p.Data, 0, n);
                    p.Len = n;
                    p.Offset = offset;
                    p.Total = data.Length;
                    p.Credit = n;
                    p.File = f;
                    PushSmall(p);
                }
                return;
            }
            byte[] path = Encoding.UTF8.GetBytes(f.Remote);
            if (path.Length == 0 || path.Length >= 1024) throw new IOException("the path is too long for the receiver");
            byte[] entry = new byte[6 + path.Length + data.Length];
            entry[0] = (byte)path.Length;
            entry[1] = (byte)(path.Length >> 8);
            Proto.Put32(entry, 2, (uint)data.Length);
            Buffer.BlockCopy(path, 0, entry, 6, path.Length);
            Buffer.BlockCopy(data, 0, entry, 6 + path.Length, data.Length);
            Piece ready = null;
            lock (bundleGate)
            {
                bundleFiles.Add(f);
                bundleData.Add(entry);
                bundleBytes += entry.Length;
                if (bundleBytes >= Proto.BundleBytes || bundleFiles.Count >= Proto.BundleFiles) ready = TakeBundle();
            }
            if (ready != null) PushSmall(ready);
        }

        /// <summary>The waiting small files as one OP_BUNDLE piece: entries [u16 path length]
        /// [u32 size][path][data]. Called with bundleGate held.</summary>
        Piece TakeBundle()
        {
            if (bundleFiles.Count == 0) return null;
            byte[] data = new byte[bundleBytes];
            int at = 0;
            long credit = 0;
            foreach (byte[] entry in bundleData)
            {
                Buffer.BlockCopy(entry, 0, data, at, entry.Length);
                at += entry.Length;
            }
            foreach (SendFile f in bundleFiles) credit += f.Size;
            Piece p = new Piece();
            p.Op = Proto.OpBundle;
            p.PathText = bundleFiles[0].Remote;
            p.Path = Encoding.UTF8.GetBytes(p.PathText);
            p.Data = data;
            p.Len = data.Length;
            p.Credit = credit;
            p.Members = bundleFiles.Count;
            bundleFiles = new List<SendFile>();
            bundleData = new List<byte[]>();
            bundleBytes = 0;
            return p;
        }

        // ---------------------------------------------------------------- large files: who sends which

        /// <summary>The next large file not yet given out becomes route's: the largest, or for a slow
        /// link the smallest. Called with pick held.</summary>
        FileRun NewRun(Route route, bool largest, bool fromMid)
        {
            SendFile f = fromMid ? (largest ? mid[midHead++] : mid[midTail--]) : (largest ? big[bigHead++] : big[bigTail--]);
            FileRun run = new FileRun();
            run.File = f;
            run.Owner = route;
            run.Back = f.Size;
            run.Empty = f.Size == 0;
            runs.Add(run);
            return run;
        }

        /// <summary>Whether a file may begin now: one every MainGap (MidGap for the mid-size ones), so
        /// the PS5 opens them one after another. Called with pick held.</summary>
        bool MayStart(bool midSize)
        {
            long now = DateTime.UtcNow.Ticks;
            if (now - lastStart < (midSize ? MidGap : MainGap)) return false;
            lastStart = now;
            return true;
        }

        /// <summary>A large file for a connection of route to work on from the front: one the route
        /// owns already (the one with the fewest connections on it) once it owns as many as it
        /// sends at once, else a new one. null: there is none.</summary>
        FileRun Own(Route route, bool largest)
        {
            lock (pick)
            {
                FileRun mine = null, fresh = null;
                int owned = 0;
                foreach (FileRun run in runs)
                {
                    if (run.Owner != route || run.Left <= 0) continue;
                    owned++;
                    if (!run.Started)
                    {
                        if (fresh == null) fresh = run;             // given out before the start, not begun
                    }
                    else if (mine == null || run.Users < mine.Users) mine = run;
                }
                // a new file, when one may begin now: one given out before the start, else the next waiting
                bool mainLeft = bigHead <= bigTail, midLeft = tailOpen && midHead <= midTail;
                if (fresh != null || ((mainLeft || midLeft) && owned < route.BigSlots))
                {
                    bool midSize = fresh == null && !mainLeft;
                    if (MayStart(midSize))
                    {
                        if (fresh == null) fresh = NewRun(route, largest, midSize);
                        fresh.Started = true;
                        fresh.Users++;
                        return fresh;
                    }
                    // its turn to begin a file comes within the gap: it waits for that rather than join a
                    // file already begun, where it would stay until that file is done
                    return null;
                }
                // a file whose link has no connection up any more (or never had one) is taken over
                if (DateTime.UtcNow.Ticks - began > TimeSpan.TicksPerSecond * 3)
                {
                    foreach (FileRun run in runs)
                    {
                        if (run.Users != 0 || run.Owner == route || run.Left <= 0) continue;
                        if (Thread.VolatileRead(ref run.Owner.Stat.Connections) != 0) continue;
                        run.Owner = route;
                        run.Started = true;
                        run.Users = 1;
                        return run;
                    }
                }
                if (mine != null) mine.Users++;
                return mine;
            }
        }

        /// <summary>The large file with the most still to go, for a fast link with nothing of its own
        /// left: it takes pieces from the file's end. null: nothing is worth helping.</summary>
        const int HelpUsers = 2;       // helper connections one file takes at most (its owner's are its own)

        FileRun Help()
        {
            lock (pick)
            {
                // while main files still wait to begin, a connection waits for its own instead
                if (bigHead <= bigTail) return null;
                foreach (FileRun run in runs)
                    if (!run.Started && run.Left > 0) return null;
                FileRun most = null;
                long mostLeft = 2L * Proto.ChunkSize - 1;         // less than two pieces: its owner finishes it
                foreach (FileRun run in runs)
                {
                    long left = run.Left;
                    // 0.15: a file that already has HelpUsers helpers takes no more. The PS5 writes a file
                    // with one writer, so a whole link's connections on one file (0.14: 12 of them, each
                    // with pieces ahead) only queued their pieces in the receiver's RAM until the kernel
                    // stalled it (three "FMEM allocation timeout"s as the helping began).
                    // 0.17: not for one huge file. There every link has to join it (0.16 sent a 7.4 GB iso over
                    // the 5 GbE lane and two cable connections only), and the receiver takes 4 writers on it.
                    if (run.Empty || left <= mostLeft || run.Helpers >= (eng.HugeSend ? 1000 : HelpUsers)) continue;
                    most = run;
                    mostLeft = left;
                }
                if (most != null)
                {
                    most.Users++;
                    most.Helpers++;
                }
                return most;
            }
        }

        void Release(Conn c)
        {
            if (c.Run == null) return;
            lock (pick)
            {
                c.Run.Users--;
                if (c.Helping) c.Run.Helpers--;
            }
            c.Run = null;
            c.Helping = false;
        }

        /// <summary>The next piece of c's file, read from the PC: from the front, or for a helper
        /// from the back. null: the file has nothing left to hand out.</summary>
        Piece Claim(Conn c, int chunk)
        {
            FileRun run = c.Run;
            SendFile f = run.File;
            long offset;
            int length;
            lock (run.Gate)
            {
                if (run.Failed) return null;
                if (run.Empty)
                {
                    if (run.EmptySent) return null;
                    run.EmptySent = true;
                    offset = 0;
                    length = 0;
                }
                else
                {
                    long left = run.Back - run.Front;
                    if (left <= 0) return null;
                    if (!c.Helping)
                    {
                        offset = run.Front;
                        length = (int)Math.Min(chunk, left);
                        run.Front += length;
                    }
                    else
                    {
                        offset = (run.Back - 1) / chunk * chunk;
                        if (offset < run.Front) offset = run.Front;
                        length = (int)(run.Back - offset);
                        run.Back = offset;
                    }
                }
                Interlocked.Increment(ref outstanding);
            }
            byte[] buffer = length > 0 ? Rent(chunk) : new byte[0];
            try
            {
                if (length > 0)
                {
                    if (c.StreamRun != run)
                    {
                        if (c.Stream != null) c.Stream.Dispose();
                        c.Stream = null;
                        c.StreamRun = null;
                        c.Stream = new FileStream(f.Local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);
                        c.StreamRun = run;
                    }
                    if (c.Stream.Position != offset) c.Stream.Seek(offset, SeekOrigin.Begin);
                    int got = 0;
                    while (got < length)
                    {
                        int n = c.Stream.Read(buffer, got, length - got);
                        if (n <= 0) throw new IOException("the file is shorter than it was when the send started");
                        got += n;
                    }
                    Interlocked.Add(ref readBytes, length);
                }
            }
            catch (Exception e)
            {
                // unreadable: this piece and all that was not handed out yet are skipped
                long rest;
                bool first;
                lock (run.Gate)
                {
                    rest = run.Back - run.Front;
                    run.Front = run.Back;
                    first = !run.Failed;
                    run.Failed = true;
                }
                if (length > 0) Return(buffer);
                Interlocked.Add(ref job.TotalBytes, -(rest + length));
                Interlocked.Decrement(ref outstanding);
                if (first)
                {
                    Interlocked.Increment(ref job.Errors);
                    eng.Log("skipped " + f.Local + ": " + e.Message);
                }
                return null;
            }
            Piece p = new Piece();
            p.Op = Proto.OpFileChunk;
            p.PathText = f.Remote;
            p.Path = Encoding.UTF8.GetBytes(f.Remote);
            p.Data = buffer;
            p.Len = length;
            p.Offset = offset;
            p.Total = f.Size;
            p.Credit = length;
            p.File = f;
            p.Pooled = length > 0;
            return p;
        }

        /// <summary>What a connection of route sends next; null when there is nothing for it now.</summary>
        int sitOutSaid;

        Piece Next(Route route, Conn c)
        {
            // 0.18: one huge file goes over the fast links only (the Realtek lanes on USB 3 and the USB
            // bridge cable); the others sit it out while a fast link is up. Measured on the same
            // 23.86 GB file: all six links 474 MB/s on average (80-718), without the USB 2.0 adapter and
            // the home network 760 (885-890 after a slow start), the three fast links alone 892 (903-940).
            if (eng.HugeSend && !route.Fast && Thread.VolatileRead(ref fastConnected) > 0)
            {
                if (Interlocked.Exchange(ref sitOutSaid, 1) == 0)
                    eng.Log("one huge file: the fast links carry it; the 1 Gbit/s links and the home network sit this send out");
                return null;
            }
            for (int round = 0; round < 4; round++)
            {
                if (c.Run != null)
                {
                    Piece p = Claim(c, route.PieceBytes);
                    if (p != null) return p;
                    Release(c);
                }
                Piece bundle;
                if (route.Fast)
                {
                    c.Run = Own(route, true);
                    if (c.Run != null) continue;
                    if (tailOpen && small.TryTake(out bundle)) return bundle;
                }
                else
                {
                    // a slow link: once the main files are on their way, the small files in bundles; before
                    // that (and after them) a file of its own, the smallest one left
                    if (tailOpen && small.TryTake(out bundle, smallDone ? 0 : 50)) return bundle;
                    c.Run = Own(route, false);
                    if (c.Run != null) continue;
                }
                c.Run = Help();
                c.Helping = c.Run != null;
                if (c.Run == null) return null;
                if (!c.Run.HelpSaid)
                {
                    c.Run.HelpSaid = true;
                    eng.Log(route.Name + " has no file of its own left and helps " + FileName(c.Run.File.Remote) + " from its end (" + Theme.Size(c.Run.Left) + " to go)");
                }
            }
            return null;
        }

        static string FileName(string remote)
        {
            int slash = remote.LastIndexOf('/');
            return slash >= 0 ? remote.Substring(slash + 1) : remote;
        }

        // ---------------------------------------------------------------- connections

        long inFlight;                 // bytes posted and not answered yet, every link together

        bool OverCap(int more)
        {
            long now = Interlocked.Read(ref inFlight);
            return now > 0 && now + more > (long)eng.InFlightCapMb * 1024 * 1024;
        }

        void Connection(Route route)
        {
            Conn state = new Conn();
            Channel s = null;
            Piece piece = null;
            Queue<Piece> flying = new Queue<Piece>();     // posted, not answered yet (oldest first)
            Queue<object> tokens = new Queue<object>();
            int ahead = Math.Max(1, route.Ahead);
            int failures = 0;
            bool said = false;
            while (!stop)
            {
                try
                {
                    if (s == null)
                    {
                        s = Channel.Open(route);
                        Interlocked.Increment(ref connected);
                        if (route.Fast) Interlocked.Increment(ref fastConnected);
                        Interlocked.Increment(ref route.Stat.Connections);
                        if (said) eng.Log(route.Name + " is back and joins the send");
                        failures = 0;
                        said = false;
                    }
                    int limit = state.Helping ? 1 : ahead;      // a helper waits for each answer
                    if (flying.Count < limit)
                    {
                        if (piece == null && !TakeAgain(route, out piece)) piece = Next(route, state);
                        // 0.17: the cap on what all links keep unanswered. Over it, a connection with a piece
                        // on its way waits for that answer first; one with none waits for room.
                        if (piece != null && flying.Count == 0)
                            while (OverCap(piece.Len) && !stop) Thread.Sleep(2);
                        if (piece != null && !(flying.Count > 0 && OverCap(piece.Len)))
                        {
                            Interlocked.Add(ref inFlight, piece.Len);
                            flying.Enqueue(piece);
                            Piece posted = piece;
                            piece = null;
                            tokens.Enqueue(PostPiece(s, route, posted, state));
                            if (flying.Count < (state.Helping ? 1 : ahead)) continue;       // one more on its way before waiting
                        }
                    }
                    if (flying.Count == 0)
                    {
                        Thread.Sleep(20);
                        continue;
                    }
                    Piece done = flying.Peek();
                    bool ok = CollectPiece(s, route, done, tokens.Peek());
                    flying.Dequeue();
                    tokens.Dequeue();
                    Interlocked.Add(ref inFlight, -done.Len);
                    if (ok)
                    {
                        // a large file's first piece is the PS5 opening it: how long that took, for the log
                        if (done.File != null && done.Offset == 0 && done.Total >= 256L * 1024 * 1024)
                            eng.Log(string.Format("opened {0} ({1}) on {2}: the PS5 answered its first piece in {3:0} ms",
                                                  FileName(done.PathText), Theme.Size(done.Total), route.Name, s.AnswerTicks * 1000.0 / Stopwatch.Frequency));
                        Landed(route, done);
                    }
                    else if (++done.Fails >= 3)
                    {
                        fatal = "the PS5 could not write " + done.PathText + " (is the drive full, or the folder read-only?)";
                    }
                    else
                    {
                        Again(done);
                    }
                }
                catch (Exception e)
                {
                    if (s != null)
                    {
                        s.Close();
                        s = null;
                        Interlocked.Decrement(ref connected);
                        if (route.Fast) Interlocked.Decrement(ref fastConnected);
                        Interlocked.Decrement(ref route.Stat.Connections);
                    }
                    if (piece != null)
                    {
                        Again(piece);                     // another connection takes it
                        piece = null;
                    }
                    while (flying.Count > 0)                  // posted but never answered
                    {
                        Piece lost = flying.Dequeue();
                        Interlocked.Add(ref inFlight, -lost.Len);
                        Again(lost);
                    }
                    tokens.Clear();
                    Release(state);                       // its file goes on with the other connections
                    if (stop) break;
                    if (!(e is SocketException) && !(e is IOException) && !(e is ObjectDisposedException))
                    {
                        fatal = e.GetType().Name + ": " + e.Message;
                        break;
                    }
                    failures++;
                    if (failures == 3 && !said)
                    {
                        said = true;
                        eng.Log(route.Name + " isn't answering (" + e.Message + "): its files go over the other links; trying again every 10 s");
                    }
                    for (int i = 0; i < (failures < 3 ? 10 : 100) && !stop; i++) Thread.Sleep(100);
                }
            }
            Release(state);
            if (state.Stream != null)
            {
                try { state.Stream.Dispose(); } catch (Exception) { }
            }
            if (s != null)
            {
                s.Close();
                Interlocked.Decrement(ref connected);
                if (route.Fast) Interlocked.Decrement(ref fastConnected);
                Interlocked.Decrement(ref route.Stat.Connections);
            }
        }

        object PostPiece(Channel s, Route route, Piece piece, Conn c)
        {
            byte[] data = piece.Data;
            int size = piece.Len;
            bool packed = false;
            if (piece.Len > 0 && eng.LzWorth(piece.File))
            {
                int n = TryLz4(piece, c);
                if (n > 0)
                {
                    data = c.Scratch;
                    size = n;
                    packed = true;
                }
            }
            byte[] head = Proto.Packet(piece.Op, packed, piece.Offset, piece.Total, size, piece.Len, piece.Path);
            piece.WireBytes = head.Length + size;
            object token = s.Post(head, data, size);
            Interlocked.Add(ref route.Stat.SendTicks, s.SendTicks);
            return token;
        }

        bool CollectPiece(Channel s, Route route, Piece piece, object token)
        {
            bool ok = s.Collect(token);
            Interlocked.Add(ref route.Stat.AnswerTicks, s.AnswerTicks);
            Interlocked.Increment(ref route.Stat.Pieces);
            if (ok) Interlocked.Add(ref route.Stat.Wire, piece.WireBytes);
            return ok;
        }

        const int Sample = 64 * 1024;

        /// <summary>The piece as an LZ4 block in c.Scratch when that makes it smaller: its size, else 0.
        /// A large piece is tried on a 64 KB sample first. Every packed piece is unpacked again and
        /// compared before it is sent, so a compressor fault can never reach the PS5.</summary>
        int TryLz4(Piece piece, Conn c)
        {
            int len = piece.Len;
            if (c.Table == null) c.Table = Lz4.NewTable();
            if (len >= 4 * Sample)
            {
                if (c.Tried == null) c.Tried = new byte[Lz4.MaxCompressed(Sample)];
                int sample = Lz4.Compress(piece.Data, Sample, c.Tried, c.Table);
                if (sample > 0.97 * Sample)
                {
                    eng.LzResult(piece.File, false);
                    return 0;
                }
            }
            int need = Lz4.MaxCompressed(len);
            if (c.Scratch == null || c.Scratch.Length < need) c.Scratch = new byte[need];
            int n = Lz4.Compress(piece.Data, len, c.Scratch, c.Table);
            if (n >= len)
            {
                eng.LzResult(piece.File, false);
                return 0;
            }
            if (c.Check == null || c.Check.Length < len) c.Check = new byte[len];
            bool same = Lz4.Decompress(c.Scratch, n, c.Check, len);
            for (int i = 0; same && i < len; i++)
                if (c.Check[i] != piece.Data[i]) same = false;
            if (!same)
            {
                eng.Lz4On = false;
                eng.Log("LZ4 self-check failed on " + piece.PathText + ": compression is off, the piece goes as it is");
                return 0;
            }
            eng.LzResult(piece.File, n <= 0.97 * len);
            return n;
        }

        void Landed(Route route, Piece piece)
        {
            Interlocked.Add(ref route.Stat.Bytes, piece.Credit);
            Interlocked.Add(ref job.DoneBytes, piece.Credit);
            if (piece.File == null) Interlocked.Add(ref job.FilesDone, piece.Members);
            else if (Interlocked.Add(ref piece.File.Left, -piece.Credit) <= 0) Interlocked.Increment(ref job.FilesDone);
            if (piece.Pooled) Return(piece.Data);
            piece.Data = null;
            Interlocked.Decrement(ref outstanding);
        }
    }
}
