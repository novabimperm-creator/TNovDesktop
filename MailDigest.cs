using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Globalization;
using System.IO;
using System.Windows.Threading;

namespace TNovDesktop
{
    /// <summary>
    /// Сводка по «Входящим» за период: счётчики, частые отправители и важное —
    /// то, что показывается в уведомлении и в окне сводки.
    /// </summary>
    internal sealed class MailDigest
    {
        private const int TopSendersCount = 3;
        private const int ImportantInSummary = 3;

        public DateTime Since { get; }
        public DateTime Until { get; }

        /// <summary>Важные и с флагом, затем непрочитанные, затем остальные; внутри группы — новые первыми.</summary>
        public IReadOnlyList<MailItemInfo> Items { get; }

        public int Total => Items.Count;
        public int Unread { get; }
        public IReadOnlyList<MailItemInfo> Important { get; }
        public IReadOnlyList<(string Name, int Count)> TopSenders { get; }

        public MailDigest(DateTime since, DateTime until, IEnumerable<MailItemInfo> items)
        {
            Since = since;
            Until = until;
            Items = items
                .OrderByDescending(IsImportant)
                .ThenBy(i => i.IsRead)
                .ThenByDescending(i => i.Received)
                .ToList();
            Unread = Items.Count(i => !i.IsRead);
            Important = Items.Where(IsImportant).ToList();
            TopSenders = Items
                .GroupBy(SenderOf, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .OrderByDescending(g => g.Count())
                .Take(TopSendersCount)
                .Select(g => (g.Key, g.Count()))
                .ToList();
        }

        public static bool IsImportant(MailItemInfo item) => item.IsHighImportance || item.IsFlagged;

        public static string SenderOf(MailItemInfo item) =>
            !string.IsNullOrWhiteSpace(item.FromName) ? item.FromName
            : !string.IsNullOrWhiteSpace(item.FromAddress) ? item.FromAddress
            : "(без отправителя)";

        /// <summary>«Почта: 37 писем с пн 10:00»</summary>
        public string Headline =>
            Total == 0
                ? $"Почта: новых писем нет с {FormatSince()}"
                : $"Почта: {Total} {Plural(Total, "письмо", "письма", "писем")} с {FormatSince()}";

        /// <summary>Краткая выжимка в 2–3 строки для уведомления и шапки окна.</summary>
        public string Summary
        {
            get
            {
                if (Total == 0)
                    return "Во «Входящих» ничего нового.";

                var lines = new List<string>
                {
                    $"Не прочитано: {Unread}" + (Important.Count > 0 ? $" · важных: {Important.Count}" : "")
                };
                if (TopSenders.Count > 0)
                    lines.Add("Чаще всего: " + string.Join(", ", TopSenders.Select(s => $"{s.Name} ({s.Count})")));
                if (Important.Count > 0)
                    lines.Add("Важное: " + string.Join("; ", Important.Take(ImportantInSummary)
                        .Select(i => $"«{Shorten(i.Subject, 40)}» — {SenderOf(i)}"))
                        + (Important.Count > ImportantInSummary ? "…" : ""));
                return string.Join(Environment.NewLine, lines);
            }
        }

        private string FormatSince()
        {
            var ru = CultureInfo.GetCultureInfo("ru-RU");
            if (Since.Date == Until.Date)
                return Since.ToString("HH:mm", ru);
            if (Since.Date == Until.Date.AddDays(-1))
                return "вчера " + Since.ToString("HH:mm", ru);
            return Since.ToString("ddd HH:mm", ru);
        }

        internal static string Shorten(string text, int max)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return "(без темы)";
            return text.Length <= max ? text : text.Substring(0, max - 1).TrimEnd() + "…";
        }

        internal static string Plural(int n, string one, string few, string many)
        {
            int n10 = n % 10, n100 = n % 100;
            if (n10 == 1 && n100 != 11) return one;
            if (n10 >= 2 && n10 <= 4 && (n100 < 12 || n100 > 14)) return few;
            return many;
        }
    }

    /// <summary>
    /// Настройки сводки — из %USERPROFILE%\TNovClient\TNovConfig.json (как у вкладки «Чек-листы»):
    /// "MailDigest": false — выключить; "MailDigestTime": "10:00"; "MailDigestWeekends": true;
    /// "MailEwsUrl" — адрес EWS, если он не на хосте вкладки «Почта».
    /// </summary>
    internal sealed class MailDigestSettings
    {
        public bool Enabled { get; private init; } = true;
        public TimeSpan Time { get; private init; } = new(10, 0, 0);
        public bool IncludeWeekends { get; private init; }
        public Uri EwsUrl { get; private init; } = new(new Uri(MailControl.Url), "/EWS/Exchange.asmx");

