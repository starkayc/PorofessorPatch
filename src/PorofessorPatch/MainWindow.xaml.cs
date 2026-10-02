using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PorofessorPatch.Core;

namespace PorofessorPatch
{
    public partial class MainWindow : Window
    {
        private readonly List<Install> _installs = new List<Install>();
        private bool _dark;
        private string _actionLog = string.Empty;
        private string _statusSummary = string.Empty;

        private static readonly Color Green = (Color)ColorConverter.ConvertFromString("#34C759");
        private static readonly Color Amber = (Color)ColorConverter.ConvertFromString("#FF9F0A");
        private static readonly Color Red = (Color)ColorConverter.ConvertFromString("#FF3B30");

        public MainWindow()
        {
            InitializeComponent();
            VersionText.Text = "v" + AppInfo.Version;
            LoadMascot();
            RefreshInstalls();
            RefreshStatus();
        }

        private void LoadMascot()
        {
            try
            {
                var uri = new Uri("pack://application:,,,/Assets/icon.ico", UriKind.Absolute);
                var stream = Application.GetResourceStream(uri)?.Stream;
                if (stream == null) return;

                var decoder = new IconBitmapDecoder(
                    stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault();
                if (frame != null) MascotImage.Source = frame;
            }
            catch
            {
                // mascot is cosmetic — the app still works without it
            }
        }

        private void Mascot_Click(object sender, MouseButtonEventArgs e)
        {
            ToggleTheme();
        }

        private void ToggleTheme()
        {
            _dark = !_dark;
            var merged = Application.Current.Resources.MergedDictionaries;
            merged.Clear();
            merged.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Themes/" + (_dark ? "Dark" : "Light") + ".xaml"),
            });
        }

        private void RefreshInstalls()
        {
            try
            {
                _installs.Clear();
                _installs.AddRange(InstallDetector.Detect());
                InstallList.ItemsSource = null;
                InstallList.ItemsSource = _installs;
                if (_installs.Count > 0) InstallList.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                AppendLog("error = " + ex.Message);
            }
        }

        private void SetStatus(string text, Color color)
        {
            StatusText.Text = text;
            StatusText.Foreground = new SolidColorBrush(color);
            StatusDot.Fill = new SolidColorBrush(color);
        }

        private void AppendLog(string text)
        {
            _actionLog += text + Environment.NewLine;
            RenderLog();
        }

        private void RefreshStatus()
        {
            CheckResult r;
            try
            {
                r = PatchEngine.Check();
            }
            catch (Exception ex)
            {
                r = new CheckResult { Running = PatchEngine.IsAppRunning(), StorageDir = Paths.StorageDir };
                AppendLog("error = " + ex.Message);
            }

            if (r.Running)
                SetStatus("Porofessor is running — close it to patch", Amber);
            else if (!r.StorageExists)
                SetStatus("No data yet — launch Porofessor once", Amber);
            else if (r.AdsRemoved)
                SetStatus("Ads removed", Green);
            else
                SetStatus("Ads present", Red);

            _statusSummary =
                "Porofessor Status: " + (r.Running ? "Running" : "Not Running") + Environment.NewLine +
                "Database Location: " + r.StorageDir + Environment.NewLine +
                "Backups: " + Paths.BackupsRoot + Environment.NewLine +
                "Patched: " + (r.AdsRemoved ? "Yes" : "No") + Environment.NewLine +
                "  Porofessor Premium: " + (r.PorofessorPremium ? "Yes" : "No") + Environment.NewLine +
                "  Overwolf Premium: " + (r.OverwolfPremium ? "Yes" : "No");

            RenderLog();
        }

        private void RenderLog()
        {
            LogBox.Text = string.IsNullOrEmpty(_actionLog)
                ? _statusSummary
                : _actionLog + Environment.NewLine + _statusSummary;
            LogBox.ScrollToEnd();
        }

        private void Patch_Click(object sender, RoutedEventArgs e)
        {
            Run(PatchEngine.Patch);
        }

        private void Restore_Click(object sender, RoutedEventArgs e)
        {
            Run(PatchEngine.Restore);
        }

        private void Run(Func<OperationResult> action)
        {
            try
            {
                var r = action();
                AppendLog(r.Message);
                foreach (var line in r.Written) AppendLog("  " + line);
                if (!string.IsNullOrEmpty(r.BackupPath)) AppendLog("backup = " + r.BackupPath);
                RefreshStatus();
            }
            catch (Exception ex)
            {
                AppendLog("error = " + ex.Message);
                MessageBox.Show(this, ex.Message, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshInstalls();
            RefreshStatus();
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var owner = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var path = FolderPicker.Pick("Select the Porofessor Standalone install folder", owner);
            if (string.IsNullOrEmpty(path)) return;

            var inst = new Install
            {
                DisplayName = "Custom",
                InstallPath = path,
                ExePath = Path.Combine(path, Paths.ExeName + ".exe"),
            };
            _installs.Insert(0, inst);
            InstallList.ItemsSource = null;
            InstallList.ItemsSource = _installs;
            InstallList.SelectedIndex = 0;
            RefreshStatus();
        }

        private void ClearLogs_Click(object sender, RoutedEventArgs e)
        {
            _actionLog = string.Empty;
            RenderLog();
        }
    }
}
