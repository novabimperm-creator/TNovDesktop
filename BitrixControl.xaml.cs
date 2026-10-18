using System.Windows;
using System.Windows.Controls;

namespace TNovDesktop
{
    public partial class BitrixControl : UserControl
    {
        private const string Url = "https://portal.talan.group/online/";

        private readonly WebViewTabController _tab;

        public event Action<string, string, string> ShowToastRequested;

        /// <summary>Число непрочитанных (из заголовка страницы Битрикса).</summary>
        public event Action<int> UnreadChanged;

        public BitrixControl()
        {
            InitializeComponent();

            // Битрикс сам не уведомляет о своих сообщениях, а поиск «своего» пользователя
            // в localStorage портала может ошибочно принять собеседника за себя.
            _tab = new WebViewTabController(WebView, LoadingProgress, ErrorPanel, ErrorDetails, Url,
                allowMedia: true,
                watchTitleCount: true,
                reportPageActivity: true,
                detectOwnUser: false,
                chatUrl: id => $"{Url}?IM_DIALOG={Uri.EscapeDataString(id)}");
            _tab.ShowToastRequested += (title, body, convId) => ShowToastRequested?.Invoke(title, body, convId);
            _tab.TitleCountChanged += count => UnreadChanged?.Invoke(count);

            Loaded += (s, e) => _tab.OnLoaded();
            Unloaded += (s, e) => _tab.OnUnloaded();
        }

        public void NavigateToChat(string chatId) => _tab.NavigateToChat(chatId);

        public void SetPageActive(bool active) => _tab.SetPageActive(active);

        public void Refresh() => _tab.Refresh();

        private void RetryButton_Click(object sender, RoutedEventArgs e) => _tab.Retry();
    }
}
