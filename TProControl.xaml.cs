using System.Windows;
using System.Windows.Controls;

namespace TNovDesktop
{
    public partial class TProControl : UserControl
    {
        private const string Url = "https://xn--n1abdg.xn--p1ai/designing/home/drawings";

        private readonly WebViewTabController _tab;

        public TProControl()
        {
            InitializeComponent();

            _tab = new WebViewTabController(WebView, LoadingProgress, ErrorPanel, ErrorDetails, Url);

            Loaded += (s, e) => _tab.OnLoaded();
            Unloaded += (s, e) => _tab.OnUnloaded();
        }

        public void Refresh() => _tab.Refresh();

        private void RetryButton_Click(object sender, RoutedEventArgs e) => _tab.Retry();
    }
}
