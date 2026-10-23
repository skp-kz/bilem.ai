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

    /// <summary>Заявка на подключение: `Code` показываем человеку (его вставляют в кабинете), `RequestId` — для опроса.</summary>
    public sealed class ConnectRequest
    {
        public string RequestId, Code;
        public DateTime ExpiresAt;
        public int PollInterval = 3;
    }

    /// <summary>Ответ опроса заявки: pending / waiting_owner / approved / declined / expired / failed.</summary>
    public sealed class ConnectStatus
    {
        public string Status, Error, Message;
        public DateTime? ExpiresAt;
        public string BaseId, OrgId, OrgName, OrgUrl, Login, AgentKey;
        public bool KeyDelivered;
        /// <summary>Заявка закончилась (подключено, отклонено, истекло, ошибка) — опрос прекратить.</summary>
        public bool Final => Status == "approved" || Status == "declined" || Status == "expired" || Status == "failed";
    }

    /// <summary>
    /// HTTP-часть протокола канала (stand/cloud-channel-protocol): заявка на подключение с кодом и опрос до «Разрешить» владельца
    /// (решение 0017, вторая редакция), отключение базы.
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
        /// 1. Заявка (решение 0017, вторая редакция; формат v1.1 — stand #77): сведения о базе → `request_id` и код подключения,
        /// его вставляют в кабинете. Организации — наименование и БИН/ИИН (решение Славы 20.10).
        /// </summary>
        public static ConnectRequest CreateRequest(AgentConfig cfg, CloudStore store, BaseConfig b, JArray organizations)
        {
            var baseId = store.BaseIds.TryGetValue(b.Slug, out var id) ? id : null;
            var body = new JObject
            {
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
            var r = Send(cfg, "POST", ApiPath + "/connect-requests", body, store.Key());
            // Без `code` — облако до v1.1: показывать нечего (раньше было окно с прочерком)
            if (string.IsNullOrEmpty((string)r["code"])) throw new CloudException("old_server", 200, Txt.S("CloudOldServer"));
            return new ConnectRequest
            {
                RequestId = (string)r["request_id"],
                Code = (string)r["code"],
                ExpiresAt = (DateTime?)r["expires_at"] ?? DateTime.UtcNow.AddMinutes(10),
                PollInterval = Math.Max(1, Math.Min(30, (int?)r["poll_interval"] ?? 3)),
            };
        }

        /// <summary>2. Опрос заявки: всегда 200 + `status`; в первом `approved` — ключ компьютера, если его не было (разбор — как у прежнего exchange).</summary>
        public static ConnectStatus Poll(AgentConfig cfg, CloudStore store, string requestId)
        {
            var r = Send(cfg, "GET", ApiPath + "/connect-requests/" + Uri.EscapeDataString(requestId), null, store.Key());
            var org = r["org"] as JObject;
            return new ConnectStatus
            {
                Status = (string)r["status"] ?? "pending",
                Error = (string)r["error"],
                Message = (string)r["message"],
                ExpiresAt = (DateTime?)r["expires_at"],
                BaseId = (string)r["base_id"],
                OrgId = (string)org?["id"] ?? (string)org?["slug"],
                OrgName = (string)org?["name"],
                OrgUrl = (string)org?["mcp_url"],
                Login = (string)r["login"],
                AgentKey = (string)r["agent_key"],
                KeyDelivered = (bool?)r["key_delivered"] == true,
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
                    throw new CloudException(code, (int)er.StatusCode, Message(code, (string)j?["message"], (int)er.StatusCode));
                }
            }
            catch (WebException e)
            {
                throw new CloudException("no_connection", 0, Txt.F("CloudNoConnection", baseUri.Host, e.Message), Txt.S("HintCloudNetwork"));
            }
        }

        /// <summary>Текст для человека — таблица ошибок протокола; неизвестный код — текст облака.</summary>
        internal static string Message(string code, string cloudText, int http = 200)
        {
            switch (code)
            {
                case "too_many_requests": return Txt.S("CloudTooManyRequests");
                case "request_not_found": return Txt.S("CloudRequestNotFound");
                // Лимит, конфликт, отмену облако объясняет точнее (тариф, какой компьютер) — его текст, наш — если пусто
                case "limit": return string.IsNullOrWhiteSpace(cloudText) ? Txt.S("CloudLimit") : cloudText;
                case "base_conflict": return string.IsNullOrWhiteSpace(cloudText) ? Txt.S("CloudBaseConflict") : cloudText;
                case "cancelled": return string.IsNullOrWhiteSpace(cloudText) ? Txt.S("CloudCancelled") : cloudText;
                case "key_revoked": return Txt.S("CloudKeyRevoked");
                // Адреса нет — на сервере облака старая версия протокола (до v1.1); его «нет такого адреса» человеку ничего не говорит
                case "not_found": case "http_404": return Txt.S("CloudOldServer");
                default:
                    if (http >= 500) return Txt.F("CloudDown", http);
                    return string.IsNullOrWhiteSpace(cloudText) ? Txt.F("CloudError", code) : cloudText;
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
