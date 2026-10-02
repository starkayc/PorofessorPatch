using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace PorofessorPatch.Core
{
    internal sealed class Install
    {
        public string DisplayName;
        public string InstallPath;
        public string ExePath;

        public bool Exists => File.Exists(ExePath);
        public string Label => DisplayName + "  —  " + InstallPath;
        public override string ToString() => Label;
    }

    internal static class InstallDetector
    {
        public static List<Install> Detect()
        {
            var map = new Dictionary<string, Install>(StringComparer.OrdinalIgnoreCase);

            void Add(string exePath, string displayName, string installPath)
            {
                if (string.IsNullOrEmpty(exePath)) return;
                string full = Path.GetFullPath(exePath);
                if (!map.ContainsKey(full))
                    map[full] = new Install { ExePath = full, DisplayName = displayName, InstallPath = installPath };
            }

            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            const string exe = "Porofessor Standalone.exe";
            const string dir = "Porofessor Standalone";

            Add(Path.Combine(local, "Programs", dir, exe), dir, Path.Combine(local, "Programs", dir));
            Add(Path.Combine(pf, dir, exe), dir, Path.Combine(pf, dir));
            Add(Path.Combine(pf86, dir, exe), dir, Path.Combine(pf86, dir));

            // Scan %LOCALAPPDATA%\Programs for any folder containing the exe.
            var localPrograms = Path.Combine(local, "Programs");
            if (Directory.Exists(localPrograms))
            {
                foreach (var sub in Directory.GetDirectories(localPrograms))
                {
                    var candidate = Path.Combine(sub, exe);
                    if (File.Exists(candidate))
                        Add(candidate, Path.GetFileName(sub), sub);
                }
            }

            // Registry uninstall keys (HKLM 64/32 + HKCU).
            foreach (var root in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using (var baseKey = RegistryKey.OpenBaseKey(root, view))
                        using (var uninstall = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall"))
                        {
                            if (uninstall == null) continue;
                            foreach (var subName in uninstall.GetSubKeyNames())
                            {
                                using (var sub = uninstall.OpenSubKey(subName))
                                {
                                    var display = sub?.GetValue("DisplayName") as string;
                                    if (string.IsNullOrEmpty(display) ||
                                        display.IndexOf("Porofessor", StringComparison.OrdinalIgnoreCase) < 0)
                                        continue;

                                    var loc = sub.GetValue("InstallLocation") as string;
                                    var icon = CleanExePath(sub.GetValue("DisplayIcon") as string);

                                    if (!string.IsNullOrEmpty(icon) && File.Exists(icon))
                                        Add(icon, display, loc);
                                    else if (!string.IsNullOrEmpty(loc))
                                        Add(Path.Combine(loc, exe), display, loc);
                                }
                            }
                        }
                    }
                    catch
                    {
                        // some hives/views are unavailable — skip
                    }
                }
            }

            return map.Values.Where(i => i.Exists)
                             .OrderBy(i => i.InstallPath, StringComparer.OrdinalIgnoreCase)
                             .ToList();
        }

        private static string CleanExePath(string p)
        {
            if (string.IsNullOrEmpty(p)) return null;
            p = p.Trim();
            if (p.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = p.IndexOf('"', 1);
                if (end > 0) p = p.Substring(1, end - 1);
            }
            int comma = p.IndexOf(',');
            if (comma >= 0) p = p.Substring(0, comma);
            return p;
        }
    }
}
