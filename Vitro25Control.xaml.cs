using System.Windows;
using System.Windows.Controls;

namespace TNovDesktop
{
    public partial class Vitro25Control : UserControl
    {
        private const string Url = "https://vitro.pm-nova.ru/site/3064dc08-2e02-8de4-aa70-1b2ae9eb890b/list/716e8d52-90dc-4c85-bddf-582c94ab505e/view/72597602-967e-4b40-b737-e3be2d011637";

        private readonly WebViewTabController _tab;

        public Vitro25Control()
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
