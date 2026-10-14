using System.Net;
using System.Net.Http;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace TNovDesktop
{
    /// <summary>Письмо из «Входящих» — только то, что нужно для сводки.</summary>
    internal sealed class MailItemInfo
    {
        public string Id { get; init; } = "";
        public string Subject { get; init; } = "";
        public string FromName { get; init; } = "";
        public string FromAddress { get; init; } = "";
        public DateTime Received { get; init; }
        public bool IsRead { get; init; }
        public bool IsHighImportance { get; init; }
        public bool IsFlagged { get; init; }
        public bool HasAttachments { get; init; }
        public string Preview { get; init; } = "";
    }

    /// <summary>Сервер ответил 401: учётка Windows не подошла (нет входа в домен / Kerberos).</summary>
    internal sealed class MailAuthException : Exception
    {
        public MailAuthException(string message) : base(message) { }
    }

    /// <summary>
    /// Чтение «Входящих» Exchange через EWS (SOAP) под текущей учёткой Windows (Negotiate/Kerberos) —
    /// пароль не спрашиваем и не храним. Запросы — FindItem по дате получения, без тел писем:
    /// для выжимки хватает item:Preview (первые ~255 символов, Exchange 2013+).
    /// </summary>
    internal sealed class EwsInboxReader
    {
        private const string T = "http://schemas.microsoft.com/exchange/services/2006/types";
        private const string M = "http://schemas.microsoft.com/exchange/services/2006/messages";
        private static readonly XNamespace Tns = T;
        private static readonly XNamespace Mns = M;

        private const int PageSize = 200;
        private const int MaxItems = 1000;

        // Negotiate (Kerberos) — в офисе; NTLM — если KDC недоступен (вне сети, почта через прокси):
        // Negotiate тогда отвечает 401 и сам на NTLM не откатывается. Удачный вариант запоминаем.
        private static readonly HttpClient _negotiate = CreateClient(null);
        private static readonly HttpClient _ntlm = CreateClient("NTLM");
        private static bool _preferNtlm;

        private static HttpClient CreateClient(string? scheme) => new(new HttpClientHandler
        {
            UseDefaultCredentials = scheme == null,
            Credentials = scheme == null ? null : new SchemeOnlyCredentials(scheme)
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        /// <summary>Учётка Windows только для указанной схемы — чтобы HttpClient не выбрал Negotiate.</summary>
        private sealed class SchemeOnlyCredentials : ICredentials
        {
            private readonly string _scheme;
            public SchemeOnlyCredentials(string scheme) => _scheme = scheme;
            public NetworkCredential? GetCredential(Uri uri, string authType) =>
                string.Equals(authType, _scheme, StringComparison.OrdinalIgnoreCase)
                    ? CredentialCache.DefaultNetworkCredentials
                    : null;
        }

        private readonly Uri _ewsUrl;

        public EwsInboxReader(Uri ewsUrl)
        {
            _ewsUrl = ewsUrl;
        }

        /// <summary>Письма, полученные не раньше <paramref name="since"/>, новые первыми.</summary>
        public async Task<List<MailItemInfo>> GetInboxSinceAsync(DateTime since, CancellationToken ct = default)
        {
            try
            {
                return await FindAsync(since, extended: true, ct);
            }
            catch (EwsErrorException ex)
            {
                // Exchange 2010 не знает item:Preview / item:Flag в FindItem — повторяем без них.
                Log.Write($"[MAIL] EWS FindItem (2013) error: {ex.Message} — повтор в режиме Exchange2010");
                return await FindAsync(since, extended: false, ct);
            }
        }

        private async Task<List<MailItemInfo>> FindAsync(DateTime since, bool extended, CancellationToken ct)
        {
            var result = new List<MailItemInfo>();
            int offset = 0;
            while (result.Count < MaxItems)
            {
                XDocument response = await PostAsync(BuildFindItem(since, offset, extended), ct);
                XElement message = response.Descendants(Mns + "FindItemResponseMessage").FirstOrDefault()
                    ?? throw new EwsErrorException("В ответе EWS нет FindItemResponseMessage");

                if ((string?)message.Attribute("ResponseClass") != "Success")
                    throw new EwsErrorException(
                        (string?)message.Element(Mns + "ResponseCode") + ": " + (string?)message.Element(Mns + "MessageText"));

                XElement? root = message.Element(Mns + "RootFolder");
                var items = root?.Element(Tns + "Items")?.Elements().ToList() ?? new List<XElement>();
                result.AddRange(items.Select(ParseItem));

                bool last = (string?)root?.Attribute("IncludesLastItemInRange") != "false";
                if (last || items.Count == 0)
                    break;
                offset += items.Count;
            }
            return result;
        }

        private async Task<XDocument> PostAsync(string soap, CancellationToken ct)
        {
            HttpResponseMessage response = await SendAsync(soap, _preferNtlm ? _ntlm : _negotiate, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized && !_preferNtlm)
            {
                response.Dispose();
                response = await SendAsync(soap, _ntlm, ct);
                if (response.StatusCode != HttpStatusCode.Unauthorized)
                {
                    _preferNtlm = true;
                    Log.Write("[MAIL] EWS: Negotiate не прошёл, вход по NTLM");
                }
            }
            using var _ = response;

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new MailAuthException("EWS: 401 — нет входа по учётной записи Windows");

            string body = await response.Content.ReadAsStringAsync(ct);
            XDocument doc;
            try { doc = XDocument.Parse(body); }
            catch
            {
                response.EnsureSuccessStatusCode();
                throw new EwsErrorException($"EWS вернул не XML (HTTP {(int)response.StatusCode})");
            }

            // SOAP Fault (HTTP 500) — например, неизвестное свойство в старой версии Exchange.
            XElement? fault = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault");
            if (fault != null)
                throw new EwsErrorException(fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value
                                            ?? "SOAP Fault");
            return doc;
        }

        private Task<HttpResponseMessage> SendAsync(string soap, HttpClient client, CancellationToken ct)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, _ewsUrl)
            {
                Content = new StringContent(soap, Encoding.UTF8, "text/xml")
            };
            // Явный Host: иначе для SPN .NET сам резолвит имя через DNS, а при почте через прокси
            // локальный DNS его не знает — «Этот хост неизвестен».
            request.Headers.Host = _ewsUrl.IsDefaultPort ? _ewsUrl.Host : _ewsUrl.Authority;
            return client.SendAsync(request, ct);
        }

        private static MailItemInfo ParseItem(XElement item)
        {
            XElement? mailbox = item.Element(Tns + "From")?.Element(Tns + "Mailbox");
            return new MailItemInfo
            {
                Id = (string?)item.Element(Tns + "ItemId")?.Attribute("Id") ?? "",
                Subject = (string?)item.Element(Tns + "Subject") ?? "",
                FromName = (string?)mailbox?.Element(Tns + "Name") ?? "",
                FromAddress = (string?)mailbox?.Element(Tns + "EmailAddress") ?? "",
                Received = DateTime.TryParse((string?)item.Element(Tns + "DateTimeReceived"), null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal, out var utc)
                    ? DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime()
                    : DateTime.MinValue,
                IsRead = (string?)item.Element(Tns + "IsRead") == "true",
                IsHighImportance = (string?)item.Element(Tns + "Importance") == "High",
                IsFlagged = (string?)item.Element(Tns + "Flag")?.Element(Tns + "FlagStatus") == "Flagged",
                HasAttachments = (string?)item.Element(Tns + "HasAttachments") == "true",
                Preview = ((string?)item.Element(Tns + "Preview") ?? "").Trim()
            };
        }

        private static string BuildFindItem(DateTime since, int offset, bool extended)
        {
            string version = extended ? "Exchange2013" : "Exchange2010_SP2";
            string extra = extended
                ? "<t:FieldURI FieldURI=\"item:Preview\"/><t:FieldURI FieldURI=\"item:Flag\"/>"
                : "";
            string sinceUtc = since.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

            return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<soap:Envelope xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"" xmlns:t=""{T}"" xmlns:m=""{M}"">
  <soap:Header><t:RequestServerVersion Version=""{version}""/></soap:Header>
  <soap:Body>
    <m:FindItem Traversal=""Shallow"">
      <m:ItemShape>
        <t:BaseShape>IdOnly</t:BaseShape>
        <t:AdditionalProperties>
          <t:FieldURI FieldURI=""item:Subject""/>
          <t:FieldURI FieldURI=""item:DateTimeReceived""/>
          <t:FieldURI FieldURI=""item:Importance""/>
          <t:FieldURI FieldURI=""item:HasAttachments""/>
          <t:FieldURI FieldURI=""message:From""/>
          <t:FieldURI FieldURI=""message:IsRead""/>
          {extra}
        </t:AdditionalProperties>
      </m:ItemShape>
      <m:IndexedPageItemView MaxEntriesReturned=""{PageSize}"" Offset=""{offset}"" BasePoint=""Beginning""/>
      <m:Restriction>
        <t:IsGreaterThanOrEqualTo>
          <t:FieldURI FieldURI=""item:DateTimeReceived""/>
          <t:FieldURIOrConstant><t:Constant Value=""{SecurityElement.Escape(sinceUtc)}""/></t:FieldURIOrConstant>
        </t:IsGreaterThanOrEqualTo>
      </m:Restriction>
      <m:SortOrder>
        <t:FieldOrder Order=""Descending""><t:FieldURI FieldURI=""item:DateTimeReceived""/></t:FieldOrder>
      </m:SortOrder>
      <m:ParentFolderIds><t:DistinguishedFolderId Id=""inbox""/></m:ParentFolderIds>
    </m:FindItem>
  </soap:Body>
</soap:Envelope>";
        }

        private sealed class EwsErrorException : Exception
        {
            public EwsErrorException(string message) : base(message) { }
        }
    }
}
