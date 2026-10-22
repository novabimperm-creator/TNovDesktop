using System.Windows;
using System.Windows.Controls;

namespace TNovDesktop
{
    public partial class TelegramControl : UserControl
    {
        private const string Url = "https://web.telegram.org/a/";

        private readonly WebViewTabController _tab;

        public event Action<string, string, string> ShowToastRequested;

        /// <summary>Число непрочитанных (Telegram передаёт его в navigator.setAppBadge).</summary>
        public event Action<int> UnreadChanged;

        public TelegramControl()
        {
            InitializeComponent();

            // Telegram Web A уведомляет, только когда страница без фокуса, а при живом
            // service worker отдаёт уведомление ему — поэтому свой скрипт-перехватчик.
            _tab = new WebViewTabController(WebView, LoadingProgress, ErrorPanel, ErrorDetails, Url,
                allowMedia: true,
                reportPageActivity: true,
                detectOwnUser: false,
                chatUrl: id => $"{Url}#{Uri.EscapeDataString(id)}",
                extraScript: TelegramScript);
            _tab.ShowToastRequested += (title, body, convId) => ShowToastRequested?.Invoke(title, body, convId);
            _tab.TitleCountChanged += count => UnreadChanged?.Invoke(count);

            Loaded += (s, e) => _tab.OnLoaded();
            Unloaded += (s, e) => _tab.OnUnloaded();
        }

        public void NavigateToChat(string chatId) => _tab.NavigateToChat(chatId);

        public void SetPageActive(bool active) => _tab.SetPageActive(active);

        public void Refresh() => _tab.Refresh();

        private void RetryButton_Click(object sender, RoutedEventArgs e) => _tab.Retry();

        // setAppBadge — общий счётчик непрочитанных; postMessage воркеру с типом
        // showMessageNotification — новое сообщение (воркеру не передаём, иначе дубль).
        private const string TelegramScript = @"
(function() {
    function post(payload) {
        try {
            if (window.chrome && window.chrome.webview)
                window.chrome.webview.postMessage(JSON.stringify(payload));
        } catch (e) {}
    }

    try {
        navigator.setAppBadge = function(count) {
            post({ type: 'appBadge', count: Number(count) || 0 });
            return Promise.resolve();
        };
        navigator.clearAppBadge = function() {
            post({ type: 'appBadge', count: 0 });
            return Promise.resolve();
        };
    } catch (e) {}

    try {
        if (window.ServiceWorker && ServiceWorker.prototype.postMessage) {
            var origPostMessage = ServiceWorker.prototype.postMessage;
            ServiceWorker.prototype.postMessage = function(message) {
                if (message && message.type === 'showMessageNotification' && message.payload) {
                    var p = message.payload;
                    post({
                        type: 'webNotification',
                        title: p.title || 'Telegram',
                        body: p.body || '',
                        conversationId: p.chatId != null ? String(p.chatId) : null
                    });
                    return;
                }
                return origPostMessage.apply(this, arguments);
            };
        }
    } catch (e) {}
})();
";
    }
}
