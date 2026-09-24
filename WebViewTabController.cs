using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MessageBox = System.Windows.MessageBox;

namespace TNovDesktop
{
    /// <summary>
    /// Общий контроллер жизненного цикла WebView2 для вкладок-обёрток над веб-сервисами.
    /// Инкапсулирует инициализацию (единое окружение приложения), разрешения, навигацию,
    /// панель ошибок, мост браузерных уведомлений и открытие окон звонков.
    /// Вкладки подключают его по композиции, поэтому XAML не меняется.
    /// </summary>
    internal sealed class WebViewTabController
    {
        private readonly WebView2 _webView;
        private readonly ProgressBar _loading;
        private readonly Border _errorPanel;
        private readonly TextBlock _errorDetails;
        private readonly string _url;
        private readonly bool _allowMedia;
        private readonly bool _watchSidebarBadges;

        private CoreWebView2Environment? _environment;
        private bool _isInitialized;
        private bool _isSubscribed;
        private int _isRecovering;
        private string _pendingNavigationChatId;
        private string _currentUserId;
        private string _currentUserName;
        private readonly HashSet<string> _ownNames = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Запрос показа тост-уведомления: (title, body, conversationId).</summary>
        public event Action<string, string, string> ShowToastRequested;

        /// <summary>Счётчики сайдбара TNovPRO: (модуль → число, суммарный total).</summary>
        public event Action<IReadOnlyDictionary<string, int>, int> SidebarBadgesChanged;

        public WebViewTabController(
            WebView2 webView,
            ProgressBar loading,
            Border errorPanel,
            TextBlock errorDetails,
            string url,
            bool allowMedia = false,
            bool watchSidebarBadges = false)
        {
            _webView = webView;
            _loading = loading;
            _errorPanel = errorPanel;
            _errorDetails = errorDetails;
            _url = url;
            _allowMedia = allowMedia;
            _watchSidebarBadges = watchSidebarBadges;
        }

        public WebView2 WebView => _webView;

        public void Refresh() => _webView.CoreWebView2?.Reload();

        public void NavigateToChat(string chatId)
        {
            if (_webView.CoreWebView2 != null)
            {
                _webView.CoreWebView2.Navigate($"{_url}#/chat/{chatId}");
                _pendingNavigationChatId = null;
            }
            else
            {
                _pendingNavigationChatId = chatId;
            }
        }

        #region Жизненный цикл UserControl

        public async void OnLoaded()
        {
            if (!_isInitialized)
                await InitializeAsync();
            else
                RestoreSubscriptions();
        }

        public void OnUnloaded()
        {
            // Не Stop() и не отписываемся: выгрузка UserControl (вкладка, шаблон)
            // не должна рвать загрузки и оставлять COM-объект WebView2 в клине.
        }

        #endregion

        #region Инициализация

        private async Task InitializeAsync()
        {
            try
            {
                _environment ??= await WebViewEnvironment.GetAsync();
                await _webView.EnsureCoreWebView2Async(_environment);

                var settings = _webView.CoreWebView2.Settings;
                settings.IsGeneralAutofillEnabled = true;
                settings.IsPasswordAutosaveEnabled = true;
                settings.IsScriptEnabled = true;
                settings.AreDefaultScriptDialogsEnabled = true;
                settings.IsWebMessageEnabled = true;

                AttachCoreHandlers(_webView.CoreWebView2);
                Subscribe();
                await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(EarlyInjectionScript);

                if (_watchSidebarBadges)
                    await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(SidebarBadgeScript);

                // Для вкладок со звонками включаем диагностику доступа к микрофону/камере.
                if (_allowMedia)
                    await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(MediaDiagnosticsScript);

                _webView.CoreWebView2.Navigate(_url);
                _isInitialized = true;
            }
            catch (Exception ex)
            {
                ShowError($"Ошибка инициализации WebView2: {ex.Message}");
                HandleRuntimeMissing(ex);
            }
        }

        private void RestoreSubscriptions()
        {
            if (_webView.CoreWebView2 != null && !_isSubscribed)
            {
                Subscribe();
                if (!string.IsNullOrEmpty(_pendingNavigationChatId))
                    NavigateToChat(_pendingNavigationChatId);
            }
            else if (_webView.CoreWebView2 == null)
            {
                _ = InitializeAsync();
            }
        }

        private void Subscribe()
        {
            if (_webView.CoreWebView2 == null) return;

            var core = _webView.CoreWebView2;
            core.NavigationStarting -= OnNavigationStarting;
            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted -= OnNavigationCompleted;
            core.NavigationCompleted += OnNavigationCompleted;
            core.WebMessageReceived -= OnWebMessageReceived;
            core.WebMessageReceived += OnWebMessageReceived;
            _isSubscribed = true;
        }

        private void AttachCoreHandlers(CoreWebView2 core)
        {
            core.PermissionRequested -= OnPermissionRequested;
            core.PermissionRequested += OnPermissionRequested;
            core.ProcessFailed -= OnProcessFailed;
            core.ProcessFailed += OnProcessFailed;
            core.DownloadStarting -= OnDownloadStarting;
            core.DownloadStarting += OnDownloadStarting;
            core.NewWindowRequested -= OnNewWindowRequested;
            core.NewWindowRequested += OnNewWindowRequested;
        }

