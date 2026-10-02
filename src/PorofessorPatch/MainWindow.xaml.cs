using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
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
        private CheckResult _lastCheck;
        private string _lastBackup;

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
            RenderStatus();
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
            catch
            {
            }
        }

        private void SetStatus(string text, Color color)
        {
            StatusText.Text = text;
            StatusText.Foreground = new SolidColorBrush(color);
            StatusDot.Fill = new SolidColorBrush(color);
        }

        private void RefreshStatus()
        {
            try
            {
                _lastCheck = PatchEngine.Check();
            }
            catch
            {
                _lastCheck = new CheckResult
                {
                    Running = PatchEngine.IsAppRunning(),
                    StorageDir = Paths.StorageDir,
                };
            }

            if (_lastCheck.Running)
                SetStatus("Porofessor is running — close it to patch", Amber);
            else if (!_lastCheck.StorageExists)
                SetStatus("No data yet — launch Porofessor once", Amber);
            else if (_lastCheck.AdsRemoved)
                SetStatus("Ads Removed", Green);
            else
                SetStatus("Ads Present", Red);

            RenderStatus();
        }

        private void RenderStatus()
        {
            var r = _lastCheck;
            var doc = new FlowDocument
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12.5,
                PagePadding = new Thickness(0),
            };

            AddLine(doc, "Porofessor Status: ", r.Running ? "Running" : "Not Running",
                new SolidColorBrush(r.Running ? Amber : Green));

            if (r.Running)
            {
                AddLine(doc, "Patch Status: ", "Unknown", new SolidColorBrush(Amber));
                AddLine(doc, "  Porofessor Premium: ", "Unknown", new SolidColorBrush(Amber));
                AddLine(doc, "  Overwolf Premium: ", "Unknown", new SolidColorBrush(Amber));
            }
            else
            {
                AddLine(doc, "Patch Status: ", r.AdsRemoved ? "Ads Removed" : "Ads Present",
                    new SolidColorBrush(r.AdsRemoved ? Green : Red));
                AddLine(doc, "  Porofessor Premium: ", r.PorofessorPremium ? "Yes" : "No", null);
                AddLine(doc, "  Overwolf Premium: ", r.OverwolfPremium ? "Yes" : "No", null);
            }

            LogBox.Document = doc;
        }

        private static void AddLine(FlowDocument doc, string label, string value, Brush valueBrush)
        {
            var p = new Paragraph { Margin = new Thickness(0) };
            p.Inlines.Add(new Run(label));
            var run = new Run(value);
            if (valueBrush != null) run.Foreground = valueBrush;
            p.Inlines.Add(run);
            doc.Blocks.Add(p);
        }

        private static void OpenFolder(string path)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }

        private void OpenDatabase_Click(object sender, RoutedEventArgs e)
        {
            if (Directory.Exists(Paths.StorageDir))
                OpenFolder(Paths.StorageDir);
            else
                MessageBox.Show(this, "Porofessor's storage isn't created yet — launch Porofessor once.",
                    AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OpenBackups_Click(object sender, RoutedEventArgs e)
        {
            var dir = string.IsNullOrEmpty(_lastBackup) ? Paths.BackupsRoot : _lastBackup;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            OpenFolder(dir);
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
                if (!string.IsNullOrEmpty(r.BackupPath)) _lastBackup = r.BackupPath;
                RefreshStatus();
            }
            catch (Exception ex)
            {
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
    }
}
