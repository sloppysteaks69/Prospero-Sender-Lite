// SPDX-License-Identifier: LGPL-2.1-or-later
// Prospero Sender Lite: the bonded receiver's wire format (mux_receiver, TCP 9030),
// ported from Prospero Sender Turbo 6.49's pw_bonded.py. C# 5 on purpose: it builds
// with the compiler that ships in Windows.
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace PSLite
{
    static class Proto
    {
        public const string AppName = "Prospero Sender Lite";
        public const string Version = "0.19";

        public const uint Magic = 0x5053354D;            // "PS5M"
        public const byte OpFileChunk = 1, OpMkdir = 2, OpSymlink = 3, OpStatus = 4, OpQuit = 5, OpBundle = 6;
        public const int Port = 9030;                    // the receiver
        public const int SvcPort = 9031;                 // its service port (list, delete)
        public const int LoaderPort = 9021;              // the PS5's ELF loader
        public const int HeaderSize = 34;
        public const int ChunkSize = 4 * 1024 * 1024;    // a file's piece (the receiver takes 8 MB at most)
        public const int BundleVersion = 61;             // the first receiver that takes bundles
        public const int BundleFileMax = 1 << 20;        // files this size or less travel in bundles
        public const int BundleBytes = 6 << 20;          // a bundle closes at about this many bytes
        public const int BundleFiles = 512;              // or this many files
        public const int ReceiverVersion = 85;           // the receiver built into this exe
        public const string ReceiverResource = "mux_receiver-85.elf";
        public const int ConnectTimeoutMs = 5000;
        public const int IoTimeoutMs = 30000;            // no answer this long: the link stalled
        public const int LoaderWaitMs = 2000;            // the ELF loader gets a moment before the payload
        public const int StartupWaitMs = 12000;          // the receiver gets this long to answer after inject

        /// <summary>The 34-byte little-endian header followed by the path: what goes before a packet's data.</summary>
        public static byte[] Packet(byte op, bool compressed, long offset, long total, int dataSize, int rawSize, byte[] path)
        {
            if (path.Length >= 1024)
                throw new IOException("the path is too long for the receiver (1023 bytes at most)");
            byte[] p = new byte[HeaderSize + path.Length];
            Put32(p, 0, Magic);
            p[4] = op;
            p[5] = (byte)(compressed ? 1 : 0);
            Put64(p, 6, (ulong)offset);
            Put64(p, 14, (ulong)total);
            Put32(p, 22, (uint)dataSize);
            Put32(p, 26, (uint)rawSize);
            p[30] = (byte)(path.Length & 0xff);
            p[31] = (byte)(path.Length >> 8);
            p[32] = 0;                                   // link target length (OP_SYMLINK only)
            p[33] = 0;
            Buffer.BlockCopy(path, 0, p, HeaderSize, path.Length);
            return p;
        }

        public static void Put32(byte[] b, int at, uint v)
        {
            b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24);
        }

        public static void Put64(byte[] b, int at, ulong v)
        {
            Put32(b, at, (uint)v);
            Put32(b, at + 4, (uint)(v >> 32));
        }

        public static uint Get32(byte[] b, int at)
        {
            return (uint)(b[at] | b[at + 1] << 8 | b[at + 2] << 16 | b[at + 3] << 24);
        }

        public static ulong Get64(byte[] b, int at)
        {
            return Get32(b, at) | ((ulong)Get32(b, at + 4) << 32);
        }

        /// <summary>A TCP connection to remote:port, from the PC's address local when one is given
        /// (which ties the connection to that link).</summary>
        public static Socket Connect(string local, string remote, int port, int timeoutMs)
        {
            Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                s.NoDelay = true;
                s.SendBufferSize = 8 * 1024 * 1024;
                if (!string.IsNullOrEmpty(local))
                    s.Bind(new IPEndPoint(IPAddress.Parse(local), 0));
                IAsyncResult r = s.BeginConnect(IPAddress.Parse(remote), port, null, null);
                if (!r.AsyncWaitHandle.WaitOne(timeoutMs))
                {
                    s.Close();
                    throw new SocketException(10060);    // timed out
                }
                s.EndConnect(r);
                s.SendTimeout = IoTimeoutMs;
                s.ReceiveTimeout = IoTimeoutMs;
                return s;
            }
            catch
            {
                try { s.Close(); } catch { }
                throw;
            }
        }

        public static void Close(Socket s)
        {
            if (s == null) return;
            try { s.Close(); } catch { }
        }

        public static void SendAll(Socket s, byte[] data, int length)
        {
            int at = 0;
            while (at < length)
            {
                int n = s.Send(data, at, length - at, SocketFlags.None);
                if (n <= 0) throw new IOException("the connection closed");
                at += n;
            }
        }

        public static void ReadAll(Socket s, byte[] data, int length)
        {
            int at = 0;
            while (at < length)
            {
                int n = s.Receive(data, at, length - at, SocketFlags.None);
                if (n <= 0) throw new IOException("the receiver closed the connection");
                at += n;
            }
        }

        /// <summary>The receiver's one-byte answer to a packet: true when it did what was asked.</summary>
        public static bool Answer(Socket s)
        {
            byte[] one = new byte[1];
            ReadAll(s, one, 1);
            return one[0] == 1;
        }

        public static bool Reachable(string local, string remote, int port, int timeoutMs)
        {
            try
            {
                Close(Connect(local, remote, port, timeoutMs));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Asks the receiver at host for its status line. False when nothing answers.
        /// version 1 and an empty line: a receiver too old to say.</summary>
        public static bool Status(string host, int port, int timeoutMs, out int version, out string line)
        {
            version = 0;
            line = "";
            Socket s;
            try { s = Connect(null, host, port, timeoutMs); }
            catch (Exception) { return false; }
            try
            {
                s.SendTimeout = timeoutMs;
                s.ReceiveTimeout = timeoutMs;
                byte[] ask = Packet(OpStatus, false, 0, 0, 0, 0, new byte[0]);
                SendAll(s, ask, ask.Length);
                byte[] head = new byte[3];
                ReadAll(s, head, 1);
                if (head[0] != 1) { version = 1; return true; }
                ReadAll(s, head, 2);
                int size = head[0] | head[1] << 8;
                byte[] text = new byte[size];
                ReadAll(s, text, size);
                string all = Encoding.UTF8.GetString(text);
                Match m = Regex.Match(all, @"^receiver (\d+); ?(.*)$", RegexOptions.Singleline);
                if (m.Success)
                {
                    version = int.Parse(m.Groups[1].Value);
                    line = m.Groups[2].Value;
                }
                else
                {
                    version = 1;
                    line = all;
                }
                return true;
            }
            catch (Exception)
            {
                version = 1;                              // it closed on us: a receiver from before OP_STATUS
                return true;
            }
            finally
            {
                Close(s);
            }
        }

        /// <summary>Makes one folder on the PS5 (the receiver makes missing parents too).</summary>
        public static bool MakeFolder(string host, string path)
        {
            Socket s = Connect(null, host, Port, ConnectTimeoutMs);
            try
            {
                byte[] p = Packet(OpMkdir, false, 0, 0, 0, 0, Encoding.UTF8.GetBytes(path));
                SendAll(s, p, p.Length);
                return Answer(s);
            }
            finally
            {
                Close(s);
            }
        }

        /// <summary>Sends a payload to the PS5's ELF loader, which starts it once the connection closes.</summary>
        public static void SendPayload(string host, int port, byte[] elf)
        {
            Socket s = Connect(null, host, port, 3000);
            try
            {
                Thread.Sleep(LoaderWaitMs);
                SendAll(s, elf, elf.Length);
                s.Shutdown(SocketShutdown.Send);
                Thread.Sleep(200);
            }
            finally
            {
                Close(s);
            }
        }

        /// <summary>The receiver ELF carried inside this exe.</summary>
        public static byte[] ReceiverElf()
        {
            using (Stream s = typeof(Proto).Assembly.GetManifestResourceStream(ReceiverResource))
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
                return data;
            }
        }
    }
}