        private void HandleRuntimeMissing(Exception ex)
        {
            if (!ex.Message.Contains("Couldn't find a compatible Webview2 Runtime"))
                return;

            var result = MessageBox.Show(
                "Для работы необходим WebView2 Runtime. Установить сейчас?",
                "Компонент не найден",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://go.microsoft.com/fwlink/p/?LinkId=2124703",
                    UseShellExecute = true
                });
            }
        }

        #endregion

        #region Разрешения и окна

        private void OnPermissionRequested(object sender, CoreWebView2PermissionRequestedEventArgs args)
        {
            switch (args.PermissionKind)
            {
                case CoreWebView2PermissionKind.Notifications:
                    args.State = CoreWebView2PermissionState.Allow;
                    break;
                // Микрофон и камера — только для вкладок, где нужны звонки (getUserMedia).
                case CoreWebView2PermissionKind.Microphone:
                case CoreWebView2PermissionKind.Camera:
                    if (_allowMedia)
                    {
                        args.State = CoreWebView2PermissionState.Allow;
                        Log.Write($"[PERMISSION] {args.PermissionKind} для {args.Uri} -> Allow");
                    }
                    else
                    {
                        Log.Write($"[PERMISSION] {args.PermissionKind} для {args.Uri} -> (пропущено, allowMedia=false)");
                    }
                    break;
            }
        }

        // window.open / target=_blank. Нельзя вызывать EnsureCoreWebView2Async до Show():
        // без HWND инициализация зависает, а исходный WebView ждёт deferral — мёртвая хватка.
        // Внешние ссылки открываем в системном браузере, чтобы не плодить вложенные WebView2.
        private async void OnNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            var source = sender as CoreWebView2;
            string uri = e.Uri ?? "";
            Log.Write($"[WEBVIEW2] NewWindowRequested uri={uri} userInit={e.IsUserInitiated}");

            if (!ShouldHostPopup(source, e))
            {
                e.Handled = true;
                TryOpenExternally(uri);
                return;
            }

            var deferral = e.GetDeferral();
            Window? popupWindow = null;
            WebView2? popupView = null;
            try
            {
                var env = await WebViewEnvironment.GetAsync();
                popupView = new WebView2();
                popupWindow = CreatePopupWindow(popupView, e);

                // HWND должен существовать до EnsureCoreWebView2Async.
                popupWindow.Show();
                await popupWindow.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);

                var init = popupView.EnsureCoreWebView2Async(env);
                var finished = await Task.WhenAny(init, Task.Delay(TimeSpan.FromSeconds(12)));
                if (finished != init)
                {
                    Log.Write("[WEBVIEW2] NewWindow EnsureCoreWebView2 timeout — отменяю popup");
                    popupWindow.Close();
                    e.Handled = true;
                    TryOpenExternally(uri);
                    return;
                }

                await init;
                AttachCoreHandlers(popupView.CoreWebView2);
                popupView.CoreWebView2.WindowCloseRequested += (_, _) =>
                {
                    try { popupWindow.Close(); } catch { }
                };

                e.NewWindow = popupView.CoreWebView2;
                e.Handled = true;
                popupWindow.Activate();
            }
            catch (Exception ex)
            {
                Log.Write($"[WEBVIEW2] NewWindowRequested error: {ex.Message}");
                try { popupWindow?.Close(); } catch { }
                e.Handled = true;
                TryOpenExternally(uri);
            }
            finally
            {
                deferral.Complete();
            }
        }

        private Window CreatePopupWindow(WebView2 popupView, CoreWebView2NewWindowRequestedEventArgs e)
        {
            double width = 900;
            double height = 700;
            try
            {
                if (e.WindowFeatures != null)
                {
                    if (e.WindowFeatures.HasSize)
                    {
                        if (e.WindowFeatures.Width > 200) width = e.WindowFeatures.Width;
                        if (e.WindowFeatures.Height > 200) height = e.WindowFeatures.Height;
                    }
                }
            }
            catch { }

            var popupWindow = new Window
            {
                Title = "TNovDesktop",
                Width = width,
                Height = height,
                Content = popupView,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true
            };
            popupWindow.Closed += (_, _) =>
            {
                try { popupView.Dispose(); } catch { }
            };
            return popupWindow;
        }

        private bool ShouldHostPopup(CoreWebView2 source, CoreWebView2NewWindowRequestedEventArgs e)
        {
            string uri = e.Uri ?? "";
            if (string.IsNullOrWhiteSpace(uri)
                || uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
                || uri.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)
                || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
                return true;

            if (parsed.Scheme is not ("http" or "https"))
                return false;

            string originHost = TryGetHost(source?.Source) ?? TryGetHost(_url);
            if (!string.IsNullOrEmpty(originHost)
                && string.Equals(parsed.Host, originHost, StringComparison.OrdinalIgnoreCase))
                return true;

            // Звонки часто открывают отдельный origin в sized-popup.
            try
            {
                if (_allowMedia && e.WindowFeatures != null && (e.WindowFeatures.HasSize || e.WindowFeatures.HasPosition))
                    return true;
            }
            catch { }

            return false;
        }

        private static string? TryGetHost(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            return Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : null;
        }

        private static bool TryOpenExternally(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri))
                return false;
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
                return false;
            if (parsed.Scheme != Uri.UriSchemeHttp
                && parsed.Scheme != Uri.UriSchemeHttps
                && parsed.Scheme != Uri.UriSchemeMailto)
                return false;

            try
            {
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                Log.Write($"[WEBVIEW2] OpenExternally failed: {ex.Message}");
                return false;
            }
        }

        private void OnDownloadStarting(object sender, CoreWebView2DownloadStartingEventArgs e)
        {
            // Штатный flyout загрузок WebView2 в WPF часто вешает UI-поток.
            // Handled=true скрывает его; скачивание всё равно идёт в ResultFilePath.
            e.Handled = true;
            if (string.IsNullOrWhiteSpace(e.ResultFilePath))
            {
                string downloads = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads");
                Directory.CreateDirectory(downloads);
                e.ResultFilePath = Path.Combine(downloads, "download");
            }

            var op = e.DownloadOperation;
            string path = e.ResultFilePath;
            Log.Write($"[WEBVIEW2] Download start {op.Uri} -> {path}");
            op.StateChanged += (_, _) =>
            {
                Log.Write($"[WEBVIEW2] Download {op.State} {op.ResultFilePath}");
            };
            DownloadWindow.Show(op, path, _webView.Dispatcher);
        }

        private void OnProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
        {
            Log.Write($"[WEBVIEW2] ProcessFailed kind={e.ProcessFailedKind} reason={e.Reason} exit={e.ExitCode} url={_url}");
            _webView.Dispatcher.BeginInvoke(new Action(() => _ = RecoverAsync(e)));
        }

        private async Task RecoverAsync(CoreWebView2ProcessFailedEventArgs e)
        {
            if (Interlocked.Exchange(ref _isRecovering, 1) == 1)
                return;

            try
            {
                await Task.Delay(300);

                if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
                {
                    WebViewEnvironment.Reset();
                    _environment = null;
                    _isInitialized = false;
                    _isSubscribed = false;
                    await InitializeAsync();
                    return;
                }

                if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited
                    || e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive
                    || e.ProcessFailedKind == CoreWebView2ProcessFailedKind.FrameRenderProcessExited
                    || e.ProcessFailedKind == CoreWebView2ProcessFailedKind.GpuProcessExited)
                {
                    if (_webView.CoreWebView2 != null)
                    {
                        try { _webView.CoreWebView2.Reload(); }
                        catch
                        {
                            _isInitialized = false;
                            _isSubscribed = false;
                            await InitializeAsync();
                        }
                    }
                    else
                    {
                        _isInitialized = false;
                        _isSubscribed = false;
                        await InitializeAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write($"[WEBVIEW2] Recover failed: {ex.Message}");
                RunOnUi(() => ShowError($"WebView2 перестал отвечать: {ex.Message}"));
            }
            finally
            {
                Interlocked.Exchange(ref _isRecovering, 0);
            }
        }

        private void RunOnUi(Action action)
        {
            var d = _webView.Dispatcher;
            if (d.CheckAccess())
                action();
            else
                d.BeginInvoke(action);
        }

        #endregion

        #region Навигация и панель ошибок

        private void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            RunOnUi(() =>
            {
                if (_loading != null) _loading.Visibility = Visibility.Visible;
                if (_errorPanel != null) _errorPanel.Visibility = Visibility.Collapsed;
                _webView.Visibility = Visibility.Visible;
            });
        }

        private async void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            RunOnUi(() =>
            {
                if (_loading != null) _loading.Visibility = Visibility.Collapsed;
                if (!e.IsSuccess)
                    ShowError($"Не удалось загрузить страницу: {e.WebErrorStatus}");
            });

            if (e.IsSuccess && !string.IsNullOrEmpty(_pendingNavigationChatId))
                NavigateToChat(_pendingNavigationChatId);

            if (e.IsSuccess && _watchSidebarBadges)
            {
                try { await _webView.ExecuteScriptAsync(SidebarBadgeScript); }
                catch (Exception ex) { Debug.WriteLine($"[WPF] Badge script: {ex.Message}"); }
            }
        }

        private void ShowError(string message)
        {
            if (_errorDetails != null) _errorDetails.Text = message;
            if (_errorPanel != null) _errorPanel.Visibility = Visibility.Visible;
            _webView.Visibility = Visibility.Collapsed;
        }

        public void Retry()
        {
            if (_errorPanel != null) _errorPanel.Visibility = Visibility.Collapsed;
            _webView.Visibility = Visibility.Visible;
            _webView.CoreWebView2?.Reload();
        }

        #endregion

        #region Мост браузерных уведомлений

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string json = e.TryGetWebMessageAsString();
            RunOnUi(() => ProcessMessage(json));
        }

        private void ProcessMessage(string json)
        {
            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("type", out var typeProp)) return;
                string type = typeProp.GetString();

                if (type == "setCurrentUserId")
                {
                    _currentUserId = doc.RootElement.GetProperty("userId").GetString();
                }
                else if (type == "setCurrentUserName")
                {
                    _currentUserName = doc.RootElement.GetProperty("userName").GetString();
                    RememberOwnName(_currentUserName);
                }
                else if (type == "setCurrentUser")
                {
                    if (doc.RootElement.TryGetProperty("userId", out var uid) && uid.ValueKind == System.Text.Json.JsonValueKind.String)
                        _currentUserId = uid.GetString();
                    if (doc.RootElement.TryGetProperty("userName", out var un) && un.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        _currentUserName = un.GetString();
                        RememberOwnName(_currentUserName);
                    }
                    if (doc.RootElement.TryGetProperty("userNames", out var names) && names.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var n in names.EnumerateArray())
                            RememberOwnName(n.GetString());
                    }
                }
                else if (type == "sidebarBadges")
                {
                    HandleSidebarBadges(doc.RootElement);
                }
                else if (type == "webNotification")
                {
                    string title = doc.RootElement.GetProperty("title").GetString();
                    string body = doc.RootElement.TryGetProperty("body", out var b) ? b.GetString() : "";
                    string senderId = doc.RootElement.TryGetProperty("senderId", out var sid) && sid.ValueKind != System.Text.Json.JsonValueKind.Null
                        ? sid.GetString() : null;
                    string senderName = doc.RootElement.TryGetProperty("senderName", out var sn) && sn.ValueKind == System.Text.Json.JsonValueKind.String
                        ? sn.GetString() : null;
                    string conversationId = doc.RootElement.TryGetProperty("conversationId", out var cid) && cid.ValueKind == System.Text.Json.JsonValueKind.String
                        ? cid.GetString() : null;
                    bool markedOwn = doc.RootElement.TryGetProperty("isOwn", out var ownEl) && ownEl.ValueKind == System.Text.Json.JsonValueKind.True;

                    if (markedOwn || IsOwnChatNotification(title, body, senderId, senderName))
                    {
                        Log.Write($"[NOTIF] skip own title={title} senderId={senderId} senderName={senderName}");
                        return;
                    }

                    ShowToastRequested?.Invoke(title, body, conversationId);
                }
                else if (type == "mediaError")
                {
                    string name = GetStr(doc, "name");
                    string message = GetStr(doc, "message");
                    string phase = GetStr(doc, "phase");
                    string devices = GetStr(doc, "devices");

                    Log.Write($"[MEDIA-ERROR] phase={phase} name={name} message={message} devices={devices}");
                    /*MessageBox.Show(
                        "Не удалось получить доступ к микрофону/камере.\n\n" +
                        $"Этап: {phase}\n" +
                        $"Ошибка: {name}\n" +
                        $"Сообщение: {message}\n" +
                        $"Найденные устройства: {devices}\n\n" +
                        $"Подробности записаны в лог:\n{Log.FilePath}",
                        "Диагностика звонков",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);*/
                }
                else if (type == "mediaOk")
                {
                    Log.Write($"[MEDIA-OK] getUserMedia успешно, constraints={GetStr(doc, "constraints")}");
                }
                else if (type == "probe")
                {
                    Log.Write($"[MEDIA-PROBE] {GetStr(doc, "kind")} = {GetStr(doc, "result")}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WPF] Message processing error: {ex.Message}");
            }
        }

        private static string GetStr(System.Text.Json.JsonDocument doc, string prop) =>
            doc.RootElement.TryGetProperty(prop, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString()
                : "";

        private void RememberOwnName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            _ownNames.Add(name.Trim());
        }

        private bool IsOwnChatNotification(string title, string body, string senderId, string senderName)
        {
            if (!string.IsNullOrEmpty(_currentUserId) && !string.IsNullOrEmpty(senderId)
                && string.Equals(_currentUserId, senderId, StringComparison.OrdinalIgnoreCase))
                return true;

            if (NameIsOwn(title) || NameIsOwn(senderName))
                return true;

            return BodyHasOwnPrefix(body);
        }

        private bool NameIsOwn(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || _ownNames.Count == 0)
                return false;
            return _ownNames.Contains(value.Trim());
        }

        private bool BodyHasOwnPrefix(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
                return false;
            var t = body.TrimStart();
            if (t.StartsWith("Вы:", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("Вы —", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("You:", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("Я:", StringComparison.OrdinalIgnoreCase))
                return true;

            foreach (var name in _ownNames)
            {
                if (name.Length < 2) continue;
                if (!t.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (t.Length == name.Length)
                    continue;
                char next = t[name.Length];
                if (next is ':' or '-' or '—' or ',')
                    return true;
            }
            return false;
        }

        private void HandleSidebarBadges(System.Text.Json.JsonElement root)
        {
            var items = new Dictionary<string, int>(StringComparer.Ordinal);
            if (root.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var p in itemsEl.EnumerateObject())
                {
                    int n = 0;
                    if (p.Value.ValueKind == System.Text.Json.JsonValueKind.Number)
                        p.Value.TryGetInt32(out n);
                    else if (p.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                        int.TryParse(p.Value.GetString(), out n);
                    if (n > 0)
                        items[p.Name] = n;
                }
            }

            int titleCount = 0;
            if (root.TryGetProperty("titleCount", out var tc) && tc.ValueKind == System.Text.Json.JsonValueKind.Number)
                tc.TryGetInt32(out titleCount);

            string labelsFound = "";
            if (root.TryGetProperty("labelsFound", out var lf) && lf.ValueKind == System.Text.Json.JsonValueKind.Array)
                labelsFound = string.Join(",", lf.EnumerateArray().Select(x => x.GetString()));

            int total = 0;
            foreach (int v in items.Values)
                total += v;
            if (total == 0 && titleCount > 0)
                total = titleCount;

            Log.Write($"[BADGES] total={total} title={titleCount} labels=[{labelsFound}] {string.Join(", ", items.Select(kv => kv.Key + "=" + kv.Value))}");
            SidebarBadgesChanged?.Invoke(items, total);
        }

        private const string EarlyInjectionScript = @"
(function() {
    var OriginalNotification = window.Notification;
    var _myUserId = null;
    var _myUserName = null;
    var _myNames = [];
    var _recentSent = [];

    function post(payload) {
        try {
            if (window.chrome && window.chrome.webview)
                window.chrome.webview.postMessage(JSON.stringify(payload));
        } catch (e) {}
    }

    function addName(name) {
        if (!name) return;
        var t = String(name).replace(/\s+/g, ' ').trim();
        if (!t || t.length < 2) return;
        if (_myNames.indexOf(t) === -1) _myNames.push(t);
        if (!_myUserName) _myUserName = t;
    }

    function pickUser(obj, depth) {
        if (!obj || typeof obj !== 'object' || depth > 5) return null;
        var id = obj.id || obj.userId || obj.user_id || obj.uid || obj.sub;
        var first = obj.firstName || obj.first_name || obj.given_name;
        var last = obj.lastName || obj.last_name || obj.family_name;
        var name = first || obj.displayName || obj.username || obj.fullName || obj.name || obj.email;
        if (id && name) {
            var full = (first && last) ? (first + ' ' + last) : name;
            return { id: String(id), name: String(full), first: first, last: last, username: obj.username };
        }
        var nested = ['user', 'currentUser', 'profile', 'me', 'account', 'auth', 'data', 'session'];
        for (var i = 0; i < nested.length; i++) {
            if (obj[nested[i]]) {
                var found = pickUser(obj[nested[i]], depth + 1);
                if (found) return found;
            }
        }
        return null;
    }

    function scanStorage(storage) {
        if (!storage) return null;
        var keys = ['mobileUser', 'user', 'currentUser', 'authUser', 'tnovUser', 'me'];
        for (var i = 0; i < keys.length; i++) {
            try {
                var raw = storage.getItem(keys[i]);
                if (!raw) continue;
                var found = pickUser(JSON.parse(raw), 0);
                if (found) return found;
            } catch (e) {}
        }
        for (var j = 0; j < storage.length; j++) {
            try {
                var raw2 = storage.getItem(storage.key(j));
                if (!raw2 || raw2.length < 8 || raw2[0] !== '{' && raw2[0] !== '[') continue;
                var found2 = pickUser(JSON.parse(raw2), 0);
                if (found2) return found2;
            } catch (e) {}
        }
        return null;
    }

    function findUser() {
        try {
            if (window.me) {
                var u = pickUser(window.me, 0);
                if (u) return u;
            }
        } catch (e) {}
        var fromSession = scanStorage(window.sessionStorage);
        if (fromSession) return fromSession;
        var fromLocal = scanStorage(window.localStorage);
        if (fromLocal) return fromLocal;
        return null;
    }

    function sendUserInfo() {
        post({
            type: 'setCurrentUser',
            userId: _myUserId,
            userName: _myUserName,
            userNames: _myNames
        });
    }

    function trySetMyUserInfo() {
        try {
            var u = findUser();
            if (!u) return !!(_myUserId || _myUserName);
            var changed = false;
            if (u.id && u.id !== _myUserId) { _myUserId = u.id; changed = true; }
            var before = _myNames.length;
            if (u.name) addName(u.name);
            if (u.first) addName(u.first);
            if (u.last) addName(u.last);
            if (u.username) addName(u.username);
            if (u.first && u.last) addName(u.first + ' ' + u.last);
            if (changed || _myNames.length !== before) sendUserInfo();
            return !!(_myUserId || _myUserName);
        } catch (e) {}
        return false;
    }

    function rememberSent(text) {
        if (!text) return;
        var t = String(text).replace(/\s+/g, ' ').trim();
        if (t.length < 1 || t.length > 2000) return;
        _recentSent.push({ text: t, t: Date.now() });
        if (_recentSent.length > 30) _recentSent.shift();
    }

    function isRecentlySent(body) {
        if (!body) return false;
        var b = String(body).replace(/\s+/g, ' ').trim();
        if (!b) return false;
        var now = Date.now();
        for (var i = 0; i < _recentSent.length; i++) {
            var s = _recentSent[i];
            if (now - s.t > 20000) continue;
            if (b === s.text) return true;
            if (s.text.length >= 8 && b.indexOf(s.text) !== -1) return true;
        }
        return false;
    }

    function extractSender(data) {
        var id = null, name = null, chatId = null;
        if (!data) return { id: id, name: name, chatId: chatId };
        if (typeof data === 'string') {
            try { data = JSON.parse(data); } catch (e) { return { id: id, name: name, chatId: chatId }; }
        }
        if (typeof data !== 'object') return { id: id, name: name, chatId: chatId };
        var sender = data.sender || data.from || data.author || data.user || data.actor || {};
        if (typeof sender !== 'object') sender = { id: sender };
        id = data.senderId || data.sender_id || data.fromUserId || data.from_id || data.authorId
            || data.userId || sender.id || sender.userId || sender.uid || null;
        name = data.senderName || data.fromName || data.authorName
            || sender.firstName || sender.name || sender.username || sender.displayName || null;
        chatId = data.conversationId || data.chatId || data.channelId || data.roomId || data.dialogId || null;
        if (id != null) id = String(id);
        if (name != null) name = String(name);
        if (chatId != null) chatId = String(chatId);
        return { id: id, name: name, chatId: chatId };
    }

    function nameIsOwn(value) {
        if (!value || !_myNames.length) return false;
        var lower = String(value).replace(/\s+/g, ' ').trim().toLowerCase();
        for (var i = 0; i < _myNames.length; i++) {
            if (lower === _myNames[i].toLowerCase()) return true;
        }
        return false;
    }

    function bodyHasOwnPrefix(body) {
        if (!body) return false;
        var t = String(body).replace(/^\s+/, '');
        if (/^(вы|you|я)\s*[:\-—]/i.test(t)) return true;
        var lower = t.toLowerCase();
        for (var i = 0; i < _myNames.length; i++) {
            var n = _myNames[i].toLowerCase();
            if (n.length < 2) continue;
            if (lower.indexOf(n) !== 0) continue;
            var next = t.charAt(n.length);
            if (next === ':' || next === '-' || next === '—' || next === ',') return true;
        }
        return false;
    }

    function isOwnNotification(title, body, sender) {
        trySetMyUserInfo();
        if (isRecentlySent(body)) return true;
        if (_myUserId && sender.id && String(sender.id) === String(_myUserId)) return true;
        if (nameIsOwn(title) || nameIsOwn(sender.name)) return true;
        if (bodyHasOwnPrefix(body)) return true;
        return false;
    }

    function inspectOutgoing(raw, url) {
        try {
            if (url && !/chat|message|msg|send|im\/|messenger|conversation|ws/i.test(String(url))) return;
            var s = typeof raw === 'string' ? raw : '';
            if (!s && raw && raw.toString && raw.constructor && raw.constructor.name !== 'FormData')
                s = String(raw);
            if (!s || s.length > 20000) return;
            var obj = null;
            try { obj = JSON.parse(s); } catch (e) {}
            if (obj) {
                var keys = ['text', 'message', 'content', 'body'];
                for (var i = 0; i < keys.length; i++) {
                    if (typeof obj[keys[i]] === 'string') rememberSent(obj[keys[i]]);
                }
                if (obj.data && typeof obj.data === 'object') {
                    for (var j = 0; j < keys.length; j++) {
                        if (typeof obj.data[keys[j]] === 'string') rememberSent(obj.data[keys[j]]);
                    }
                }
            }
        } catch (e) {}
    }

    document.addEventListener('keydown', function(e) {
        if (e.key !== 'Enter' || e.shiftKey) return;
        var el = e.target;
        if (!el) return;
        var text = (el.value != null ? el.value : el.innerText) || '';
        rememberSent(text);
    }, true);

    document.addEventListener('click', function(e) {
        var btn = e.target && e.target.closest && e.target.closest('button, [role=""button""]');
        if (!btn) return;
        var label = ((btn.getAttribute('aria-label') || btn.title || btn.innerText || '') + ' ' + (btn.className || '')).toLowerCase();
        if (!/send|отправ|сообщ|paper|submit/i.test(label)) return;
        var root = btn.closest('form, footer, [class*=""input""], [class*=""compose""], [class*=""chat""]') || document.body;
        var field = root.querySelector('textarea, [contenteditable=""true""], input[type=""text""]');
        if (field) rememberSent(field.value != null ? field.value : field.innerText);
    }, true);

    try {
        var origFetch = window.fetch;
        if (origFetch) {
            window.fetch = function() {
                try {
                    var url = arguments[0] && arguments[0].url ? arguments[0].url : arguments[0];
                    var opts = arguments[1] || {};
                    if (opts.body) inspectOutgoing(opts.body, url);
                } catch (e) {}
                return origFetch.apply(this, arguments);
            };
        }
    } catch (e) {}

    try {
        var OrigWS = window.WebSocket;
        window.WebSocket = function(url, protocols) {
            var ws = protocols !== undefined ? new OrigWS(url, protocols) : new OrigWS(url);
            var origSend = ws.send;
            ws.send = function(data) {
                try { inspectOutgoing(data, url); } catch (e) {}
                return origSend.apply(this, arguments);
            };
            return ws;
        };
        window.WebSocket.prototype = OrigWS.prototype;
        window.WebSocket.CONNECTING = OrigWS.CONNECTING;
        window.WebSocket.OPEN = OrigWS.OPEN;
        window.WebSocket.CLOSING = OrigWS.CLOSING;
        window.WebSocket.CLOSED = OrigWS.CLOSED;
    } catch (e) {}

    window.Notification = function(title, options) {
        trySetMyUserInfo();
        var body = (options && options.body) ? options.body : '';
        var sender = extractSender(options && options.data);
        var own = isOwnNotification(title, body, sender);
        if (window.chrome && window.chrome.webview) {
            post({
                type: 'webNotification',
                title: title,
                body: body,
                senderId: sender.id,
                senderName: sender.name,
                conversationId: sender.chatId,
                isOwn: own
            });
        }
        var notif = {
            close: function() {},
            onclick: null,
            onclose: null,
            onerror: null,
            onshow: null,
            title: title,
            body: body,
            data: (options && options.data) || null
        };
        setTimeout(function() { if (notif.onshow) notif.onshow(); }, 0);
        return notif;
    };
    window.Notification.prototype = OriginalNotification.prototype;
    window.Notification.requestPermission = function() { return Promise.resolve('granted'); };
    Object.defineProperty(window.Notification, 'permission', {
        get: function() { return 'granted'; },
        configurable: true
    });

    var attempts = 0;
    var interval = setInterval(function() {
        var ok = trySetMyUserInfo();
        if ((ok && _myUserId && _myUserName) || ++attempts >= 120) clearInterval(interval);
    }, 500);
})();
";

        // Следит за цифрами рядом с пунктами сайдбара TNovPRO (Чат, Проекты, …)
        // и шлёт снимок в WPF. Идемпотентен: повторный запуск на той же странице — no-op.
        private const string SidebarBadgeScript = @"
(function() {
    if (window.__tnovSidebarBadgeWatch) return;
    window.__tnovSidebarBadgeWatch = true;

    var LABELS = ['Дашборд','Проекты','Таймшит','Каналы связи','Чат','Обращения','Опросы','Wiki'];

    function post(payload) {
        try {
            if (window.chrome && window.chrome.webview)
                window.chrome.webview.postMessage(JSON.stringify(payload));
        } catch (e) {}
    }

    function parseCount(text) {
        if (!text) return 0;
        var t = String(text).replace(/\s+/g, ' ').trim();
        if (/^\d+$/.test(t)) return parseInt(t, 10);
        if (/^\d+\+$/.test(t)) return parseInt(t, 10);
        return 0;
    }

    function ownText(el) {
        var t = '';
        if (!el || !el.childNodes) return t;
        for (var i = 0; i < el.childNodes.length; i++) {
            var n = el.childNodes[i];
            if (n.nodeType === 3) t += n.textContent;
        }
        return t.replace(/\s+/g, ' ').trim();
    }

    function isVisible(el) {
        if (!el || !el.getClientRects) return false;
        try {
            var s = window.getComputedStyle(el);
            if (!s || s.display === 'none' || s.visibility === 'hidden' || s.opacity === '0') return false;
            var cls = String(el.className || '');
            if (/invisible|MuiBadge-invisible/i.test(cls)) return false;
            if (el.getClientRects().length === 0) return false;
        } catch (e) { return false; }
        return true;
    }

    function labelHits(text) {
        if (!text) return 0;
        var n = 0;
        for (var i = 0; i < LABELS.length; i++) {
            if (text.indexOf(LABELS[i]) !== -1) n++;
        }
        return n;
    }

    function findRow(labelEl) {
        var cur = labelEl;
        var prev = labelEl;
        while (cur.parentElement && cur.parentElement !== document.body) {
            var parentText = (cur.parentElement.innerText || '').replace(/\s+/g, ' ');
            if (labelHits(parentText) >= 2) return prev;
            prev = cur;
            cur = cur.parentElement;
        }
        return prev;
    }

    function matchLabel(text) {
        if (!text) return null;
        var t = text.replace(/\s+/g, ' ').trim();
        for (var i = 0; i < LABELS.length; i++) {
            var l = LABELS[i];
            if (t === l) return { label: l, count: 0 };
            if (t.indexOf(l) === 0) {
                var rest = t.slice(l.length).trim();
                var c = parseCount(rest);
                if (rest === '' || c > 0) return { label: l, count: c };
            }
        }
        return null;
    }

    function ariaCount(el, label) {
        if (!el || !el.getAttribute) return 0;
        var a = (el.getAttribute('aria-label') || el.getAttribute('title') || '').replace(/\s+/g, ' ').trim();
        if (!a) return 0;
        if (label && a.indexOf(label) === -1 && a !== label) return 0;
        var src = label && a.indexOf(label) !== -1 ? a.slice(a.indexOf(label) + label.length) : a;
        var m = src.match(/(\d+)/);
        return m ? parseInt(m[1], 10) : 0;
    }

    function pseudoCount(el) {
        try {
            var parts = ['::after', '::before'];
            for (var i = 0; i < parts.length; i++) {
                var c = window.getComputedStyle(el, parts[i]).content;
                if (!c || c === 'none' || c === 'normal') continue;
                var m = String(c).match(/(\d+)/);
                if (m) return parseInt(m[1], 10);
            }
        } catch (e) {}
        return 0;
    }

    function findBadgeIn(row, label) {
        if (!row) return 0;
        var best = ariaCount(row, label);
        var els = row.querySelectorAll('*');
        for (var i = 0; i < els.length; i++) {
            var el = els[i];
            var n = parseCount(ownText(el));
            if (!n && el.children.length === 0)
                n = parseCount((el.innerText || el.textContent || '').replace(/\s+/g, ' ').trim());
            if (!n) n = pseudoCount(el);
            if (n <= 0 || n > 9999) continue;
            if (!isVisible(el) && !pseudoCount(el)) continue;
            var cls = String(el.className || '');
            var w = el.offsetWidth || 0, h = el.offsetHeight || 0;
            var looksLikeBadge = /badge|counter|unread|count|notify|chip/i.test(cls)
                || (w > 0 && h > 0 && w <= 44 && h <= 32)
                || ownText(el).length <= 3;
            if (!looksLikeBadge) continue;
            if (n > best) best = n;
        }
        if (!best) {
            var leftover = (row.innerText || '').split(label).join(' ').replace(/\s+/g, ' ').trim();
            if (/^\d+\+?$/.test(leftover)) best = parseCount(leftover);
        }
        return best;
    }

    function walkLabels(root, items, found) {
        if (!root || !root.querySelectorAll) return;

        var walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, null);
        var node;
        while ((node = walker.nextNode())) {
            var hit = matchLabel(node.textContent);
            if (!hit) continue;
            if (found.indexOf(hit.label) === -1) found.push(hit.label);
            var el = node.parentElement;
            if (!el) continue;
            var row = findRow(el);
            var count = Math.max(hit.count, findBadgeIn(row, hit.label));
            if (count > 0 && (!items[hit.label] || count > items[hit.label]))
                items[hit.label] = count;
        }

        var clickables = root.querySelectorAll('a, button, [role=""menuitem""], [role=""link""], [role=""tab""]');
        for (var i = 0; i < clickables.length; i++) {
            var el = clickables[i];
            var a = (el.getAttribute('aria-label') || el.getAttribute('title') || '').replace(/\s+/g, ' ').trim();
            if (!a) continue;
            for (var j = 0; j < LABELS.length; j++) {
                var label = LABELS[j];
                if (a === label || a.indexOf(label) === 0) {
                    var count = ariaCount(el, label) || findBadgeIn(el, label);
                    if (count > 0 && (!items[label] || count > items[label]))
                        items[label] = count;
                }
            }
        }

        var all = root.querySelectorAll('*');
        for (var i = 0; i < all.length; i++) {
            if (all[i].shadowRoot) walkLabels(all[i].shadowRoot, items, found);
        }
    }

    function collectItems() {
        var items = {};
        var found = [];
        var root = document.querySelector('nav, aside, [class*=""sidebar"" i], [class*=""SideBar""], [class*=""side-bar"" i], [class*=""SideNav""]') || document.body;
        walkLabels(root, items, found);
        return { items: items, labelsFound: found };
    }

    function titleCount() {
        var m = String(document.title || '').match(/^\((\d+)\)/);
        return m ? parseInt(m[1], 10) : 0;
    }

    var last = '';
    function collectAndPost() {
        try {
            var snapshot = collectItems();
            var payload = { type: 'sidebarBadges', items: snapshot.items, titleCount: titleCount(), labelsFound: snapshot.labelsFound };
            var key = JSON.stringify(payload);
            if (key === last) return;
            last = key;
            post(payload);
        } catch (e) {}
    }

    var timer = null;
    function schedule() {
        if (timer) return;
        timer = setTimeout(function() {
            timer = null;
            collectAndPost();
        }, 250);
    }

    function start() {
        if (!document.body) {
            setTimeout(start, 200);
            return;
        }
        collectAndPost();
        try {
            var obs = new MutationObserver(schedule);
            obs.observe(document.body, { subtree: true, childList: true, characterData: true });
        } catch (e) {}
        setInterval(collectAndPost, 2500);
    }

    if (document.readyState === 'loading')
        document.addEventListener('DOMContentLoaded', start);
    else
        start();
})();
";

        // Диагностика звонков: оборачиваем getUserMedia и сообщаем в WPF точную причину сбоя
        // (имя ошибки, сообщение и список доступных устройств).
        private const string MediaDiagnosticsScript = @"
(function() {
    function post(payload) {
        try {
            if (window.chrome && window.chrome.webview)
                window.chrome.webview.postMessage(JSON.stringify(payload));
        } catch (e) {}
    }

    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
        post({ type: 'mediaError', phase: 'init', name: 'NoMediaDevices',
               message: 'navigator.mediaDevices.getUserMedia недоступен (небезопасный контекст?)',
               devices: '' });
        return;
    }

    function describeDevices() {
        return navigator.mediaDevices.enumerateDevices().then(function(devs) {
            return devs.map(function(d) {
                return d.kind + ':' + (d.label || '(без метки)');
            }).join(' | ');
        }).catch(function() { return 'enumerateFailed'; });
    }

    const original = navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);
    navigator.mediaDevices.getUserMedia = function(constraints) {
        return original(constraints).then(function(stream) {
            post({ type: 'mediaOk', constraints: JSON.stringify(constraints || {}) });
            return stream;
        }).catch(function(err) {
            describeDevices().then(function(detail) {
                post({ type: 'mediaError', phase: 'getUserMedia',
                       name: err.name, message: err.message, devices: detail });
            });
            throw err;
        });
    };

    // Независимая проба через 4 c после загрузки: отдельно микрофон и камера,
    // чтобы понять, какое именно устройство не стартует. Треки сразу останавливаем.
    setTimeout(function() {
        original({ audio: true }).then(function(s) {
            s.getTracks().forEach(function(t) { t.stop(); });
            post({ type: 'probe', kind: 'audio', result: 'ok' });
        }).catch(function(e) {
            post({ type: 'probe', kind: 'audio', result: e.name + ': ' + e.message });
        });

        original({ video: true }).then(function(s) {
            s.getTracks().forEach(function(t) { t.stop(); });
            post({ type: 'probe', kind: 'video', result: 'ok' });
        }).catch(function(e) {
            post({ type: 'probe', kind: 'video', result: e.name + ': ' + e.message });
        });
    }, 4000);
})();
";

        #endregion
    }
}
