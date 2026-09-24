using System.Windows;
using System.Windows.Controls;

namespace TNovDesktop
{
    public partial class MessengerControl : UserControl
    {
        private const string Url = "https://tnov.pm-nova.ru";

        private readonly WebViewTabController _tab;

        public event Action<string, string, string> ShowToastRequested;
        public event Action<IReadOnlyDictionary<string, int>, int> SidebarBadgesChanged;

        public MessengerControl()
        {
            InitializeComponent();

            _tab = new WebViewTabController(WebView, LoadingProgress, ErrorPanel, ErrorDetails, Url, allowMedia: true, watchSidebarBadges: true);
            _tab.ShowToastRequested += (title, body, convId) => ShowToastRequested?.Invoke(title, body, convId);
            _tab.SidebarBadgesChanged += (items, total) => SidebarBadgesChanged?.Invoke(items, total);

            Loaded += (s, e) => _tab.OnLoaded();
            Unloaded += (s, e) => _tab.OnUnloaded();
        }

        public void NavigateToChat(string chatId) => _tab.NavigateToChat(chatId);

        public void Refresh() => _tab.Refresh();

        private void RetryButton_Click(object sender, RoutedEventArgs e) => _tab.Retry();
    }
}
