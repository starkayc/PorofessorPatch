using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace PorofessorPatch.Core
{
    internal sealed class CheckResult
    {
        public bool Running;
        public string StorageDir;
        public bool StorageExists;
        public bool AdsRemoved;
        public bool PorofessorPremium;
        public bool OverwolfPremium;
    }

    internal sealed class OperationResult
    {
        public string Message;
        public string BackupPath;
        public List<string> Written = new List<string>();
    }

    internal static class PatchEngine
    {
        private static readonly Dictionary<string, string> OnKeys = new Dictionary<string, string>
        {
            { "isOWPremium", "{\"isPremium\":true}" },
            { "isPremium", "true" },
        };

        private static readonly Dictionary<string, string> OffKeys = new Dictionary<string, string>
        {
            { "isOWPremium", "{\"isPremium\":false}" },
            { "isPremium", "false" },
        };

        public static bool IsAppRunning()
        {
            try { return Process.GetProcessesByName(Paths.ExeName).Length > 0; }
            catch { return false; }
        }

        private static void AssertStorage()
        {
            if (!Directory.Exists(Paths.StorageDir))
                throw new InvalidOperationException(
                    "Storage not found: " + Paths.StorageDir + Environment.NewLine +
                    "Launch Porofessor once (so it creates its profile), close it, then try again.");
        }

        private static void AssertAppClosed()
        {
            if (IsAppRunning())
                throw new InvalidOperationException("Close Porofessor first — it is still running.");
        }

        private static string BackupStorage()
        {
            var dest = Paths.MakeBackupDir();
            Directory.CreateDirectory(dest);
            foreach (var f in Directory.GetFiles(Paths.StorageDir))
                File.Copy(f, Path.Combine(dest, Path.GetFileName(f)));
            return dest;
        }

        private static List<LevelDb.Op> MakeOps(Dictionary<string, string> keys)
        {
            var ops = new List<LevelDb.Op>();
            foreach (var kv in keys)
                ops.Add(new LevelDb.Op { Key = LevelDb.LsKey(kv.Key), Value = LevelDb.LsValue(kv.Value) });
            return ops;
        }

        private static List<string> WrittenLines(Dictionary<string, string> keys)
        {
            bool pr = keys["isPremium"] == "true";
            bool ow = keys["isOWPremium"].Contains("true");
            return new List<string>
            {
                "Porofessor Premium = " + (pr ? "true" : "false"),
                "Overwolf Premium = " + (ow ? "true" : "false"),
            };
        }

        public static OperationResult Patch()
        {
            AssertStorage();
            AssertAppClosed();
            var backup = BackupStorage();
            LevelDb.AppendWal(Paths.StorageDir, MakeOps(OnKeys));
            return new OperationResult
            {
                BackupPath = backup,
                Message = "Patch applied — the ad is removed. Relaunch Porofessor to see it.",
                Written = WrittenLines(OnKeys),
            };
        }

        public static OperationResult Restore()
        {
            AssertStorage();
            AssertAppClosed();
            var backup = BackupStorage();
            LevelDb.AppendWal(Paths.StorageDir, MakeOps(OffKeys));
            return new OperationResult
            {
                BackupPath = backup,
                Message = "Patch removed — the ad is back.",
                Written = WrittenLines(OffKeys),
            };
        }

        public static CheckResult Check()
        {
            var r = new CheckResult
            {
                Running = IsAppRunning(),
                StorageDir = Paths.StorageDir,
                StorageExists = Directory.Exists(Paths.StorageDir),
            };
            if (!r.StorageExists) return r;

            var wal = LevelDb.ReadWalState(Paths.StorageDir);

            var prName = Encoding.UTF8.GetString(LevelDb.LsKey("isPremium"));
            wal.TryGetValue(prName, out var prVal);
            r.PorofessorPremium = IsTrue(prVal);

            var owName = Encoding.UTF8.GetString(LevelDb.LsKey("isOWPremium"));
            bool owPresent = wal.TryGetValue(owName, out var owVal);
            bool owPremium = owVal != null && Encoding.UTF8.GetString(owVal).Contains("\"isPremium\":true");
            r.OverwolfPremium = owPremium;

            bool diskOwd = LevelDb.RawScan(Paths.StorageDir, "isOWPremium");

            r.AdsRemoved = owPresent ? owPremium : diskOwd;
            return r;
        }

        private static bool IsTrue(byte[] v)
        {
            if (v == null || v.Length <= 1) return false;
            return Encoding.UTF8.GetString(v, 1, v.Length - 1) == "true";
        }
    }
}
