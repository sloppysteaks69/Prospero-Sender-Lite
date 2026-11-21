// SPDX-License-Identifier: LGPL-2.1-or-later
// Prospero Sender Lite: the receiver's service port (TCP 9031, receiver 67+): folder
// listings and deletes done on the PS5 itself. Ported from pw_svc.py; the wire format
// is in mux_receiver/svc_port.c. A request: "PWS1", op (u8), body length (u32 LE), body.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace PSLite
{
    class RemoteEntry
    {
        public string Name;
        public bool IsDir;
        public bool IsLink;
        public long Size;
        public DateTime Modified;
    }

    class Svc : IDisposable
    {
        const byte OpDelete = 1, OpList = 2, OpRead = 3, OpWrite = 4;
        public const int Moved = 1, Absent = 2, DeletedInPlace = 3;

        Socket sock;

        public Svc(string host)
        {
            try
            {
                sock = Proto.Connect(null, host, Proto.SvcPort, 3000);
                sock.ReceiveTimeout = 60000;
                sock.SendTimeout = 60000;
            }
            catch (Exception e)
            {
                throw new IOException("the PS5's service port (" + host + ":" + Proto.SvcPort + ") doesn't answer: " + e.Message);
            }
        }

        public void Dispose()
        {
            Proto.Close(sock);
            sock = null;
        }

        void Ask(byte op, byte[] body)
        {
            byte[] head = new byte[9];
            head[0] = (byte)'P'; head[1] = (byte)'W'; head[2] = (byte)'S'; head[3] = (byte)'1';
            head[4] = op;
            Proto.Put32(head, 5, (uint)body.Length);
            Proto.SendAll(sock, head, head.Length);
            Proto.SendAll(sock, body, body.Length);
        }

        byte[] Take(int n)
        {
            byte[] data = new byte[n];
            Proto.ReadAll(sock, data, n);
            return data;
        }

        /// <summary>An answer's bytes: raw size (u32), packed size (u32; equal: not packed), the data.</summary>
        byte[] Packed()
        {
            byte[] head = Take(8);
            int raw = (int)Proto.Get32(head, 0);
            int packed = (int)Proto.Get32(head, 4);
            if (raw < 0 || packed < 0 || raw > (256 << 20) || packed > (256 << 20))
                throw new IOException("a bad answer from the service port");
            byte[] data = Take(packed);
            if (packed == raw) return data;
            byte[] plain = new byte[raw];
            if (!Lz4.Decompress(data, packed, plain, raw))
                throw new IOException("a bad LZ4 block from the service port");
            return plain;
        }

        /// <summary>Each folder's entries; null for one the PS5 couldn't open.</summary>
        public Dictionary<string, List<RemoteEntry>> ListMany(IList<string> folders)
        {
            MemoryStream body = new MemoryStream();
            foreach (string folder in folders)
            {
                byte[] b = Encoding.UTF8.GetBytes(folder);
                body.Write(b, 0, b.Length);
                body.WriteByte(0);
            }
            Ask(OpList, body.ToArray());
            string text = Encoding.UTF8.GetString(Packed());
            Dictionary<string, List<RemoteEntry>> result = new Dictionary<string, List<RemoteEntry>>();
            List<RemoteEntry> current = null;
            foreach (string line in text.Split('\n'))
            {
                string[] parts = line.Split('\t');
                if (parts.Length == 3 && parts[0] == "D")
                {
                    current = parts[1] == "0" ? new List<RemoteEntry>() : null;
                    result[parts[2]] = current;
                }
                else if (parts.Length >= 4 && current != null && (parts[0] == "f" || parts[0] == "d" || parts[0] == "l"))
                {
                    RemoteEntry e = new RemoteEntry();
                    e.IsDir = parts[0] == "d";
                    e.IsLink = parts[0] == "l";
                    long.TryParse(parts[1], out e.Size);
                    long seconds;
                    long.TryParse(parts[2], out seconds);
                    try { e.Modified = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds).ToLocalTime(); }
                    catch (Exception) { e.Modified = DateTime.MinValue; }
                    e.Name = string.Join("\t", parts, 3, parts.Length - 3);
                    current.Add(e);
                }
            }
            return result;
        }

        /// <summary>A file's bytes: all of it (tail 0) or its last tail bytes; the receiver gives 64 MB
        /// at most. null: the PS5 could not open it (it isn't there).</summary>
        public byte[] Read(string path, int tail)
        {
            Ask(OpRead, Encoding.UTF8.GetBytes(tail + "\t" + path + "\0"));
            int count = (int)Proto.Get32(Take(4), 0);
            byte[] found = null;
            for (int i = 0; i < count; i++)
            {
                byte[] head = Take(22);                       // path length, errno, size, where the data starts
                int nameLength = head[0] | head[1] << 8;
                int error = (int)Proto.Get32(head, 2);
                Take(nameLength);
                byte[] data = Packed();
                if (i == 0 && error == 0) found = data;
            }
            return found;
        }

        /// <summary>Writes a .conf in /data/prospero-win, whole (the receiver writes it beside and renames).</summary>
        public bool Write(string path, byte[] data)
        {
            byte[] name = Encoding.UTF8.GetBytes(path);
            byte[] body = new byte[name.Length + 1 + data.Length];
            Buffer.BlockCopy(name, 0, body, 0, name.Length);
            Buffer.BlockCopy(data, 0, body, name.Length + 1, data.Length);
            Ask(OpWrite, body);
            return Take(1)[0] == 1;
        }

        public List<RemoteEntry> ListOne(string folder)
        {
            Dictionary<string, List<RemoteEntry>> all = ListMany(new string[] { folder });
            List<RemoteEntry> found;
            if (!all.TryGetValue(folder, out found) || found == null)
                throw new IOException("the PS5 can't open " + folder);
            return found;
        }

        /// <summary>Deletes a file or folder on the PS5. status: Moved (into the drive's trash, emptied
        /// in the background), Absent, DeletedInPlace, or 0 with a message (not deleted).</summary>
        public int Delete(string path, out long files, out long bytes, out string message)
        {
            Ask(OpDelete, Encoding.UTF8.GetBytes(path));
            byte[] head = Take(15);
            int status = head[0];
            files = Proto.Get32(head, 1);
            bytes = (long)Proto.Get64(head, 5);
            int length = head[13] | head[14] << 8;
            message = length > 0 ? Encoding.UTF8.GetString(Take(length)) : "";
            return status;
        }
    }
}
