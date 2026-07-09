using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
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

        private CoreWebView2Environment _environment;
        private bool _isInitialized;
        private bool _isSubscribed;
        private string _pendingNavigationChatId;
        private string _currentUserId;
        private string _currentUserName;

        /// <summary>Запрос показа тост-уведомления: (title, body, conversationId).</summary>
        public event Action<string, string, string> ShowToastRequested;

        public WebViewTabController(
            WebView2 webView,
            ProgressBar loading,
            Border errorPanel,
            TextBlock errorDetails,
            string url,
            bool allowMedia = false)
        {
            _webView = webView;
            _loading = loading;
            _errorPanel = errorPanel;
            _errorDetails = errorDetails;
            _url = url;
            _allowMedia = allowMedia;
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
            if (_isSubscribed && _webView.CoreWebView2 != null)
            {
                _webView.CoreWebView2.NavigationStarting -= OnNavigationStarting;
                _webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                _webView.CoreWebView2.NewWindowRequested -= OnNewWindowRequested;
                _isSubscribed = false;
            }

            // Останавливаем навигацию, чтобы вкладка не грузила ресурсы в фоне.
            _webView.CoreWebView2?.Stop();
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

                _webView.CoreWebView2.PermissionRequested += OnPermissionRequested;

                Subscribe();
                await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(EarlyInjectionScript);

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

            _webView.CoreWebView2.NavigationStarting += OnNavigationStarting;
            _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            _webView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
            _isSubscribed = true;
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

        // Окна звонков, открываемые сайтом через window.open, по умолчанию блокируются
        // WebView2. Открываем их в дочернем окне с тем же окружением и разрешениями.
        private async void OnNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            var deferral = e.GetDeferral();
            try
            {
                var env = await WebViewEnvironment.GetAsync();
                var popupView = new WebView2();

                var popupWindow = new Window
                {
                    Title = "TNovDesktop",
                    Width = 900,
                    Height = 700,
                    Content = popupView,
                    Owner = Window.GetWindow(_webView),
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };

                await popupView.EnsureCoreWebView2Async(env);
                popupView.CoreWebView2.PermissionRequested += OnPermissionRequested;
                popupView.CoreWebView2.WindowCloseRequested += (s, a) => popupWindow.Close();

                e.NewWindow = popupView.CoreWebView2;
                e.Handled = true;
                popupWindow.Show();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WPF] NewWindowRequested error: {ex.Message}");
            }
            finally
            {
                deferral.Complete();
            }
        }

        #endregion

        #region Навигация и панель ошибок

        private void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            _webView.Dispatcher.Invoke(() =>
            {
                if (_loading != null) _loading.Visibility = Visibility.Visible;
                if (_errorPanel != null) _errorPanel.Visibility = Visibility.Collapsed;
                _webView.Visibility = Visibility.Visible;
            });
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            _webView.Dispatcher.Invoke(() =>
            {
                if (_loading != null) _loading.Visibility = Visibility.Collapsed;
                if (!e.IsSuccess)
                    ShowError($"Не удалось загрузить страницу: {e.WebErrorStatus}");
            });

            if (e.IsSuccess && !string.IsNullOrEmpty(_pendingNavigationChatId))
                NavigateToChat(_pendingNavigationChatId);
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
            _webView.Dispatcher.Invoke(() => ProcessMessage(json));
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
                }
                else if (type == "webNotification")
                {
                    string title = doc.RootElement.GetProperty("title").GetString();
                    string body = doc.RootElement.TryGetProperty("body", out var b) ? b.GetString() : "";
                    string senderId = doc.RootElement.TryGetProperty("senderId", out var sid) && sid.ValueKind != System.Text.Json.JsonValueKind.Null
                        ? sid.GetString() : null;

                    // Не показываем собственные уведомления.
                    if (!string.IsNullOrEmpty(_currentUserName) && title == _currentUserName) return;
                    if (!string.IsNullOrEmpty(_currentUserId) && senderId == _currentUserId) return;

                    ShowToastRequested?.Invoke(title, body, Guid.NewGuid().ToString());
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

        private const string EarlyInjectionScript = @"
(function() {
    const OriginalNotification = window.Notification;
    let _myUserId = null;
    let _myUserName = null;

    function trySetMyUserInfo() {
        if (_myUserId && _myUserName) return true;
        try {
            const stored = sessionStorage.getItem('mobileUser');
            if (stored) {
                const user = JSON.parse(stored);
                if (user && user.id) {
                    _myUserId = String(user.id);
                    _myUserName = user.firstName || user.username || '';
                    sendUserInfo();
                    return !!_myUserName;
                }
            }
            if (window.me && window.me.id) {
                _myUserId = String(window.me.id);
                _myUserName = window.me.firstName || window.me.username || '';
                sendUserInfo();
                return !!_myUserName;
            }
        } catch(e) {}
        return false;
    }

    function sendUserInfo() {
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage(JSON.stringify({
                type: 'setCurrentUserId',
                userId: _myUserId
            }));
        }
        if (_myUserName && window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage(JSON.stringify({
                type: 'setCurrentUserName',
                userName: _myUserName
            }));
        }
    }

    window.Notification = function(title, options) {
        if (window.chrome && window.chrome.webview) {
            const body = (options && options.body) ? options.body : '';
            const senderId = (options && options.data && options.data.senderId) ? String(options.data.senderId) : null;
            window.chrome.webview.postMessage(JSON.stringify({
                type: 'webNotification',
                title: title,
                body: body,
                senderId: senderId
            }));
        }
        const notif = {
            close: function() {},
            onclick: null,
            onclose: null,
            onerror: null,
            onshow: null,
            title: title,
            body: (options && options.body) || '',
            data: (options && options.data) || null,
        };
        setTimeout(() => { if (notif.onshow) notif.onshow(); }, 0);
        return notif;
    };
    window.Notification.prototype = OriginalNotification.prototype;
    window.Notification.requestPermission = () => Promise.resolve('granted');
    Object.defineProperty(window.Notification, 'permission', {
        get: () => 'granted',
        configurable: true
    });

    let attempts = 0;
    const interval = setInterval(() => {
        if (trySetMyUserInfo() || ++attempts >= 120) clearInterval(interval);
    }, 500);
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
