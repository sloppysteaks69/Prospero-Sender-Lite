// SPDX-License-Identifier: LGPL-2.1-or-later
// Prospero Sender Lite: Activate Quantum Slipstream (ported from pw_bonded.py's
// slipstream_now / feed_slipstream / clear_slipstream, Turbo 6.17-6.29).
// The PS5 writes its M.2 at one of three speeds, and a Sony install job is what switches
// it to the fast one. The receiver asks Sony's installer to install one small pkg, the
// "Quantum Slipstream Drive", whenever a new copy of it lands at
// /data/prospero-win/slipstream/slipstream.pkg. This puts it there and reports the answer.
// Turbo uploaded the pkg over FTP; Lite sends it through the receiver's own port, as one
// piece, so the file appears whole.
using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace PSLite
{
    static class Slipstream
    {
        public const string PkgName = "slipstream.pkg";
        public const string RemoteFolder = "/data/prospero-win/slipstream";
        public const string Remote = RemoteFolder + "/" + PkgName;
        public const int ButtonVersion = 79;     // receiver 79 asks again for each new upload of the pkg
        public const int WaitSeconds = 30;       // how long the button waits for the installer's answer
        public const int KeepSeconds = 120;      // a taken pkg stays on the PS5 this long for its install job
        const int OnePacketMax = 8 * 1024 * 1024 - 4096;   // the receiver takes 8 MB in a packet at most

        /// <summary>'engaged', 'waiting for the pkg', 'not engaged: ...' from a status line.</summary>
        public static string State(string line)
        {
            Match m = Regex.Match(line ?? "", "slipstream: ([^;]*)");
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }

        /// <summary>The install requests the receiver has made ("slipstream asks N"; -1: not said).</summary>
        public static int Asks(string line)
        {
            Match m = Regex.Match(line ?? "", @"slipstream asks (\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : -1;
        }

        /// <summary>The pkg: a slipstream.pkg beside the exe when there is one (a newer build
        /// dropped in), else the one inside the exe. null: neither.</summary>
        public static byte[] Pkg(out string source)
        {
            source = "";
            try
            {
                string beside = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, PkgName);
                if (File.Exists(beside))
                {
                    source = beside;
                    return File.ReadAllBytes(beside);
                }
            }
            catch (Exception) { }
            using (Stream s = typeof(Slipstream).Assembly.GetManifestResourceStream(PkgName))
            {
                if (s == null) return null;
                byte[] data = new byte[(int)s.Length];
                int at = 0;
                while (at < data.Length)
                {
                    int n = s.Read(data, at, data.Length - at);
                    if (n <= 0) break;
                    at += n;
                }
                source = "built in";
                return data;
            }
        }

        public static bool LooksLikePkg(byte[] pkg)
        {
            return pkg != null && pkg.Length >= 0x100 && pkg.Length <= (32 << 20) && pkg[0] == 0x7F && pkg[1] == (byte)'C' && pkg[2] == (byte)'N' && pkg[3] == (byte)'T';
        }

        /// <summary>Writes the pkg to its place on the PS5 through the receiver.</summary>
        static void Send(string host, byte[] pkg)
        {
            Socket s = Proto.Connect(null, host, Proto.Port, Proto.ConnectTimeoutMs);
            try
            {
                byte[] folder = Proto.Packet(Proto.OpMkdir, false, 0, 0, 0, 0, Encoding.UTF8.GetBytes(RemoteFolder));
                Proto.SendAll(s, folder, folder.Length);
                if (!Proto.Answer(s)) throw new IOException("the PS5 could not make " + RemoteFolder);
                byte[] path = Encoding.UTF8.GetBytes(Remote);
                int step = pkg.Length <= OnePacketMax ? pkg.Length : Proto.ChunkSize;
                for (int offset = 0; offset < pkg.Length; offset += step)
                {
                    int n = Math.Min(step, pkg.Length - offset);
                    byte[] head = Proto.Packet(Proto.OpFileChunk, false, offset, pkg.Length, n, n, path);
                    Proto.SendAll(s, head, head.Length);
                    s.Send(pkg, offset, n, SocketFlags.None);
                    if (!Proto.Answer(s)) throw new IOException("the PS5 could not write " + Remote);
                }
            }
            finally
            {
                Proto.Close(s);
            }
        }

        /// <summary>Sends the pkg anew, so the receiver asks the PS5's installer again, and waits for
        /// its answer. True when the request was taken; message says what happened either way.</summary>
        public static bool Activate(string host, Action<string> log, out string message)
        {
            int version;
            string line;
            if (!Proto.Status(host, Proto.Port, 2500, out version, out line))
            {
                message = "The receiver isn't running on the PS5, so Quantum Slipstream can't be asked for: press Inject receiver (is the ELF loader running?).";
                return false;
            }
            if (version < ButtonVersion)
            {
                message = "The PS5 runs receiver " + version + ": Quantum Slipstream needs receiver " + ButtonVersion + " or newer. Press Inject receiver first.";
                return false;
            }
            string state = State(line);
            if (state.Contains("installer library isn't there"))
            {
                message = "The receiver couldn't load the PS5's installer (" + state + "). Restart the PS5, load kstuff, then press Quantum Slipstream again.";
                return false;
            }
            if (state.Contains("earlier receiver ended"))
            {
                message = "The PS5's installer took the receiver down earlier this boot, so it isn't asked again until the PS5 restarts. Restart it, load kstuff, then press Quantum Slipstream.";
                return false;
            }
            if (state == "off")
            {
                message = "Quantum Slipstream is turned off in the receiver (MUX_SLIPSTREAM=0).";
                return false;
            }
            string source;
            byte[] pkg = Pkg(out source);
            if (!LooksLikePkg(pkg))
            {
                message = pkg == null ? PkgName + " is neither inside this exe nor beside it." : PkgName + " (" + source + ") is not a pkg the receiver would take.";
                return false;
            }
            int before = Asks(line);
            try
            {
                Send(host, pkg);
                log("slipstream: sent " + PkgName + " (" + pkg.Length + " bytes, " + source + ") to " + host);
            }
            catch (Exception e)
            {
                log("slipstream: couldn't send " + PkgName + ": " + e.Message);
                message = PkgName + " couldn't be sent to the PS5: " + e.Message;
                return false;
            }
            DateTime until = DateTime.UtcNow.AddSeconds(WaitSeconds);
            while (DateTime.UtcNow < until)
            {
                Thread.Sleep(1000);
                if (!Proto.Status(host, Proto.Port, 1500, out version, out line)) continue;
                if (Asks(line) <= before || line.Contains("asking Sony's installer")) continue;
                state = State(line);
                log("slipstream: " + state);
                if (state == "engaged")
                {
                    message = "Quantum Slipstream engaged: the PS5 took the install request. Its install job switches the M.2 to full speed until the PS5 restarts.";
                    return true;
                }
                Match code = Regex.Match(state, "answered (0x[0-9a-fA-F]+)");
                if (code.Success)
                {
                    message = "The PS5 refused the Quantum Slipstream install (" + code.Groups[1].Value + "). Fake pkgs install only with kstuff loaded: send kstuff with Payloads, then press Quantum Slipstream again.";
                    return false;
                }
                message = "Quantum Slipstream not engaged: " + state;
                return false;
            }
            message = "The PS5 hasn't answered the install request in " + WaitSeconds + " s. The receiver's state: " + State(line);
            return false;
        }

        /// <summary>Deletes the pkg from the PS5 once its install job has had it. A receiver asks
        /// the installer at its start for a pkg left there, so none is kept.</summary>
        public static void Clear(string host, Action<string> log)
        {
            try
            {
                using (Svc svc = new Svc(host))
                {
                    long files, bytes;
                    string text;
                    int result = svc.Delete(Remote, out files, out bytes, out text);
                    if (result == Svc.Moved || result == Svc.DeletedInPlace) log("slipstream: deleted the used " + PkgName + " from the PS5");
                    else if (result == 0) log("slipstream: couldn't delete " + PkgName + ": " + text);
                }
            }
            catch (Exception e)
            {
                log("slipstream: couldn't delete " + PkgName + ": " + e.Message);
            }
        }
    }
}