        public static MailDigestSettings Load()
        {
            JObject? config = ChecklistsControl.ReadConfig();
            var defaults = new MailDigestSettings();
            if (config == null)
                return defaults;

            string? time = config["MailDigestTime"]?.ToString();
            string? url = config["MailEwsUrl"]?.ToString();
            return new MailDigestSettings
            {
                Enabled = config["MailDigest"]?.Type != JTokenType.Boolean || (bool)config["MailDigest"]!,
                Time = TimeSpan.TryParse(time, CultureInfo.InvariantCulture, out var t) && t < TimeSpan.FromDays(1)
                    ? t : defaults.Time,
                IncludeWeekends = config["MailDigestWeekends"]?.Type == JTokenType.Boolean && (bool)config["MailDigestWeekends"]!,
                EwsUrl = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u : defaults.EwsUrl
            };
        }
    }

    /// <summary>
    /// Ежедневная сводка по почте в заданное время (по умолчанию 10:00, по будним дням).
    /// Период — с конца прошлой сводки (в понедельник захватит выходные), не больше <see cref="MaxPeriod"/>.
    /// Если в это время приложение не было запущено или компьютер спал — сводка придёт при следующей
    /// проверке в тот же день. Нет входа в Exchange (401) — молча ждём и пробуем позже.
    /// </summary>
    internal sealed class MailDigestScheduler
    {
        private static readonly TimeSpan MaxPeriod = TimeSpan.FromDays(4);
        private static readonly TimeSpan RetryAfterAuthError = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan RetryAfterError = TimeSpan.FromMinutes(10);

        private static readonly string StateFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TNovDesktop", "mail-digest.json");

        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
        private DateTime _nextAttempt = DateTime.MinValue;
        private bool _busy;

        /// <summary>Плановая сводка готова (письма есть) — показать уведомление.</summary>
        public event Action<MailDigest>? DigestReady;

        public void Start()
        {
            _timer.Tick += async (s, e) => await TickAsync();
            _timer.Start();
        }

        private async Task TickAsync()
        {
            DateTime now = DateTime.Now;
            if (_busy || now < _nextAttempt)
                return;

            MailDigestSettings settings = MailDigestSettings.Load();
            if (!settings.Enabled || now.TimeOfDay < settings.Time)
                return;
            if (!settings.IncludeWeekends && now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                return;

            State state = State.Load();
            if (state.LastShownDate == now.Date)
                return;

            _busy = true;
            try
            {
                DateTime since = ClampSince(state.LastUntil, now);
                MailDigest digest = await BuildAsync(settings, since, now);
                new State { LastShownDate = now.Date, LastSince = since, LastUntil = now }.Save();
                Log.Write($"[MAIL] Сводка: {digest.Total} писем, непрочитанных {digest.Unread}, с {since:g}");

                if (digest.Total > 0)
                    DigestReady?.Invoke(digest);
            }
            catch (MailAuthException ex)
            {
                Log.Write($"[MAIL] {ex.Message} — сводка отложена");
                _nextAttempt = now + RetryAfterAuthError;
            }
            catch (Exception ex)
            {
                Log.Write($"[MAIL] Сводка не собрана: {ex.Message}");
                _nextAttempt = now + RetryAfterError;
            }
            finally
            {
                _busy = false;
            }
        }

        /// <summary>
        /// Сводка по запросу (меню трея): тот же период, что у сегодняшней плановой сводки,
        /// но с актуальным состоянием писем; ещё не было — с конца прошлой. Состояние не меняет.
        /// </summary>
        public Task<MailDigest> BuildNowAsync()
        {
            DateTime now = DateTime.Now;
            State state = State.Load();
            DateTime since = state.LastShownDate == now.Date && state.LastSince.HasValue
                ? state.LastSince.Value
                : ClampSince(state.LastUntil, now);
            return BuildAsync(MailDigestSettings.Load(), since, now);
        }

        private static async Task<MailDigest> BuildAsync(MailDigestSettings settings, DateTime since, DateTime until)
        {
            var items = await new EwsInboxReader(settings.EwsUrl).GetInboxSinceAsync(since);
            return new MailDigest(since, until, items);
        }

        /// <summary>С конца прошлой сводки, но не дальше MaxPeriod; первой сводке — последние сутки.</summary>
        private static DateTime ClampSince(DateTime? lastUntil, DateTime now)
        {
            if (lastUntil == null || lastUntil > now)
                return now.AddDays(-1);
            return lastUntil.Value < now - MaxPeriod ? now - MaxPeriod : lastUntil.Value;
        }

        private sealed class State
        {
            public DateTime? LastShownDate { get; set; }
            public DateTime? LastSince { get; set; }
            public DateTime? LastUntil { get; set; }

            public static State Load()
            {
                try
                {
                    if (File.Exists(StateFile))
                        return JsonConvert.DeserializeObject<State>(File.ReadAllText(StateFile)) ?? new State();
                }
                catch { }
                return new State();
            }

            public void Save()
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
                    File.WriteAllText(StateFile, JsonConvert.SerializeObject(this, Formatting.Indented));
                }
                catch (Exception ex)
                {
                    Log.Write($"[MAIL] Не удалось сохранить {StateFile}: {ex.Message}");
                }
            }
        }
    }
}
