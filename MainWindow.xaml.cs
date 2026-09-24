using Microsoft.Toolkit.Uwp.Notifications;
using Newtonsoft.Json;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using TNovClient;
using MessageBox = System.Windows.MessageBox;

namespace TNovDesktop
{
    public partial class MainWindow : Window
    {
        [DllImport("shell32.dll", SetLastError = true)]
        private static extern void SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string AppID);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        private string pendingConversationId = null;
        public static string novaserver = "//fs-nova/Distr/0.For Admin/";
        private DispatcherTimer _tabHoverTimer;
        private TabItem _hoveredTabItem;
        private DispatcherTimer _taskMonitorTimer;
        private DateTime _lastTaskCheckTime;
        private DispatcherTimer _updateCheckTimer;
        private bool _isCheckingUpdate = false;
        private string _notifiedUpdateVersion = string.Empty;
        private const string UpdateFolderPath = @"\\fs-nova\Distr\0.For Admin\_TNov\actual\desktop\";
        private Rect _restoreBounds;
        private bool _isWorkAreaMaximized;
        private bool _suppressStateChange;
        private System.Drawing.Icon _trayIconBase;
        private System.Drawing.Icon _trayIconBadged;
        private DateTime? _notificationsMutedUntil;
        private bool _muteNotificationsUntilRestart;
        private DispatcherTimer? _notificationMuteTimer;
        private string _trayUnreadDetail = "";

        public MainWindow()
        {
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
                _trayIconBase = TrayIcon.Icon;
            }
            catch { }

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
            StateChanged += MainWindow_StateChanged;

