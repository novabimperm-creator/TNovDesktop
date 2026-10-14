using System.Windows;
using System.Windows.Controls;

namespace TNovDesktop
{
    public partial class MailControl : UserControl
    {
        internal const string Url = "https://mail.talan.group";

        private readonly WebViewTabController _tab;

        public MailControl()
        {
            InitializeComponent();

            _tab = new WebViewTabController(WebView, LoadingProgress, ErrorPanel, ErrorDetails, Url);

            Loaded += (s, e) => _tab.OnLoaded();
            Unloaded += (s, e) => _tab.OnUnloaded();
        }

        public void Refresh() => _tab.Refresh();

        /// <summary>
        /// Открыть письмо по EWS ItemId в OWA. Через window.open внутри вкладки — popup с её сессией
        /// (хост тот же), а сама вкладка остаётся во «Входящих». Вкладка ещё не открывалась — в браузере.
        /// </summary>
        public void OpenItem(string ewsItemId)
        {
            string url = $"{Url}/owa/?ItemID={Uri.EscapeDataString(ewsItemId)}&exvsurl=1&viewmodel=ReadMessageItem";
            var core = WebView.CoreWebView2;
            if (core != null)
            {
                _ = core.ExecuteScriptAsync($"window.open({System.Text.Json.JsonSerializer.Serialize(url)}, '_blank');");
                return;
            }
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write($"[MAIL] Не удалось открыть письмо: {ex.Message}"); }
        }

        private void RetryButton_Click(object sender, RoutedEventArgs e) => _tab.Retry();
    }
}
