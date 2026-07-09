using Microsoft.Toolkit.Uwp.Notifications;
using Newtonsoft.Json;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TNovClient;
using MessageBox = System.Windows.MessageBox;

namespace TNovDesktop
{
    public partial class MainWindow : Window
    {
        [DllImport("shell32.dll", SetLastError = true)]
        private static extern void SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string AppID);

        private string pendingConversationId = null;
        public static string novaserver = "//fs-nova/Distr/0.For Admin/";
        private DispatcherTimer _tabHoverTimer;
        private TabItem _hoveredTabItem;
        private DispatcherTimer _taskMonitorTimer;
        private DateTime _lastTaskCheckTime;
        private Mutex _appMutex;
        private DispatcherTimer _updateCheckTimer;
        private bool _isCheckingUpdate = false;
        private const string UpdateFolderPath = @"\\fs-nova\Distr\0.For Admin\_TNov\actual\desktop\";

        public MainWindow()
        {
            // Проверка на единственный экземпляр
            if (!CheckSingleInstance())
            {
                // Если приложение уже запущено — завершаем текущий процесс
                Environment.Exit(0);
                return;
            }
            InitializeComponent();

            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            VersionTextBlock.Text = version.ToString();

            if (File.Exists(novaserver + "_TNov/desktopusage.txt"))
            {
                string usagefilePath = novaserver + "_TNov/desktopusage.txt";
                string date = DateTime.Now.ToString().Replace(":", "-");
                string TNovVersion = version?.ToString() ?? "";
                try
                {
                    File.AppendAllText(usagefilePath, "\n" + date + "," + Environment.UserName + "," + TNovVersion);
                }
                catch { }
            }

            try
            {
                var iconUri = new Uri("pack://application:,,,/Resources/logo.ico");
                Icon = BitmapFrame.Create(iconUri);
            }
            catch { }

            try
            {
                var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Resources/logotray.ico")).Stream;
                TrayIcon.Icon = new System.Drawing.Icon(stream);
            }
            catch { }

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;

            // Подписываемся на запросы показа уведомлений от контрола
            MessengerControl.ShowToastRequested += OnMessengerToastRequested;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            SetCurrentProcessExplicitAppUserModelID("TNov.TNovDesktop");
            InitializeNotificationAppId();
            SetupTabHoverSwitch();
            StartTaskMonitor();
            StartPeriodicUpdateCheck();
        }

        private void OnMessengerToastRequested(string title, string body, string conversationId)
        {
            pendingConversationId = conversationId;
            ShowToastNotification(title, body);
        }



        #region Переключение на вкладку при наведении

        private void SetupTabHoverSwitch()
        {
            _tabHoverTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _tabHoverTimer.Tick += (s, e) =>
            {
                _tabHoverTimer.Stop();
                if (_hoveredTabItem != null)
                {
                    _hoveredTabItem.IsSelected = true;
                    _hoveredTabItem = null;
                }
            };

            foreach (object item in MainTabControl.Items)
            {
                if (item is TabItem tabItem)
                {
                    tabItem.MouseEnter += TabItem_MouseEnter;
                    tabItem.MouseLeave += TabItem_MouseLeave;
                }
            }
        }

