// SPDX-License-Identifier: LGPL-2.1-or-later
// Prospero Sender Lite: LZ4 blocks (the format the receiver unpacks pieces with and
// packs its listings in). Plain safe C#, no native library.
using System;

namespace PSLite
{
    static class Lz4
    {
        const int HashLog = 16;
        const int MinMatch = 4;
        const int LastLiterals = 5;      // a block ends in at least 5 literals
        const int MfLimit = 12;          // no match starts in the last 12 bytes

        public static int MaxCompressed(int size)
        {
            return size + size / 255 + 16;
        }

        public static int[] NewTable()
        {
            return new int[1 << HashLog];
        }

        static uint Read32(byte[] b, int at)
        {
            return (uint)(b[at] | b[at + 1] << 8 | b[at + 2] << 16 | b[at + 3] << 24);
        }

        /// <summary>src[0..length) as one LZ4 block in dst (at least MaxCompressed(length) long).
        /// Returns the block's size. table is NewTable(), reused between calls.</summary>
        public static int Compress(byte[] src, int length, byte[] dst, int[] table)
        {
            Array.Clear(table, 0, table.Length);
            int ip = 0, anchor = 0, op = 0;
            int mfLimit = length - MfLimit;
            int matchLimit = length - LastLiterals;
            while (ip < mfLimit)
            {
                uint seq = Read32(src, ip);
                int h = (int)((seq * 2654435761u) >> (32 - HashLog));
                int candidate = table[h] - 1;            // 0 in the table: nothing yet
                table[h] = ip + 1;
                if (candidate < 0 || ip - candidate > 65535 || Read32(src, candidate) != seq)
                {
                    ip += 1 + ((ip - anchor) >> 6);      // data that doesn't match is stepped over faster
                    continue;
                }
                int matched = MinMatch;
                while (ip + matched < matchLimit && src[candidate + matched] == src[ip + matched]) matched++;
                int literals = ip - anchor;
                int token = op++;
                if (literals >= 15)
                {
                    dst[token] = 0xF0;
                    int left = literals - 15;
                    while (left >= 255) { dst[op++] = 255; left -= 255; }
                    dst[op++] = (byte)left;
                }
                else
                {
                    dst[token] = (byte)(literals << 4);
                }
                Buffer.BlockCopy(src, anchor, dst, op, literals);
                op += literals;
                int offset = ip - candidate;
                dst[op++] = (byte)offset;
                dst[op++] = (byte)(offset >> 8);
                int extra = matched - MinMatch;
                if (extra >= 15)
                {
                    dst[token] |= 15;
                    extra -= 15;
                    while (extra >= 255) { dst[op++] = 255; extra -= 255; }
                    dst[op++] = (byte)extra;
                }
                else
                {
                    dst[token] |= (byte)extra;
                }
                ip += matched;
                anchor = ip;
            }
            int last = length - anchor;                   // the closing literals
            int lastToken = op++;
            if (last >= 15)
            {
                dst[lastToken] = 0xF0;
                int left = last - 15;
                while (left >= 255) { dst[op++] = 255; left -= 255; }
                dst[op++] = (byte)left;
            }
            else
            {
                dst[lastToken] = (byte)(last << 4);
            }
            Buffer.BlockCopy(src, anchor, dst, op, last);
            return op + last;
        }

        /// <summary>Unpacks one LZ4 block of packed bytes into dst, which must come out at exactly
        /// rawSize bytes. False for a damaged block.</summary>
        public static bool Decompress(byte[] src, int packed, byte[] dst, int rawSize)
        {
            int ip = 0, op = 0;
            while (ip < packed)
            {
                int token = src[ip++];
                int literals = token >> 4;
                if (literals == 15)
                {
                    int more;
                    do
                    {
                        if (ip >= packed) return false;
                        more = src[ip++];
                        literals += more;
                    } while (more == 255);
                }
                if (literals < 0 || ip + literals > packed || op + literals > rawSize) return false;
                Buffer.BlockCopy(src, ip, dst, op, literals);
                ip += literals;
                op += literals;
                if (ip >= packed) break;                  // the last sequence has no match
                if (ip + 2 > packed) return false;
                int offset = src[ip] | src[ip + 1] << 8;
                ip += 2;
                int matched = token & 15;
                if (matched == 15)
                {
                    int more;
                    do
                    {
                        if (ip >= packed) return false;
                        more = src[ip++];
                        matched += more;
                    } while (more == 255);
                }
                matched += MinMatch;
                int from = op - offset;
                if (offset == 0 || from < 0 || matched < 0 || op + matched > rawSize) return false;
                for (int i = 0; i < matched; i++) dst[op + i] = dst[from + i];   // may overlap itself
                op += matched;
            }
            return op == rawSize;
        }
    }
}
