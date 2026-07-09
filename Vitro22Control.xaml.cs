using System.Windows;
using System.Windows.Controls;

namespace TNovDesktop
{
    public partial class Vitro22Control : UserControl
    {
        private const string Url = "http://vitrocad-nova/_layouts/15/Vitro/TableView/ListView.aspx?List=VitroTasks&listname=Задачи%20рабочих%20процессов%20(Vitro)";

        private readonly WebViewTabController _tab;

        public Vitro22Control()
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