        private void TabItem_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is TabItem tabItem)
            {
                _hoveredTabItem = tabItem;
                _tabHoverTimer.Start();
            }
        }

        private void TabItem_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _tabHoverTimer.Stop();
            _hoveredTabItem = null;
        }

        #endregion

        #region Уведомления (Toast)

        private void InitializeNotificationAppId()
        {
            const string appId = "TNov.TNovDesktop";
            SetCurrentProcessExplicitAppUserModelID(appId);

            string shortcutPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs",
                "TNovDesktop.lnk");

            if (!File.Exists(shortcutPath))
            {
                string exePath = Process.GetCurrentProcess().MainModule.FileName;
                IWshRuntimeLibrary.WshShell shell = new IWshRuntimeLibrary.WshShell();
                IWshRuntimeLibrary.IWshShortcut shortcut = (IWshRuntimeLibrary.IWshShortcut)shell.CreateShortcut(shortcutPath);
                shortcut.TargetPath = exePath;
                shortcut.WorkingDirectory = Path.GetDirectoryName(exePath);
                shortcut.Description = "TNovDesktop — ваш удобный мессенджер";
                shortcut.Save();
            }

            ToastNotificationManagerCompat.OnActivated += OnToastActivated;
        }

        private void ShowToastNotification(string title, string message)
        {
            try
            {
                NotificationWindow notificationWindow = new NotificationWindow();
                notificationWindow.ShowNotification(title, message);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{title}\n\n{message}", "TNovDesktop", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
        {
            var args = ToastArguments.Parse(e.Argument);
            if (args.TryGetValue("action", out string action) && action == "navigateToProjects")
            {
                Dispatcher.Invoke(() => NavigateToProjectsAndRefresh());
            }
            else
            {
                Dispatcher.Invoke(() => ShowMainWindow());
            }
        }

        public void NavigateToProjectsAndRefresh()
        {
            // Ищем вкладку «Задания» по заголовку (может быть TextBlock в StackPanel)
            foreach (TabItem item in MainTabControl.Items)
            {
                string header = null;
                if (item.Header is string s)
                    header = s;
                else if (item.Header is StackPanel sp)
                    header = sp.Children.OfType<TextBlock>().FirstOrDefault()?.Text;

                if (header == "Задания")
                {
                    item.IsSelected = true;
                    break;
                }
            }
            // Принудительно обновляем данные
            ProjectControl.RefreshData();
        }

        #endregion

        #region Системный трей и управление окном

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            e.Cancel = true;
            Hide();
            TrayIcon.Visibility = Visibility.Visible;
        }

        private void TrayIcon_TrayLeftMouseDown(object sender, RoutedEventArgs e)
        {
            ShowMainWindow();
        }

        private void ShowWindow_Click(object sender, RoutedEventArgs e)
        {
            ShowMainWindow();
        }

        private void ExitApp_Click(object sender, RoutedEventArgs e)
        {
            TrayIcon.Dispose();
            Application.Current.Shutdown();
        }

        private void ShowMainWindow()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            TrayIcon.Visibility = Visibility.Collapsed;

            if (!string.IsNullOrEmpty(pendingConversationId))
            {
                string chatId = pendingConversationId;
                pendingConversationId = null;
                MessengerControl.NavigateToChat(chatId);
            }
        }

        private bool CheckSingleInstance()
        {
            const string mutexName = "TNovDesktop_SingleInstance_1234"; // Уникальный идентификатор
            bool createdNew;
            _appMutex = new Mutex(true, mutexName, out createdNew);

            Log.Write($"[STARTUP] exe={Environment.ProcessPath} primaryInstance={createdNew}");

            if (createdNew)
            {
                // Приложение — первый экземпляр. Мьютекс будет удерживаться до выхода.
                return true;
            }

            // Уже запущен другой экземпляр — активируем его окно
            Log.Write("[STARTUP] Обнаружен уже запущенный экземпляр — активирую его и выхожу.");
            ActivateExistingWindow();
            return false;
        }

        private static void ActivateExistingWindow()
        {
            IntPtr hWnd = FindWindow(null, "TNovDesktop");
            if (hWnd != IntPtr.Zero)
            {
                ShowWindow(hWnd, SW_RESTORE);
                SetForegroundWindow(hWnd);
            }
        }
        // WinAPI объявления
        [DllImport("user32.dll")]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private const int SW_RESTORE = 9;

        #endregion

        #region Кастомный заголовок и кнопки

        private void Border_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
                DragMove();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Hide();
            TrayIcon.Visibility = Visibility.Visible;
        }

        private void HideButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaxButton_Click(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            else WindowState = WindowState.Maximized;
        }

        private void OpenWebsite_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://tnov.pm-nova.ru",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось открыть сайт: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MessengerTabRefresh_Click(object sender, RoutedEventArgs e)
        {
            // Если MessengerControl имеет публичный доступ к WebView2 (например, свойство WebView)
            MessengerControl.WebView?.Reload();
            // Если WebView2 недоступен напрямую, создайте в контроле публичный метод Refresh()
            // MessengerControl.Refresh();
        }
        private void ProjectsTabRefresh_Click(object sender, RoutedEventArgs e)
        {
            ProjectControl.RefreshData();
        }
        private void Vitro22TabRefresh_Click(object sender, RoutedEventArgs e)
        {
            Vitro22Control.WebView?.Reload();
        }
        private void Vitro25TabRefresh_Click(object sender, RoutedEventArgs e)
        {
            Vitro25Control.WebView?.Reload();
        }
        private void TimettaTabRefresh_Click(object sender, RoutedEventArgs e)
        {
            TimettaControl.WebView?.Reload();
        }
        private void MailTabRefresh_Click(object sender, RoutedEventArgs e)
        {
            MailControl.WebView?.Reload();
        }
        private void TProTabRefresh_Click(object sender, RoutedEventArgs e)
        {
            TProControl.WebView?.Reload();
        }
        private void YougileTabRefresh_Click(object sender, RoutedEventArgs e)
        {
            YougileControl.WebView?.Reload();
        }

        #endregion

        #region Мониторинг новых заданий
        private void StartTaskMonitor()
        {
            _lastTaskCheckTime = DateTime.Now; // начальная точка – сейчас
            _taskMonitorTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _taskMonitorTimer.Tick += async (s, e) => await CheckForNewTasksAsync();
            _taskMonitorTimer.Start();
        }
        private async Task CheckForNewTasksAsync()
        {
            string taskFolder = @"\\fs-nova\Distr\0.For Admin\_TNov\tasks\";
            if (!Directory.Exists(taskFolder))
                return;

            DateTime checkEnd = DateTime.Now;
            DateTime checkStart = checkEnd.AddMinutes(-1);

            var newAssignments = new Dictionary<string, List<HoleGroupBaseItem>>();

            try
            {
                var jsonFiles = Directory.GetFiles(taskFolder, "*.json", SearchOption.TopDirectoryOnly);

                foreach (var file in jsonFiles)
                {
                    FileInfo fi = new FileInfo(file);
                    // Обрабатываем только файлы, изменённые после последней проверки
                    if (fi.LastWriteTime < _lastTaskCheckTime)
                        continue;

                    try
                    {
                        string json = File.ReadAllText(file);
                        var items = JsonConvert.DeserializeObject<List<HoleGroupBaseItem>>(json)
                                    ?? new List<HoleGroupBaseItem>();
                        string modelName = Path.GetFileNameWithoutExtension(file);

                        var recent = items.Where(item =>
                        {
                            if (string.IsNullOrEmpty(item.TaskDate)) return false;
                            return DateTime.TryParse(item.TaskDate, out DateTime dt) && dt >= checkStart && dt <= checkEnd;
                        }).ToList();

                        if (recent.Any())
                            newAssignments[modelName] = recent;
                    }
                    catch { /* игнорируем повреждённые файлы */ }
                }
            }
            catch { /* ошибка доступа к папке */ }

            _lastTaskCheckTime = checkEnd;

            // Формируем уведомления
            foreach (var kvp in newAssignments)
            {
                string model = kvp.Key;
                var items = kvp.Value;
                var taskDescs = new List<string>();
                string verb = "";

                foreach (var item in items)
                {
                    int version;
                    int.TryParse(item.TaskVersion, out version);
                    verb = (version > 1) ? "обновлены" : "выданы";
                    taskDescs.Add($"{item.HoleGroupName} v{item.TaskVersion}");
                }

                string title = $"{model} {verb} задания:";
                string message = string.Join(", ", taskDescs);

                NotificationWindow notificationWindow = new NotificationWindow();
                notificationWindow.ShowNotification(title, message);
            }
        }
        #endregion
        #region Проверка обновлений
        private void StartPeriodicUpdateCheck()
        {
            _updateCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
            _updateCheckTimer.Tick += async (s, e) => await CheckForUpdatePeriodicallyAsync();
            _updateCheckTimer.Start();
        }

        private async Task CheckForUpdatePeriodicallyAsync()
        {
            if (_isCheckingUpdate) return;
            _isCheckingUpdate = true;
            try
            {
                bool hasUpdate = await Task.Run(() => CheckForUpdateInFolder());
                if (hasUpdate)
                {
                    Dispatcher.Invoke(() =>
                    {
                        ShowMainWindow();
                        var result = MessageBox.Show(
                            "Доступна новая версия приложения. Установить сейчас?",
                            "Обновление TNovDesktop",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Information);

                        if (result == MessageBoxResult.Yes)
                        {
                            // Запускаем обновление (например, setup.exe или .application файл)
                            string installerPath = Path.Combine(UpdateFolderPath, "TNovDesktop.application");
                            if (File.Exists(installerPath))
                            {
                                Process.Start(new ProcessStartInfo
                                {
                                    FileName = installerPath,
                                    UseShellExecute = true
                                });
                            }
                            Application.Current.Shutdown();
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка проверки обновлений: {ex.Message}");
            }
            finally
            {
                _isCheckingUpdate = false;
            }
        }
        private bool CheckForUpdateInFolder()
        {
            try
            {
                if (!Directory.Exists(UpdateFolderPath))
                    return false;

                string versionFile = Path.Combine(UpdateFolderPath, "version.txt");
                if (!File.Exists(versionFile))
                    return false;

                string serverVersionStr = File.ReadAllText(versionFile).Trim();
                if (Version.TryParse(serverVersionStr, out Version serverVersion))
                {
                    Version currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
                    return serverVersion > currentVersion;
                }
            }
            catch { /* сеть недоступна или файл не найден */ }
            return false;
        }


        #endregion

        
    }
}