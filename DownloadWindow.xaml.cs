using Microsoft.Web.WebView2.Core;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace TNovDesktop
{
    public partial class DownloadWindow : Window
    {
        private static readonly List<DownloadWindow> OpenWindows = new();

        private readonly CoreWebView2DownloadOperation _op;
        private readonly Dispatcher _ui;
        private string _path;
        private bool _uiQueued;
        private bool _closed;

        public DownloadWindow(CoreWebView2DownloadOperation op, string path)
        {
            InitializeComponent();
            _op = op;
            _path = path;
            _ui = Dispatcher;

            FileNameText.Text = string.IsNullOrWhiteSpace(path)
                ? "Файл"
                : Path.GetFileName(path);

            Loaded += (_, _) => RelayoutAll();
            Closed += OnClosed;

            _op.BytesReceivedChanged += OnDownloadChanged;
            _op.StateChanged += OnDownloadChanged;
            Refresh();
        }

        public static void Show(CoreWebView2DownloadOperation op, string path, Dispatcher dispatcher)
        {
            dispatcher.BeginInvoke(() =>
            {
                var window = new DownloadWindow(op, path);
                OpenWindows.Add(window);
                window.Show();
                RelayoutAll();
            });
        }

        private void OnDownloadChanged(object? sender, object e) => QueueRefresh();

        private void QueueRefresh()
        {
            if (_closed || _uiQueued)
                return;
            _uiQueued = true;
            _ui.BeginInvoke(() =>
            {
                _uiQueued = false;
                if (!_closed)
                    Refresh();
            });
        }

        private void Refresh()
        {
            if (!string.IsNullOrWhiteSpace(_op.ResultFilePath))
                _path = _op.ResultFilePath;

            string name = Path.GetFileName(_path);
            if (!string.IsNullOrWhiteSpace(name))
                FileNameText.Text = name;

            long received = _op.BytesReceived;
            ulong? totalBytes = _op.TotalBytesToReceive;
            bool hasTotal = totalBytes.GetValueOrDefault() > 0;

            switch (_op.State)
            {
                case CoreWebView2DownloadState.InProgress:
                    StatusText.Text = hasTotal
                        ? $"{FormatSize(received)} из {FormatSize((long)totalBytes!.Value)}"
                        : FormatSize(received);
                    if (hasTotal)
                    {
                        Progress.IsIndeterminate = false;
                        Progress.Value = Math.Min(100, received * 100.0 / totalBytes!.Value);
                    }
                    else
                    {
                        Progress.IsIndeterminate = true;
                    }
                    OpenFileButton.IsEnabled = false;
                    OpenFolderButton.IsEnabled = DirectoryExists();
                    break;

                case CoreWebView2DownloadState.Completed:
                    Progress.IsIndeterminate = false;
                    Progress.Value = 100;
                    StatusText.Text = "Готово";
                    OpenFileButton.IsEnabled = File.Exists(_path);
                    OpenFolderButton.IsEnabled = DirectoryExists();
                    break;

                case CoreWebView2DownloadState.Interrupted:
                    Progress.IsIndeterminate = false;
                    StatusText.Text = "Загрузка прервана";
                    StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0xE5, 0x73, 0x73));
                    OpenFileButton.IsEnabled = File.Exists(_path);
                    OpenFolderButton.IsEnabled = DirectoryExists();
                    break;
            }
        }

        private bool DirectoryExists()
        {
            string? dir = Path.GetDirectoryName(_path);
            return !string.IsNullOrEmpty(dir) && Directory.Exists(dir);
        }

        private void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            if (!File.Exists(_path))
            {
                StatusText.Text = "Файл не найден";
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(_path) { UseShellExecute = true });
                Close();
            }
            catch (Exception ex)
            {
                Log.Write($"[WEBVIEW2] Open file failed: {ex.Message}");
                StatusText.Text = "Не удалось открыть файл";
            }
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (File.Exists(_path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_path}\"")
                    {
                        UseShellExecute = true
                    });
                }
                else
                {
                    string? dir = Path.GetDirectoryName(_path);
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                    {
                        StatusText.Text = "Папка не найдена";
                        return;
                    }
                    Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
                }
                Close();
            }
            catch (Exception ex)
            {
                Log.Write($"[WEBVIEW2] Open folder failed: {ex.Message}");
                StatusText.Text = "Не удалось открыть папку";
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void OnClosed(object? sender, EventArgs e)
        {
            if (_closed)
                return;
            _closed = true;
            _op.BytesReceivedChanged -= OnDownloadChanged;
            _op.StateChanged -= OnDownloadChanged;
            OpenWindows.Remove(this);
            RelayoutAll();
        }

        private static void RelayoutAll()
        {
            Rect work = SystemParameters.WorkArea;
            try
            {
                var main = Application.Current?.MainWindow;
                if (main != null)
                    work = WindowWorkArea.Get(main);
            }
            catch { }

            double bottom = work.Bottom - 10;
            for (int i = OpenWindows.Count - 1; i >= 0; i--)
            {
                var w = OpenWindows[i];
                double width = Math.Max(1, w.ActualWidth);
                double height = Math.Max(1, w.ActualHeight);
                w.Left = Math.Max(work.Left, work.Right - width - 10);
                w.Top = Math.Max(work.Top, bottom - height);
                bottom = w.Top - 4;
            }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 0) bytes = 0;
            const double kb = 1024;
            const double mb = kb * 1024;
            const double gb = mb * 1024;
            if (bytes >= gb) return $"{bytes / gb:0.0} ГБ";
            if (bytes >= mb) return $"{bytes / mb:0.0} МБ";
            if (bytes >= kb) return $"{bytes / kb:0.0} КБ";
            return $"{bytes} Б";
        }
    }
}