            // Подписываемся на запросы показа уведомлений от контрола
            MessengerControl.ShowToastRequested += OnMessengerToastRequested;
            MessengerControl.SidebarBadgesChanged += OnMessengerSidebarBadgesChanged;
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
            pendingConversationId = string.IsNullOrWhiteSpace(conversationId) ? null : conversationId;
            ShowToastNotification(title, body, () =>
            {
                SelectTabByTitle("TNovPro");
                ShowMainWindow();
            });
        }

        private void OnMessengerSidebarBadgesChanged(IReadOnlyDictionary<string, int> items, int total)
        {
            bool show = total > 0;
            MessengerTabBadge.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            MessengerTabBadgeText.Text = total > 99 ? "99+" : total.ToString();

            var parts = items.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key}: {kv.Value}").ToList();
            string detail = parts.Count > 0
                ? string.Join(", ", parts)
                : (show ? $"Непрочитанные: {total}" : "");

            MessengerTabBadge.ToolTip = string.IsNullOrEmpty(detail) ? null : detail;
            _trayUnreadDetail = detail;
            RefreshTrayToolTip();

            if (AppTaskbarInfo != null)
                AppTaskbarInfo.Overlay = show ? CreateBadgeOverlay(total) : null;

            UpdateTrayIconBadge(total);
        }

        private ImageSource CreateBadgeOverlay(int count)
        {
            string text = count > 99 ? "99+" : count.ToString();
            int width = text.Length > 1 ? 20 : 16;
            const int height = 16;
            double dip = 1.0;
            try { dip = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { }

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var fill = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
                fill.Freeze();
                dc.DrawRoundedRectangle(fill, null, new Rect(0, 0, width, height), 8, 8);

                double fontSize = text.Length > 2 ? 8 : 10;
                var ft = new FormattedText(
                    text,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"),
                    fontSize,
                    Brushes.White,
                    dip);
                dc.DrawText(ft, new Point((width - ft.Width) / 2, (height - ft.Height) / 2));
            }

            var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }

        private void UpdateTrayIconBadge(int count)
        {
            if (_trayIconBase == null)
                return;

            if (count <= 0)
            {
                TrayIcon.Icon = _trayIconBase;
                DisposeTrayBadgeIcon();
                return;
            }

            try
            {
                var created = CreateBadgedTrayIcon(_trayIconBase, count);
                TrayIcon.Icon = created;
                DisposeTrayBadgeIcon();
                _trayIconBadged = created;
            }
            catch
            {
                TrayIcon.Icon = _trayIconBase;
            }
        }

        private static System.Drawing.Icon CreateBadgedTrayIcon(System.Drawing.Icon baseIcon, int count)
        {
            string text = count > 99 ? "99+" : count.ToString();
            using var bmp = new System.Drawing.Bitmap(32, 32);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.Clear(System.Drawing.Color.Transparent);
                g.DrawIcon(baseIcon, new System.Drawing.Rectangle(0, 0, 32, 32));

                var badge = new System.Drawing.Rectangle(text.Length > 1 ? 8 : 14, 14, text.Length > 1 ? 24 : 18, 18);
                using (var fill = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(229, 57, 53)))
                    g.FillEllipse(fill, badge);

                using var font = new System.Drawing.Font("Segoe UI", text.Length > 2 ? 7f : 8f, System.Drawing.FontStyle.Bold);
                var sf = new System.Drawing.StringFormat
                {
                    Alignment = System.Drawing.StringAlignment.Center,
                    LineAlignment = System.Drawing.StringAlignment.Center
                };
                g.DrawString(text, font, System.Drawing.Brushes.White, badge, sf);
            }

            IntPtr handle = bmp.GetHicon();
            try
            {
                using var tmp = System.Drawing.Icon.FromHandle(handle);
                return (System.Drawing.Icon)tmp.Clone();
            }
            finally
            {
                DestroyIcon(handle);
            }
        }

        private void DisposeTrayBadgeIcon()
        {
            if (_trayIconBadged == null)
                return;
            try { _trayIconBadged.Dispose(); } catch { }
            _trayIconBadged = null;
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

        private bool AreNotificationsMuted =>
            _muteNotificationsUntilRestart ||
            (_notificationsMutedUntil.HasValue && _notificationsMutedUntil.Value > DateTime.Now);

        private void ShowToastNotification(string title, string message, Action onClick = null)
        {
            if (AreNotificationsMuted)
                return;

            try
            {
                NotificationWindow notificationWindow = new NotificationWindow();
                notificationWindow.ShowNotification(title, message, onClick: onClick ?? ShowMainWindow);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{title}\n\n{message}", "TNovDesktop", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void NotificationMuteMenu_Opened(object sender, RoutedEventArgs e)
        {
            UpdateNotificationMuteMenu();
        }

        private void MuteNotificationsButton_Click(object sender, RoutedEventArgs e)
        {
            if (TitleMuteMenu == null)
                return;

            UpdateNotificationMuteMenu();
            TitleMuteMenu.PlacementTarget = MuteNotificationsButton;
            TitleMuteMenu.Placement = PlacementMode.Bottom;
            TitleMuteMenu.IsOpen = true;
            e.Handled = true;
        }

        private void MuteFor30Minutes_Click(object sender, RoutedEventArgs e)
        {
            MuteNotifications(untilRestart: false, duration: TimeSpan.FromMinutes(30));
        }

        private void MuteUntilRestart_Click(object sender, RoutedEventArgs e)
        {
            MuteNotifications(untilRestart: true, duration: null);
        }

        private void UnmuteNotifications_Click(object sender, RoutedEventArgs e)
        {
            ClearNotificationMute();
        }

        private void MuteNotifications(bool untilRestart, TimeSpan? duration)
        {
            _muteNotificationsUntilRestart = untilRestart;
            _notificationsMutedUntil = untilRestart || duration == null
                ? null
                : DateTime.Now.Add(duration.Value);

            StopNotificationMuteTimer();
            if (_notificationsMutedUntil.HasValue)
            {
                TimeSpan remaining = _notificationsMutedUntil.Value - DateTime.Now;
                if (remaining <= TimeSpan.Zero)
                {
                    ClearNotificationMute();
                    return;
                }

                _notificationMuteTimer = new DispatcherTimer { Interval = remaining };
                _notificationMuteTimer.Tick += NotificationMuteTimer_Tick;
                _notificationMuteTimer.Start();
            }

            UpdateNotificationMuteMenu();
            RefreshTrayToolTip();
        }

        private void NotificationMuteTimer_Tick(object sender, EventArgs e)
        {
            ClearNotificationMute();
        }

        private void ClearNotificationMute()
        {
            _muteNotificationsUntilRestart = false;
            _notificationsMutedUntil = null;
            StopNotificationMuteTimer();
            UpdateNotificationMuteMenu();
            RefreshTrayToolTip();
        }

        private void StopNotificationMuteTimer()
        {
            if (_notificationMuteTimer == null)
                return;

            _notificationMuteTimer.Stop();
            _notificationMuteTimer.Tick -= NotificationMuteTimer_Tick;
            _notificationMuteTimer = null;
        }

        private void UpdateNotificationMuteMenu()
        {
            UpdateMuteMenuItems(
                MuteNotificationsMenu,
                MuteFor30MinutesItem,
                MuteUntilRestartItem,
                UnmuteSeparator,
                UnmuteNotificationsItem);

            UpdateMuteMenuItems(
                parent: null,
                TitleMuteFor30MinutesItem,
                TitleMuteUntilRestartItem,
                TitleUnmuteSeparator,
                TitleUnmuteNotificationsItem);

            UpdateTitleMuteButton();
        }

        private void UpdateMuteMenuItems(
            MenuItem? parent,
            MenuItem? mute30,
            MenuItem? muteUntilRestart,
            Separator? unmuteSep,
            MenuItem? unmute)
        {
            bool muted = AreNotificationsMuted;
            if (parent != null)
            {
                parent.Header = muted
                    ? GetNotificationMuteStatusText()
                    : "Отключить уведомления";
            }

            if (mute30 != null)
                mute30.IsChecked = muted && !_muteNotificationsUntilRestart && _notificationsMutedUntil.HasValue;

            if (muteUntilRestart != null)
                muteUntilRestart.IsChecked = muted && _muteNotificationsUntilRestart;

            var unmuteVisibility = muted ? Visibility.Visible : Visibility.Collapsed;
            if (unmuteSep != null)
                unmuteSep.Visibility = unmuteVisibility;
            if (unmute != null)
                unmute.Visibility = unmuteVisibility;
        }

        private void UpdateTitleMuteButton()
        {
            if (MuteNotificationsButton == null)
                return;

            bool muted = AreNotificationsMuted;
            MuteNotificationsButton.Content = muted ? "\uE7ED" : "\uE91C";
            MuteNotificationsButton.ToolTip = muted
                ? GetNotificationMuteStatusText()
                : "Отключить уведомления";
            MuteNotificationsButton.Foreground = muted
                ? (Brush)FindResource("MutedBrush")
                : Brushes.White;
        }

        private string GetNotificationMuteStatusText()
        {
            if (_muteNotificationsUntilRestart)
                return "Уведомления отключены до перезапуска";

            if (_notificationsMutedUntil.HasValue)
                return $"Уведомления отключены до {_notificationsMutedUntil.Value:HH:mm}";

            return "Уведомления отключены";
        }

        private void RefreshTrayToolTip()
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(_trayUnreadDetail))
                parts.Add(_trayUnreadDetail);
            if (AreNotificationsMuted)
                parts.Add(GetNotificationMuteStatusText());

            TrayIcon.ToolTipText = parts.Count == 0
                ? "TNovDesktop"
                : $"TNovDesktop — {string.Join("; ", parts)}";
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
            SelectTabByTitle("Задания");
            ProjectControl.RefreshData();
        }

        private void SelectTabByTitle(string title)
        {
            foreach (TabItem item in MainTabControl.Items)
            {
                string header = null;
                if (item.Header is string s)
                    header = s;
                else if (item.Header is StackPanel sp)
                    header = sp.Children.OfType<TextBlock>().FirstOrDefault()?.Text;

                if (header == title)
                {
                    item.IsSelected = true;
                    break;
                }
            }
        }

        #endregion

        #region Системный трей и управление окном

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            if (App.AllowExit)
                return;

            e.Cancel = true;
            Hide();
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
            StopNotificationMuteTimer();
            DisposeTrayBadgeIcon();
            TrayIcon.Dispose();
            App.RequestExit();
        }

        private void ShowMainWindow()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();

            if (!string.IsNullOrEmpty(pendingConversationId))
            {
                string chatId = pendingConversationId;
                pendingConversationId = null;
                MessengerControl.NavigateToChat(chatId);
            }
        }

        #endregion

        #region Кастомный заголовок и кнопки

        private void Border_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton != System.Windows.Input.MouseButton.Left)
                return;

            if (_isWorkAreaMaximized)
                RestoreFromWorkArea();

            DragMove();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        private void HideButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaxButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleWorkAreaMaximize();
        }

        private void MainWindow_StateChanged(object sender, EventArgs e)
        {
            if (_suppressStateChange)
                return;

            // Win+↑, Aero Snap и системное разворачивание: не даём окну
            // перекрыть панель задач, а сажаем его в рабочую область монитора.
            if (WindowState == WindowState.Maximized)
            {
                _suppressStateChange = true;
                WindowState = WindowState.Normal;
                _suppressStateChange = false;

                if (_isWorkAreaMaximized)
                    RestoreFromWorkArea();
                else
                    MaximizeToWorkArea();
            }
        }

        private void ToggleWorkAreaMaximize()
        {
            if (_isWorkAreaMaximized)
                RestoreFromWorkArea();
            else
                MaximizeToWorkArea();
        }

        private void MaximizeToWorkArea()
        {
            if (!_isWorkAreaMaximized)
                _restoreBounds = new Rect(Left, Top, Width, Height);

            Rect work = WindowWorkArea.Get(this);
            Left = work.Left;
            Top = work.Top;
            Width = work.Width;
            Height = work.Height;
            _isWorkAreaMaximized = true;
            ApplyMaximizedChrome(true);
        }

        private void RestoreFromWorkArea()
        {
            if (_restoreBounds.Width > 0 && _restoreBounds.Height > 0)
            {
                Left = _restoreBounds.X;
                Top = _restoreBounds.Y;
                Width = _restoreBounds.Width;
                Height = _restoreBounds.Height;
            }
            _isWorkAreaMaximized = false;
            ApplyMaximizedChrome(false);
        }

        private void ApplyMaximizedChrome(bool maximized)
        {
            MainBorder.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(12);

            var chrome = WindowChrome.GetWindowChrome(this);
            if (chrome != null)
                chrome.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(12);
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
        private void ChecklistsTabRefresh_Click(object sender, RoutedEventArgs e)
        {
            ChecklistsControl.RefreshData();
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

                ShowToastNotification(title, message, () =>
                {
                    NavigateToProjectsAndRefresh();
                    ShowMainWindow();
                });
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
                string availableVersion = await Task.Run(ReadAvailableServerVersion);
                if (string.IsNullOrEmpty(availableVersion))
                    return;

                if (string.Equals(availableVersion, _notifiedUpdateVersion, StringComparison.Ordinal))
                    return;

                _notifiedUpdateVersion = availableVersion;
                Dispatcher.Invoke(() =>
                {
                    ShowToastNotification(
                        "Доступно обновление TNovDesktop",
                        $"Версия {availableVersion}. TNovClient установит её после закрытия программы через трей.");
                });
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

        private static string ReadAvailableServerVersion()
        {
            try
            {
                if (!Directory.Exists(UpdateFolderPath))
                    return string.Empty;

                string versionFile = Path.Combine(UpdateFolderPath, "version.txt");
                if (!File.Exists(versionFile))
                    return string.Empty;

                string serverVersionStr = File.ReadAllText(versionFile).Trim();
                if (!Version.TryParse(serverVersionStr, out Version? serverVersion) || serverVersion == null)
                    return string.Empty;

                Version? currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
                if (currentVersion == null)
                    return string.Empty;

                return serverVersion > currentVersion ? serverVersion.ToString() : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }


        #endregion

        
    }
}