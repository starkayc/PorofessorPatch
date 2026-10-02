using System;
using System.IO;

namespace PorofessorPatch.Core
{
    internal static class AppInfo
    {
        public const string Name = "PorofessorPatch";
        public const string Version = "2.0.0";
    }

    internal static class Paths
    {
        public const string ExeName = "Porofessor Standalone";

        public static string StorageDir
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("POROFESSOR_STORAGE_DIR");
                if (!string.IsNullOrEmpty(env)) return env;
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Porofessor Standalone",
                    "Local Storage",
                    "leveldb");
            }
        }

        public static string BackupsRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PorofessorPatch",
            "backups");

        public static string MakeBackupDir()
        {
            var stamp = DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss");
            return Path.Combine(BackupsRoot, "storage-" + stamp);
        }
    }
}
