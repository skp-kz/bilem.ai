using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent.Core.Cloud
{
    /// <summary>Ошибка облака по протоколу: код из таблицы (code_invalid, code_expired…) и текст для человека.</summary>
    public sealed class CloudException : AgentException
    {
        public string Code { get; }
        public int Http { get; }
        public CloudException(string code, int http, string message, string hint = null) : base(message, hint) { Code = code; Http = http; Short = code; }
    }

    public sealed class ConnectResult
    {
        public string BaseId, OrgId, OrgName, OrgUrl, Login, AgentKey;
    }

    /// <summary>
    /// HTTP-часть протокола канала (stand/cloud-channel-protocol): подключение базы кодом из кабинета (решение 0017), отключение базы.
    /// Только https; http — только к 127.0.0.1 (заглушка облака на стенде и в тестах). Прокси — системный.
    /// </summary>
    public static class CloudApi
    {
        public const string ApiPath = "/agent/v1";

        /// <summary>Адрес облака допустим: https куда угодно, http — только на этот компьютер.</summary>
        public static Uri CheckUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) throw new AgentException(Txt.F("CloudBadUrl", url));
            if (u.Scheme == Uri.UriSchemeHttps) return u;
            if (u.Scheme == Uri.UriSchemeHttp && (u.Host == "127.0.0.1" || u.Host == "localhost" || u.Host == "[::1]")) return u;
            throw new AgentException(Txt.F("CloudBadUrl", url));
        }

        /// <summary>Адрес канала: https → wss, http (только 127.0.0.1) → ws.</summary>
        public static Uri ChannelUri(string baseUrl)
        {
            var u = CheckUrl(baseUrl);
            var b = new UriBuilder(u) { Scheme = u.Scheme == Uri.UriSchemeHttps ? "wss" : "ws", Path = u.AbsolutePath.TrimEnd('/') + ApiPath + "/channel" };
            if (b.Port == 443 && b.Scheme == "wss") b.Port = -1;
            return b.Uri;
        }

        /// <summary>
        /// Подключение базы кодом из кабинета (решение 0017, stand #77): код + сведения о базе → привязка к кабинету и ключ компьютера,
        /// если его ещё не было. Организации — наименование и БИН/ИИН (решение Славы 20.10). Дефисы, пробелы и регистр кода не важны.
        /// </summary>
        public static ConnectResult Connect(AgentConfig cfg, CloudStore store, BaseConfig b, JArray organizations, string code)
        {
            var baseId = store.BaseIds.TryGetValue(b.Slug, out var id) ? id : null;
            var body = new JObject
            {
                ["code"] = NormalizeCode(code),
                ["agent_id"] = store.AgentId,
                ["agent_version"] = Mcp.McpHost.Version,
                ["machine"] = Environment.MachineName,
                ["base"] = new JObject
                {
                    ["base_id"] = baseId,
                    ["slug"] = b.Slug,
                    ["configuration"] = new JObject { ["name"] = b.DisplayName, ["version"] = b.Version },
                    ["organizations"] = organizations ?? new JArray(),
                },
            };
            var r = Send(cfg, "POST", ApiPath + "/connect", body, store.Key());
            return new ConnectResult
            {
                BaseId = (string)r["base_id"],
                OrgId = (string)r["org"]?["id"] ?? (string)r["org"]?["slug"],
                OrgName = (string)r["org"]?["name"],
                OrgUrl = (string)r["org"]?["mcp_url"],
                Login = (string)r["login"],
                AgentKey = (string)r["agent_key"],
            };
        }

        /// <summary>5. Отключить базу в агенте.</summary>
        public static void Disconnect(AgentConfig cfg, CloudStore store, string baseId) =>
            Send(cfg, "DELETE", ApiPath + "/bases/" + Uri.EscapeDataString(baseId), null, store.Key());

        public static string NormalizeCode(string code) =>
            new string((code ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

        private static JObject Send(AgentConfig cfg, string method, string path, JObject body, string key)
        {
            var baseUri = CheckUrl(cfg.Cloud?.EffectiveUrl ?? CloudSettings.DefaultUrl);
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(new Uri(baseUri, baseUri.AbsolutePath.TrimEnd('/') + path));
            req.Method = method;
            req.Timeout = 30000;
            req.ReadWriteTimeout = 30000;
            req.Accept = "application/json";
            req.UserAgent = "bilem-agent/" + Mcp.McpHost.Version;
            req.Proxy = baseUri.IsLoopback ? null : SystemProxy();
            if (!string.IsNullOrEmpty(key)) req.Headers[HttpRequestHeader.Authorization] = "Bearer " + key;
            if (body != null)
            {
                var bytes = new UTF8Encoding(false).GetBytes(body.ToString(Formatting.None));
                req.ContentType = "application/json; charset=utf-8";
                req.ContentLength = bytes.Length;
                using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
            }
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                    return ReadJson(resp) ?? new JObject();
            }
            catch (WebException e) when (e.Response is HttpWebResponse er)
            {
                using (er)
                {
                    var j = ReadJson(er);
                    var code = (string)j?["error"] ?? ("http_" + (int)er.StatusCode);
                    throw new CloudException(code, (int)er.StatusCode, Message(code, (string)j?["message"]));
                }
            }
            catch (WebException e)
            {
                throw new CloudException("no_connection", 0, Txt.F("CloudNoConnection", baseUri.Host, e.Message), Txt.S("HintCloudNetwork"));
            }
        }

        /// <summary>Текст для человека — таблица ошибок протокола; неизвестный код — текст облака.</summary>
        internal static string Message(string code, string cloudText)
        {
            switch (code)
            {
                case "code_invalid": return Txt.S("CloudCodeInvalid");
                case "code_expired": return Txt.S("CloudCodeExpired");
                case "code_used": return Txt.S("CloudCodeUsed");
                case "too_many_attempts": return Txt.S("CloudTooManyAttempts");
                // Лимит и конфликт облако объясняет точнее (тариф, какой компьютер) — его текст, наш — если пусто
                case "limit": return string.IsNullOrWhiteSpace(cloudText) ? Txt.S("CloudLimit") : cloudText;
                case "base_conflict": return string.IsNullOrWhiteSpace(cloudText) ? Txt.S("CloudBaseConflict") : cloudText;
                case "key_revoked": return Txt.S("CloudKeyRevoked");
                default: return string.IsNullOrWhiteSpace(cloudText) ? Txt.F("CloudError", code) : cloudText;
            }
        }

        internal static IWebProxy SystemProxy()
        {
            try { var p = WebRequest.GetSystemWebProxy(); p.Credentials = CredentialCache.DefaultCredentials; return p; }
            catch { return null; }
        }

        private static JObject ReadJson(HttpWebResponse r)
        {
            try
            {
                using (var s = r.GetResponseStream())
                using (var sr = new StreamReader(s, Encoding.UTF8))
                {
                    var t = sr.ReadToEnd();
                    return string.IsNullOrWhiteSpace(t) ? null : JObject.Parse(t);
                }
            }
            catch { return null; }
        }
    }
}
