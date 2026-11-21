// SPDX-License-Identifier: LGPL-2.1-or-later
// Prospero Sender Lite: "--selftest". Checks the packet layout, LZ4 both ways, and a
// whole send over the loopback into a stand-in receiver that speaks the same protocol,
// then compares every file byte for byte. Needs no PS5.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace PSLite
{
    /// <summary>A stand-in for mux_receiver on 127.0.0.1: OP_STATUS, OP_MKDIR, OP_FILE_CHUNK
    /// (plain or LZ4, written at its offset) and OP_BUNDLE, each answered with one byte.</summary>
    class MockReceiver
    {
        readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        readonly string root;
        readonly object gate = new object();
        volatile bool stop;
        public int Port;
        public int Chunks, Bundles, Packed;

        public MockReceiver(string root)
        {
            this.root = root;
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Thread t = new Thread(Accept);
            t.IsBackground = true;
            t.Start();
        }

        public void Stop()
        {
            stop = true;
            try { listener.Stop(); } catch (Exception) { }
        }

        void Accept()
        {
            while (!stop)
            {
                Socket s;
                try { s = listener.AcceptSocket(); }
                catch (Exception) { return; }
                Thread t = new Thread(delegate() { Serve(s); });
                t.IsBackground = true;
                t.Start();
            }
        }

        string Local(string path)
        {
            return Path.Combine(root, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        }

        void Serve(Socket s)
        {
            try
            {
                byte[] head = new byte[Proto.HeaderSize];
                while (!stop)
                {
                    Proto.ReadAll(s, head, head.Length);
                    if (Proto.Get32(head, 0) != Proto.Magic) return;
                    byte op = head[4];
                    bool packed = head[5] != 0;
                    long offset = (long)Proto.Get64(head, 6);
                    int size = (int)Proto.Get32(head, 22), raw = (int)Proto.Get32(head, 26);
                    int pathLength = head[30] | head[31] << 8, targetLength = head[32] | head[33] << 8;
                    byte[] pathBytes = new byte[pathLength + targetLength];
                    Proto.ReadAll(s, pathBytes, pathBytes.Length);
                    string path = Encoding.UTF8.GetString(pathBytes, 0, pathLength);
                    byte[] data = new byte[size];
                    Proto.ReadAll(s, data, size);
                    if (packed)
                    {
                        byte[] plain = new byte[raw];
                        if (!Lz4.Decompress(data, size, plain, raw)) { s.Send(new byte[] { 0 }); continue; }
                        data = plain;
                        Interlocked.Increment(ref Packed);
                    }
                    if (op == Proto.OpStatus)
                    {
                        byte[] text = Encoding.UTF8.GetBytes("receiver " + Proto.ReceiverVersion + "; AX88179: not plugged in; RTL8157: not plugged in; pid 1");
                        byte[] answer = new byte[3 + text.Length];
                        answer[0] = 1; answer[1] = (byte)text.Length; answer[2] = (byte)(text.Length >> 8);
                        Buffer.BlockCopy(text, 0, answer, 3, text.Length);
                        s.Send(answer);
                        continue;
                    }
                    lock (gate)
                    {
                        if (op == Proto.OpMkdir)
                        {
                            Directory.CreateDirectory(Local(path));
                        }
                        else if (op == Proto.OpFileChunk)
                        {
                            Write(path, data, data.Length, 0, offset);
                            Chunks++;
                        }
                        else if (op == Proto.OpBundle)
                        {
                            int at = 0;
                            while (at < data.Length)
                            {
                                int nameLength = data[at] | data[at + 1] << 8;
                                int fileSize = (int)Proto.Get32(data, at + 2);
                                string name = Encoding.UTF8.GetString(data, at + 6, nameLength);
                                Write(name, data, fileSize, at + 6 + nameLength, 0);
                                at += 6 + nameLength + fileSize;
                            }
                            Bundles++;
                        }
                        else if (op == Proto.OpQuit)
                        {
                            return;
                        }
                    }
                    s.Send(new byte[] { 1 });
                }
            }
            catch (Exception) { }
            finally { Proto.Close(s); }
        }

        void Write(string path, byte[] data, int count, int from, long offset)
        {
            string local = Local(path);
            Directory.CreateDirectory(Path.GetDirectoryName(local));
            using (FileStream f = new FileStream(local, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
            {
                f.Seek(offset, SeekOrigin.Begin);
                f.Write(data, from, count);
            }
        }
    }

    static class SelfTest
    {
        static readonly List<string> lines = new List<string>();
        static int failures;

        static void Say(bool ok, string text)
        {
            if (!ok) failures++;
            lines.Add((ok ? "PASS  " : "FAIL  ") + text);
        }

        public static int Run(string report)
        {
            lines.Add(Proto.AppName + " " + Proto.Version + " self-test, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            try { Header(); } catch (Exception e) { Say(false, "packet header: " + e); }
            try { Compression(); } catch (Exception e) { Say(false, "LZ4: " + e); }
            try { Elf(); } catch (Exception e) { Say(false, "built-in receiver: " + e); }
            try
            {
                string conf = "file_writers = 4\n  cpus = keep\n# note\n";
                string all = ReceiverConf.With(conf, "cpus", "root");
                Say(all == "file_writers = 4\ncpus=root\n# note\n" && ReceiverConf.Value(all, "cpus") == "root" &&
                    ReceiverConf.With(all, "cpus", null) == "file_writers = 4\n# note\n" &&
                    ReceiverConf.With("", "cpus", "root") == "cpus=root\n" && ReceiverConf.With("cpus=root\n", "cpus", null) == "",
                    "receiver.conf: a key is set, replaced and removed without touching the other lines");
            }
            catch (Exception e) { Say(false, "receiver.conf editing: " + e); }
            try { Loopback(true, Proto.ReceiverVersion, "LZ4 on, bundles on"); } catch (Exception e) { Say(false, "loopback send: " + e); }
            try { Loopback(false, 60, "LZ4 off, bundles off (receiver 60)"); } catch (Exception e) { Say(false, "loopback send: " + e); }
            try { lines.Add("      USB bridge cables plugged into this PC: " + UsbPipe.Plugged()); } catch (Exception e) { Say(false, "USB bridge: looking for cables: " + e); }
            lines.Add(failures == 0 ? "ALL PASSED" : failures + " FAILED");
            try { File.WriteAllLines(report, lines.ToArray()); } catch (Exception) { }
            return failures;
        }

        static void Header()
        {
            byte[] p = Proto.Packet(Proto.OpFileChunk, true, 0x0102030405060708L, 0x1112131415161718L, 0x21222324, 0x31323334, Encoding.UTF8.GetBytes("ab"));
            byte[] want = {
                0x4D, 0x35, 0x53, 0x50, 1, 1,
                0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01,
                0x18, 0x17, 0x16, 0x15, 0x14, 0x13, 0x12, 0x11,
                0x24, 0x23, 0x22, 0x21, 0x34, 0x33, 0x32, 0x31,
                2, 0, 0, 0, (byte)'a', (byte)'b' };
            bool same = p.Length == want.Length;
            for (int i = 0; same && i < want.Length; i++) same = p[i] == want[i];
            Say(same, "packet header: 34 bytes little-endian, then the path");
        }

        static bool Same(byte[] a, byte[] b, int length)
        {
            for (int i = 0; i < length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        static byte[] Sample(int kind, int size, Random random)
        {
            byte[] b = new byte[size];
            if (kind == 1) random.NextBytes(b);                                   // nothing to find
            else if (kind == 2)                                                   // text-like
            {
                byte[] words = Encoding.ASCII.GetBytes("the quick brown fox jumps over the lazy dog; prospero sender lite; ");
                for (int i = 0; i < size; i++) b[i] = words[(i + (i / 977) * 7) % words.Length];
            }
            else if (kind == 3)                                                   // runs of random and of repeats
            {
                random.NextBytes(b);
                for (int at = 0; at + 9000 < size; at += 9000) Buffer.BlockCopy(b, 0, b, at + 4000, 5000);
            }
            return b;                                                             // kind 0: zeros
        }

        static void Compression()
        {
            // a block written by hand (not by this compressor): 1 literal, a 14-byte match at
            // offset 1, then 5 closing literals = twenty 'a'
            byte[] hand = { 0x1A, 0x61, 0x01, 0x00, 0x50, 0x61, 0x61, 0x61, 0x61, 0x61 };
            byte[] twenty = new byte[20];
            bool ok = Lz4.Decompress(hand, hand.Length, twenty, 20);
            for (int i = 0; ok && i < 20; i++) ok = twenty[i] == 0x61;
            Say(ok, "LZ4 unpacks a reference block");

            Random random = new Random(86);
            int[] table = Lz4.NewTable();
            int[] sizes = { 0, 1, 4, 12, 13, 14, 64, 300, 65536, 1 << 20, 4 << 20, (4 << 20) - 3 };
            bool all = true;
            long rawTotal = 0, packedTotal = 0;
            foreach (int size in sizes)
            {
                for (int kind = 0; kind < 4; kind++)
                {
                    byte[] src = Sample(kind, size, random);
                    byte[] dst = new byte[Lz4.MaxCompressed(size)];
                    int n = Lz4.Compress(src, size, dst, table);
                    byte[] back = new byte[size];
                    if (n <= 0 || n > dst.Length || !Lz4.Decompress(dst, n, back, size) || !Same(src, back, size))
                    {
                        all = false;
                        Say(false, "LZ4 round trip, " + size + " bytes of kind " + kind);
                    }
                    rawTotal += size;
                    packedTotal += n;
                }
            }
            Say(all, "LZ4 round trip on " + sizes.Length * 4 + " buffers (" + Theme.Size(rawTotal) + " -> " + Theme.Size(packedTotal) + ")");
            byte[] zeros = new byte[1 << 20];
            Say(Lz4.Compress(zeros, zeros.Length, new byte[Lz4.MaxCompressed(zeros.Length)], table) < 8192, "LZ4 shrinks a megabyte of zeros to a few KB");
        }

        static void Elf()
        {
            byte[] elf = Proto.ReceiverElf();
            string origin;
            byte[] pkg = Slipstream.Pkg(out origin);
            Say(Slipstream.LooksLikePkg(pkg), "the Quantum Slipstream pkg (" + (pkg != null ? Theme.Size(pkg.Length) + ", " + origin : "missing") + ") is there and is a pkg");
            Say(Slipstream.State("AX88179: not plugged in; slipstream: waiting for the pkg; slipstream asks 3; pid 9") == "waiting for the pkg" &&
                Slipstream.Asks("slipstream: engaged; slipstream asks 3; pid 9") == 3, "the slipstream state and request count are read from a status line");
            Say(elf != null && elf.Length > 100000 && elf[0] == 0x7F && elf[1] == (byte)'E' && elf[2] == (byte)'L' && elf[3] == (byte)'F',
                "the built-in receiver (" + Proto.ReceiverResource + (elf != null ? ", " + Theme.Size(elf.Length) : "") + ") is in the exe");
        }

        static void Loopback(bool lz4, int receiverVersion, string label)
        {
            string work = Path.Combine(Path.GetTempPath(), "psl-selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string source = Path.Combine(work, "source", "game"), target = Path.Combine(work, "ps5");
            Directory.CreateDirectory(Path.Combine(source, "data", "deep", "er"));
            Directory.CreateDirectory(Path.Combine(source, "empty folder"));
            Directory.CreateDirectory(target);
            Random random = new Random(5);
            Dictionary<string, byte[]> made = new Dictionary<string, byte[]>();
            made["big-random.bin"] = Sample(1, 9 * 1024 * 1024 + 123, random);     // 3 pieces, the last one short
            made["big-text.dat"] = Sample(2, 6 * 1024 * 1024, random);            // shrinks
            made["exact.pak"] = Sample(3, 4 * 1024 * 1024, random);               // one whole piece, an extension LZ4 skips
            made["empty.txt"] = new byte[0];
            made["long.bin"] = Sample(3, 37 * 1024 * 1024 + 5, random);            // long enough for the other link to help from its end
            made[Path.Combine("data", "one-byte")] = new byte[] { 42 };
            for (int i = 0; i < 40; i++)
                made[Path.Combine("data", "deep", "small-" + i + ".cfg")] = Sample(i % 4, 100 + i * 733, random);
            made[Path.Combine("data", "deep", "er", "edge-1mb.bin")] = Sample(1, 1 << 20, random);          // the largest bundled size
            made[Path.Combine("data", "deep", "er", "just-over.bin")] = Sample(2, (1 << 20) + 1, random);  // the smallest not bundled
            long total = 0;
            foreach (KeyValuePair<string, byte[]> pair in made)
            {
                File.WriteAllBytes(Path.Combine(source, pair.Key), pair.Value);
                total += pair.Value.Length;
            }

            MockReceiver mock = new MockReceiver(target);
            try
            {
                int version;
                string line;
                Say(Proto.Status("127.0.0.1", mock.Port, 2000, out version, out line) && version == Proto.ReceiverVersion, label + ": the status request is answered");
                Engine eng = new Engine();
                List<string> log = new List<string>();
                eng.Log = delegate(string text) { lock (log) log.Add(text); };
                eng.Host = "127.0.0.1";
                eng.ReceiverVersion = receiverVersion;
                eng.Lz4On = lz4;
                for (int i = 0; i < 2; i++)
                {
                    Route r = new Route();
                    r.Remote = "127.0.0.1";
                    r.Port = mock.Port;
                    r.Threads = 3;
                    r.Name = "loopback " + (i + 1);
                    r.Fast = i == 0;                   // one link of each kind: the fast one gets the large files
                    r.BigSlots = 2;
                    r.PieceBytes = i == 0 ? 8 * 1024 * 1024 : Proto.ChunkSize;
                    r.Ahead = i == 0 ? 2 : 1;          // the fast one sends a piece ahead, as on the PS5's fast lanes
                    r.EstimateMb = i == 0 ? 590 : 110;
                    r.Stat = eng.StatFor(r.Name);
                    eng.Routes.Add(r);
                }
                Job job = eng.Enqueue(new string[] { source }, "/psl-test");
                DateTime until = DateTime.UtcNow.AddSeconds(120);
                while (!job.Finished && DateTime.UtcNow < until) Thread.Sleep(50);
                Say(job.State == "Done" && job.Errors == 0, label + ": the send finished (" + job.State + (job.Error.Length > 0 ? ": " + job.Error : "") + ")");
                Say(job.DoneBytes == total && job.FilesDone == made.Count, label + ": " + job.FilesDone + " of " + made.Count + " files and " + job.DoneBytes + " of " + total + " bytes counted");
                bool same = true;
                foreach (KeyValuePair<string, byte[]> pair in made)
                {
                    string landed = Path.Combine(target, "psl-test", "game", pair.Key);
                    byte[] got = File.Exists(landed) ? File.ReadAllBytes(landed) : null;
                    if (got == null || got.Length != pair.Value.Length || !Same(got, pair.Value, got.Length))
                    {
                        same = false;
                        Say(false, label + ": " + pair.Key + " differs on the receiving side");
                    }
                }
                Say(same, label + ": every file arrived byte for byte");
                Say(Directory.Exists(Path.Combine(target, "psl-test", "game", "empty folder")), label + ": the empty folder was made");
                long both = eng.Routes[0].Stat.Bytes + eng.Routes[1].Stat.Bytes;
                Say(both == total, label + ": the two links' counters add up to the total (" + Theme.Size(eng.Routes[0].Stat.Bytes) + " + " + Theme.Size(eng.Routes[1].Stat.Bytes) + ")");
                Say(lz4 ? mock.Packed > 0 : mock.Packed == 0, label + ": " + mock.Packed + " packed pieces, " + mock.Chunks + " pieces, " + mock.Bundles + " bundles");
                Say((receiverVersion >= Proto.BundleVersion) == (mock.Bundles > 0), label + ": bundles used only when the receiver takes them");
                lines.Add("      " + string.Format("{0} in {1:0.00} s", Theme.Size(total), job.Seconds));
                lock (log) foreach (string text in log) lines.Add("      log: " + text);
            }
            finally
            {
                mock.Stop();
                try { Directory.Delete(work, true); } catch (Exception) { }
            }
        }
    }
}
