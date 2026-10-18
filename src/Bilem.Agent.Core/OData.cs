using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent.Core
{
    public sealed class HttpReply
    {
        public int Code { get; set; }
        public string Body { get; set; }
        public bool Ok => Code >= 200 && Code < 300;
    }

    /// <summary>Клиент стандартного интерфейса OData 1С (только 127.0.0.1).</summary>
    public sealed class ODataClient
    {
        private readonly string _base;
        private readonly string _auth;

        public ODataClient(string baseUrl, string user, string password)
        {
            _base = baseUrl.TrimEnd('/');
            _auth = user == null ? null : "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));
        }

        public string BaseUrl => _base;

        /// <summary>Слой MCP «Доступа»: набор → текст отказа (объект выключен) или null. Проверяется до запроса к 1С.</summary>
        public Func<string, string> Deny { get; set; }

        /// <summary>Проверка слоя MCP «Доступа» до запроса (для запросов в обход Rows: чтение по ключу).</summary>
        public void Check(string resource)
        {
            if (Deny == null) return;
            var set = resource.Split('/', '(')[0];
            var why = Deny(set);
            if (why != null) throw new AgentException(why, Txt.S("HintAccessOff"));
        }

        public static string Query(IDictionary<string, string> q)
        {
            var all = new Dictionary<string, string>(q ?? new Dictionary<string, string>()) { ["$format"] = "json" };
            return string.Join("&", all.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value)));
        }

        /// <summary>Строковый литерал OData: кавычка удваивается.</summary>
        public static string Lit(string s) => "'" + (s ?? "").Replace("'", "''") + "'";

        public HttpReply Send(string method, string url, string body = null, bool withAuth = true, int timeoutMs = 60000)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.Accept = "application/json";
            req.Proxy = null;
            req.AllowAutoRedirect = false;
            if (withAuth && _auth != null) req.Headers[HttpRequestHeader.Authorization] = _auth;
            if (body != null)
            {
                var bytes = new UTF8Encoding(false).GetBytes(body);
                req.ContentType = "application/json; charset=utf-8";
                req.ContentLength = bytes.Length;
                using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
            }
            HttpWebResponse resp;
            try { resp = (HttpWebResponse)req.GetResponse(); }
            catch (WebException e) when (e.Response is HttpWebResponse r) { resp = r; }
            catch (WebException e) { return new HttpReply { Code = 0, Body = e.Message }; }
            using (resp)
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return new HttpReply { Code = (int)resp.StatusCode, Body = sr.ReadToEnd() };
        }

        public HttpReply Get(string resource, IDictionary<string, string> q = null, bool withAuth = true) =>
            Send("GET", $"{_base}/{resource}?{Query(q)}", null, withAuth);

        /// <summary>GET с разбором value[]; ошибка 1С — исключение с её текстом.</summary>
        public JArray Rows(string resource, IDictionary<string, string> q = null)
        {
            Check(resource);
            var r = Get(resource, q);
            if (r.Code != 200) throw new AgentException(Txt.F("ODataHttpError", r.Code, ErrorText(r.Body)));
            return (JArray)JObject.Parse(r.Body)["value"] ?? new JArray();
        }

        /// <summary>GET с $inlinecount=allpages: строки и общее число (для пометки «показаны первые N из M»).</summary>
        public (JArray Rows, int Total) RowsCounted(string resource, IDictionary<string, string> q)
        {
            Check(resource);
            var all = new Dictionary<string, string>(q ?? new Dictionary<string, string>()) { ["$inlinecount"] = "allpages" };
            var r = Get(resource, all);
            if (r.Code != 200) throw new AgentException(Txt.F("ODataHttpError", r.Code, ErrorText(r.Body)));
            var j = JObject.Parse(r.Body);
            var rows = (JArray)j["value"] ?? new JArray();
            var total = int.TryParse((string)j["odata.count"], out var c) ? c : rows.Count;
            return (rows, total);
        }

        public static string ErrorText(string body)
        {
            if (string.IsNullOrEmpty(body)) return "";
            try
            {
                var m = (string)JObject.Parse(body).SelectToken("['odata.error'].message.value");
                if (!string.IsNullOrEmpty(m)) return m.Replace("\r", " ").Replace("\n", " ").Trim();
            }
            catch { /* не JSON — например, HTML-ошибка IIS */ }
            var t = System.Text.RegularExpressions.Regex.Replace(body, "<[^>]+>", " ");
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ").Trim();
            return t.Length > 200 ? t.Substring(0, 200) : t;
        }

        /// <summary>Имена наборов из $metadata.</summary>
        public static List<string> EntitySets(string metadataXml) =>
            System.Text.RegularExpressions.Regex.Matches(metadataXml ?? "", "EntitySet Name=\"([^\"]+)\"")
                .Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).ToList();

        /// <summary>Имя набора OData по полному имени объекта метаданных: Справочник.X → Catalog_X и т. д.</summary>
        public static string SetName(string fullName)
        {
            var i = fullName.IndexOf('.');
            var kind = fullName.Substring(0, i);
            var name = fullName.Substring(i + 1);
            string prefix;
            switch (kind)
            {
                case "Справочник": prefix = "Catalog"; break;
                case "Документ": prefix = "Document"; break;
                case "РегистрСведений": prefix = "InformationRegister"; break;
                case "РегистрБухгалтерии": prefix = "AccountingRegister"; break;
                case "РегистрНакопления": prefix = "AccumulationRegister"; break;
                case "ПланСчетов": prefix = "ChartOfAccounts"; break;
                case "ПланВидовХарактеристик": prefix = "ChartOfCharacteristicTypes"; break;
                case "Константа": prefix = "Constant"; break;
                case "ЖурналДокументов": prefix = "DocumentJournal"; break;
                default: prefix = kind; break;
            }
            return prefix + "_" + name;
        }
    }
}
