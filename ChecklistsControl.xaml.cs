using Newtonsoft.Json.Linq;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using TNovApi.Client;
using TNovUtils.Checklist.Report;

namespace TNovDesktop
{
    /// <summary>
    /// Вкладка «Чек-листы»: тот же отчёт, что кнопка «Отчет» в Revit (ядро — TNovUtils\Checklist\Report,
    /// подключено ссылкой). Журнал синхронизаций читается из {ServerPath}projects\, JSON Чек-листа —
    /// оттуда же или из TNovApi (по "ChecklistStorage"), модели не открываются.
    /// </summary>
    public partial class ChecklistsControl : UserControl
    {
        private const string DefaultServerPath = @"\\fs-nova\Distr\0.For Admin\_TNov\";

        private static readonly object _clientLock = new object();
        private static TNovApiClient? _client;
        private static string? _clientSignature;

        private readonly ReportViewModel _vm;
        private bool _loadedOnce;

        public ChecklistsControl()
        {
            InitializeComponent();
            _vm = new ReportViewModel(ResolveServerPath, CreateSource);
            DataContext = _vm;

            // Сбор занимает несколько секунд — запускаем при первом показе вкладки, а не при старте окна.
            IsVisibleChanged += (s, e) =>
            {
                if (IsVisible && !_loadedOnce)
                {
                    _loadedOnce = true;
                    RefreshData();
                }
            };
        }

        public void RefreshData()
        {
            string server = ResolveServerPath();
            bool online;
            try { online = Directory.Exists(server); }
            catch { online = false; }

            StatusPanel.Visibility = online ? Visibility.Collapsed : Visibility.Visible;
            HeadPanel.Visibility = online ? Visibility.Visible : Visibility.Collapsed;
            TableContainer.Visibility = online ? Visibility.Visible : Visibility.Collapsed;
            if (!online)
            {
                StatusHint.Text = "Нет доступа к " + server;
                _loadedOnce = false; // повторим при следующем показе вкладки
                return;
            }

            _ = _vm.RefreshAsync();
        }

        /// <summary>
        /// ServerPath из конфигурации клиента TNov (%USERPROFILE%\TNovClient\TNovConfig.json) —
        /// так отчёт работает и в хабаровском офисе. Без конфигурации — пермский сервер.
        /// </summary>
        private static string ResolveServerPath()
        {
            string? path = ReadConfig()?["ServerPath"]?.ToString();
            return string.IsNullOrWhiteSpace(path) ? DefaultServerPath : path;
        }

        /// <summary>
        /// Источник JSON Чек-листа — как в плагине (DocumentStores.ForChecklist):
        /// "ChecklistStorage": "api" + "ApiUrl" (+ "ApiKey") — TNovApi, иначе файлы шары.
        /// </summary>
        private static IChecklistDataSource CreateSource()
        {
            JObject? config = ReadConfig();
            string? storage = config?["ChecklistStorage"]?.ToString();
            string? apiUrl = config?["ApiUrl"]?.ToString();
            if (!ChecklistDataSources.UsesApi(storage, apiUrl))
                return new FileChecklistSource(ResolveServerPath());

            string? apiKey = config?["ApiKey"]?.ToString();
            return new ApiChecklistSource(() => GetClient(apiUrl!, apiKey));
        }

        /// <summary>Один HttpClient на процесс: keep-alive экономит рукопожатия при каждом обновлении.</summary>
        private static TNovApiClient GetClient(string apiUrl, string? apiKey)
        {
            string signature = apiUrl + "|" + apiKey;
            lock (_clientLock)
            {
                if (_client == null || _clientSignature != signature)
                {
                    _client?.Dispose();
                    _client = new TNovApiClient(new TNovApiClientOptions
                    {
                        BaseAddress = new Uri(apiUrl),
                        ApiKey = apiKey,
                        UserName = Environment.UserName,
                        Timeout = TimeSpan.FromSeconds(10)
                    });
                    _clientSignature = signature;
                }
                return _client;
            }
        }

        private static JObject? ReadConfig()
        {
            try
            {
                string config = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "TNovClient", "TNovConfig.json");
                if (File.Exists(config))
                    return JObject.Parse(File.ReadAllText(config));
            }
            catch { }
            return null;
        }
    }
}
