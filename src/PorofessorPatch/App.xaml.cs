using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using PorofessorPatch.Core;

namespace PorofessorPatch
{
    public partial class App : Application
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint dwProcessId);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private const uint AttachParentProcess = 0xFFFFFFFF;
        private const string InstanceMutexName = "PorofessorPatch.SingleInstance";
        private const int SwRestore = 9;

        private bool _console;
        private Mutex _instanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            AppDomain.CurrentDomain.UnhandledException += (s, ex) => LogCrash(ex.ExceptionObject as Exception);
            DispatcherUnhandledException += (s, ex) =>
            {
                LogCrash(ex.Exception);
                ex.Handled = true;
                Shutdown(-1);
            };

            _console = AttachConsole(AttachParentProcess);
            var args = e.Args;

            if (args.Contains("--version"))
            {
                Out(AppInfo.Name + " v" + AppInfo.Version);
                Environment.Exit(0);
            }
            else if (args.Contains("--check"))
            {
                try
                {
                    var r = PatchEngine.Check();
                    Out("Running: " + (r.Running ? "yes" : "no"));
                    Out("Storage: " + r.StorageDir);
                    Out("Ads:     " + (r.AdsRemoved ? "removed (premium flag set)" : "present"));
                    Environment.Exit(0);
                }
                catch (Exception ex)
                {
                    Out("FAILED: " + ex.Message);
                    Environment.Exit(1);
                }
            }
            else if (args.Contains("--patch") || args.Contains("--restore"))
            {
                try
                {
                    var r = args.Contains("--patch") ? PatchEngine.Patch() : PatchEngine.Restore();
                    Out(r.Message);
                    if (!string.IsNullOrEmpty(r.BackupPath)) Out("Backup: " + r.BackupPath);
                    Environment.Exit(0);
                }
                catch (Exception ex)
                {
                    Out("FAILED: " + ex.Message);
                    Environment.Exit(1);
                }
            }
            else
            {
                if (!AcquireSingleInstance())
                {
                    ActivateExistingWindow();
                    Shutdown();
                    return;
                }

                try
                {
                    var w = new MainWindow();
                    MainWindow = w;
                    w.Show();
                }
                catch (Exception ex)
                {
                    LogCrash(ex);
                    Shutdown(-1);
                }
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            ReleaseSingleInstance();
            base.OnExit(e);
        }

        private bool AcquireSingleInstance()
        {
            _instanceMutex = new Mutex(false, InstanceMutexName, out bool createdNew);
            if (createdNew)
            {
                _instanceMutex.WaitOne();
                return true;
            }

            try
            {
                return _instanceMutex.WaitOne(TimeSpan.Zero, false);
            }
            catch (AbandonedMutexException)
            {
                return true;
            }
        }

        private void ReleaseSingleInstance()
        {
            if (_instanceMutex == null) return;
            try
            {
                _instanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // not held by this instance (e.g. abandoned) — nothing to release
            }
            _instanceMutex.Dispose();
            _instanceMutex = null;
        }

        private static void ActivateExistingWindow()
        {
            var me = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(me.ProcessName))
            {
                if (p.Id == me.Id || p.MainWindowHandle == IntPtr.Zero) continue;
                ShowWindow(p.MainWindowHandle, SwRestore);
                SetForegroundWindow(p.MainWindowHandle);
                return;
            }
        }

        private void Out(string text)
        {
            if (_console) Console.WriteLine(text);
        }

        private static void LogCrash(Exception ex)
        {
            if (ex == null) return;
            try
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PorofessorPatch");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(dir, "crash.log"),
                    DateTime.Now.ToString("o") + " " + ex + Environment.NewLine + Environment.NewLine);
            }
            catch
            {
            }
        }
    }
}
