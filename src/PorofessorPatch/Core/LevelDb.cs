using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PorofessorPatch.Core
{
    // Reads Chromium's localStorage LevelDB (MANIFEST + WAL) and appends a
    // single WriteBatch (PUT) record to the current .log file.
    internal static class LevelDb
    {
        public sealed class Op
        {
            public byte[] Key;
            public byte[] Value;
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? (0x82f63b78u ^ (c >> 1)) : (c >> 1);
                t[i] = c;
            }
            return t;
        }

        private static uint Crc32c(byte[] buf)
        {
            uint c = 0xffffffff;
            foreach (var b in buf) c = CrcTable[(c ^ b) & 0xff] ^ (c >> 8);
            return c ^ 0xffffffff;
        }

        private static uint MaskCrc(uint crc) => ((crc >> 15) | (crc << 17)) + 0xa282ead8u;

        private static ushort ReadU16(byte[] b, int off) => (ushort)(b[off] | (b[off + 1] << 8));

        private static uint ReadU32(byte[] b, int off) =>
            (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));

        private static ulong ReadU64(byte[] b, int off) =>
            (ulong)ReadU32(b, off) | ((ulong)ReadU32(b, off + 4) << 32);

        private static void WriteU32(byte[] b, int off, uint v)
        {
            b[off] = (byte)v;
            b[off + 1] = (byte)(v >> 8);
            b[off + 2] = (byte)(v >> 16);
            b[off + 3] = (byte)(v >> 24);
        }

        private static void WriteU64(byte[] b, int off, ulong v)
        {
            WriteU32(b, off, (uint)v);
            WriteU32(b, off + 4, (uint)(v >> 32));
        }

        private static (ulong value, int end) DecodeVarint(byte[] buf, int off)
        {
            ulong value = 0;
            int shift = 0;
            for (int i = off; i < buf.Length; i++)
            {
                byte b = buf[i];
                value += (ulong)(b & 0x7f) << shift;
                if ((b & 0x80) == 0) return (value, i + 1);
                shift += 7;
            }
            return (value, buf.Length);
        }

        private static void WriteVarint(List<byte> parts, ulong v)
        {
            do
            {
                byte b = (byte)(v & 0x7f);
                v >>= 7;
                if (v != 0) b |= 0x80;
                parts.Add(b);
            } while (v != 0);
        }

        // Scan log-style records; keep only FULL (type 1) records whose CRC
        // verifies.
        private static List<byte[]> ScanRecords(byte[] buf)
        {
            var records = new List<byte[]>();
            int off = 0;
            while (off + 7 <= buf.Length)
            {
                int boundary = (off / 32768 + 1) * 32768;
                if (buf[off] == 0)
                {
                    int z = off;
                    while (z < boundary && z < buf.Length && buf[z] == 0) z++;
                    if (z == Math.Min(boundary, buf.Length))
                    {
                        off = z;
                        continue;
                    }
                }

                uint crc = ReadU32(buf, off);
                int len = ReadU16(buf, off + 4);
                byte type = buf[off + 6];
                if (off + 7 + len > buf.Length) break;

                var data = new byte[len];
                Array.Copy(buf, off + 7, data, 0, len);

                var head = new byte[1 + len];
                head[0] = type;
                Array.Copy(data, 0, head, 1, len);

                if (MaskCrc(Crc32c(head)) == crc && type == 1)
                    records.Add(data);

                off += 7 + len;
            }
            return records;
        }

        private static List<byte[]> WalBatches(string dbDir)
        {
            var batches = new List<byte[]>();
            foreach (var f in Directory.GetFiles(dbDir, "*.log").OrderBy(p => p, StringComparer.Ordinal))
            {
                foreach (var batch in ScanRecords(File.ReadAllBytes(f)))
                {
                    if (batch.Length >= 12) batches.Add(batch);
                }
            }
            return batches;
        }

        private static (ulong logNumber, ulong lastSeq) ReadManifest(string dbDir)
        {
            var manifests = Directory.GetFiles(dbDir)
                .Where(f =>
                {
                    var n = Path.GetFileName(f);
                    return n.StartsWith("MANIFEST-", StringComparison.Ordinal) &&
                           n.Substring("MANIFEST-".Length).All(char.IsDigit);
                })
                .OrderBy(f => long.Parse(Path.GetFileName(f).Substring("MANIFEST-".Length)))
                .ToList();

            if (manifests.Count == 0)
                throw new InvalidDataException("No MANIFEST file found in " + dbDir);

            var buf = File.ReadAllBytes(manifests[manifests.Count - 1]);
            ulong logNumber = 0;
            ulong lastSeq = 0;

            foreach (var edit in ScanRecords(buf))
            {
                int p = 0;
                while (p < edit.Length)
                {
                    var tag = DecodeVarint(edit, p);
                    p = tag.end;
                    ulong t = tag.value;

                    if (t == 1)
                    {
                        var l = DecodeVarint(edit, p);
                        p = l.end + (int)l.value;
                    }
                    else if (t == 5)
                    {
                        p = DecodeVarint(edit, p).end;
                        var l = DecodeVarint(edit, p);
                        p = l.end + (int)l.value;
                    }
                    else if (t == 6)
                    {
                        p = DecodeVarint(edit, p).end;
                        p = DecodeVarint(edit, p).end;
                    }
                    else if (t == 7)
                    {
                        p = DecodeVarint(edit, p).end;
                        p = DecodeVarint(edit, p).end;
                        p = DecodeVarint(edit, p).end;
                        var s = DecodeVarint(edit, p);
                        p = s.end + (int)s.value;
                        var l = DecodeVarint(edit, p);
                        p = l.end + (int)l.value;
                    }
                    else if (t == 2 || t == 3 || t == 4 || t == 9)
                    {
                        var v = DecodeVarint(edit, p);
                        p = v.end;
                        if (t == 2) logNumber = v.value;
                        else if (t == 4) lastSeq = v.value;
                    }
                    else
                    {
                        throw new InvalidDataException(
                            "Unsupported manifest tag " + t + " — this leveldb layout is newer than the tool.");
                    }
                }
            }

            return (logNumber, lastSeq);
        }

        private static ulong MaxWalSequence(string dbDir)
        {
            ulong max = 0;
            foreach (var batch in WalBatches(dbDir))
            {
                ulong seq = ReadU64(batch, 0);
                if (seq > max) max = seq;
            }
            return max;
        }

        private static byte[] EncodeWriteBatch(ulong sequence, IReadOnlyList<Op> entries)
        {
            var parts = new List<byte>();

            var seq = new byte[8];
            WriteU64(seq, 0, sequence);
            parts.AddRange(seq);

            var count = new byte[4];
            WriteU32(count, 0, (uint)entries.Count);
            parts.AddRange(count);

            foreach (var e in entries)
            {
                parts.Add(1); // PUT
                WriteVarint(parts, (ulong)e.Key.Length);
                parts.AddRange(e.Key);
                WriteVarint(parts, (ulong)e.Value.Length);
                parts.AddRange(e.Value);
            }

            return parts.ToArray();
        }

        public static void AppendWal(string dbDir, IReadOnlyList<Op> ops)
        {
            var (logNumber, lastSeq) = ReadManifest(dbDir);
            var logPath = Path.Combine(dbDir, logNumber.ToString("D6") + ".log");
            ulong seq = Math.Max(lastSeq, MaxWalSequence(dbDir)) + 1000;
            var batch = EncodeWriteBatch(seq, ops);

            if (batch.Length + 7 > 32768)
                throw new InvalidOperationException("Write batch too large for one log record.");

            using (var fs = new FileStream(logPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
            {
                fs.Seek(0, SeekOrigin.End);
                long size = fs.Length;
                int off = (int)(size % 32768);
                if (off + 7 + batch.Length > 32768)
                {
                    var pad = new byte[32768 - off];
                    fs.Write(pad, 0, pad.Length);
                }

                var header = new byte[7];
                var crcInput = new byte[1 + batch.Length];
                crcInput[0] = 1;
                Array.Copy(batch, 0, crcInput, 1, batch.Length);
                WriteU32(header, 0, MaskCrc(Crc32c(crcInput)));
                header[4] = (byte)batch.Length;
                header[5] = (byte)(batch.Length >> 8);
                header[6] = 1; // FULL

                fs.Write(header, 0, header.Length);
                fs.Write(batch, 0, batch.Length);
                fs.Flush(true);
            }
        }

        // Best-effort readback of the localStorage key/value state by replaying
        // WAL records (newest sequence wins).
        public static Dictionary<string, byte[]> ReadWalState(string dbDir)
        {
            var state = new Dictionary<string, byte[]>();
            var seqOf = new Dictionary<string, ulong>();

            foreach (var batch in WalBatches(dbDir))
            {
                ulong seq = ReadU64(batch, 0);
                uint count = ReadU32(batch, 8);
                int p = 12;

                for (uint i = 0; i < count && p < batch.Length; i++)
                {
                    byte tag = batch[p++];

                    var kl = DecodeVarint(batch, p);
                    p = kl.end;
                    int keyLen = (int)kl.value;
                    if (keyLen < 0 || p + keyLen > batch.Length) break;
                    var key = new byte[keyLen];
                    Array.Copy(batch, p, key, 0, keyLen);
                    p += keyLen;

                    var vl = DecodeVarint(batch, p);
                    p = vl.end;
                    int valLen = (int)vl.value;
                    if (valLen < 0 || p + valLen > batch.Length) break;
                    var value = new byte[valLen];
                    Array.Copy(batch, p, value, 0, valLen);
                    p += valLen;

                    string name = Encoding.UTF8.GetString(key);
                    if (!seqOf.ContainsKey(name) || seqOf[name] <= seq)
                    {
                        seqOf[name] = seq;
                        state[name] = tag == 1 ? value : null;
                    }
                }
            }

            return state;
        }

        public static bool RawScan(string dbDir, string name)
        {
            var needle = Encoding.UTF8.GetBytes(name);
            foreach (var f in Directory.GetFiles(dbDir, "*.ldb"))
            {
                if (Contains(File.ReadAllBytes(f), needle)) return true;
            }
            return false;
        }

        private static bool Contains(byte[] haystack, byte[] needle)
        {
            if (needle.Length == 0) return true;
            if (needle.Length > haystack.Length) return false;
            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { match = false; break; }
                }
                if (match) return true;
            }
            return false;
        }

        // Chromium localStorage leveldb format:
        //   key   = '_file://\x00\x01' + <localStorage key name>
        //   value = 0x01 + <string bytes>
        public static byte[] LsKey(string name)
        {
            var prefix = Encoding.ASCII.GetBytes("_file://");
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var bytes = new byte[prefix.Length + 2 + nameBytes.Length];
            Array.Copy(prefix, 0, bytes, 0, prefix.Length);
            bytes[prefix.Length] = 0x00;
            bytes[prefix.Length + 1] = 0x01;
            Array.Copy(nameBytes, 0, bytes, prefix.Length + 2, nameBytes.Length);
            return bytes;
        }

        public static byte[] LsValue(string s)
        {
            var sb = Encoding.UTF8.GetBytes(s);
            var bytes = new byte[1 + sb.Length];
            bytes[0] = 0x01;
            Array.Copy(sb, 0, bytes, 1, sb.Length);
            return bytes;
        }
    }
}
