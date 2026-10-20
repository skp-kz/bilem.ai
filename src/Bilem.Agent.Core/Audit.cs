using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Bilem.Agent.Core.Mcp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent.Core
{
    /// <summary>
    /// Журнал аудита печати и почты: C:\ProgramData\Bilem\logs\audit-ГГГГ-ММ.jsonl — строка JSON на событие, только дописывание,
    /// доступ — Администраторы и SYSTEM. Кто запросил (программа, адрес, Tailscale), документ, путь к PDF и SHA-256, для письма — адреса и итог SMTP.
    /// Без содержимого: ни ИИН, ни удостоверения, ни сумм по строкам — только ссылка на файл и хеш.
    /// </summary>
    public static class Audit
    {
        public const string Print = "print", Send = "send", SendFailed = "send_failed", Update = "update";
        private static readonly object Sync = new object();

        public static string FileFor(DateTime t) => Path.Combine(Paths.LogsDir, $"audit-{t:yyyy-MM}.jsonl");

        public static string Sha256(byte[] data)
        {
            if (data == null) return null;
            using (var h = SHA256.Create())
            {
                var b = h.ComputeHash(data);
                var sb = new StringBuilder(b.Length * 2);
                foreach (var x in b) sb.Append(x.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>Записать событие (и строку в «Журнал»). Сбой записи аудита не роняет инструмент — предупреждение в журнал.</summary>
        public static JObject Write(string action, string baseSlug, string kind, string title, string pdfPath, byte[] pdf,
            string to = null, string bcc = null, string smtp = null, McpCaller caller = null)
        {
            caller = caller ?? McpCaller.Current;
            var now = DateTime.Now;
            var o = new JObject
            {
                ["time"] = now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
                ["base"] = baseSlug,
                ["action"] = action,
                ["document"] = new JObject { ["kind"] = kind, ["title"] = title },
            };
            if (pdfPath != null || pdf != null) o["pdf"] = new JObject { ["path"] = pdfPath, ["sha256"] = Sha256(pdf) };
            var who = new JObject { ["client"] = caller?.Client, ["remote"] = caller?.Remote };
            if (!string.IsNullOrEmpty(caller?.TailscaleLogin)) who["tailscale_login"] = caller.TailscaleLogin;
            if (!string.IsNullOrEmpty(caller?.TailscaleName)) who["tailscale_name"] = caller.TailscaleName;
            // Вызов из Bilem Cloud: канал (whatsapp, telegram, chatgpt…) и кто спросил — как прислало облако (часть L)
            if (!string.IsNullOrEmpty(caller?.CloudChannel)) { who["cloud_channel"] = caller.CloudChannel; who["cloud_user"] = caller.CloudUser; }
            if (caller == null) who["client"] = "bilem-agent";
            o["requested_by"] = who;
            if (to != null) o["mail"] = new JObject { ["to"] = to, ["bcc"] = bcc, ["smtp"] = smtp };

            lock (Sync)
            {
                try
                {
                    Directory.CreateDirectory(Paths.LogsDir);
                    var f = FileFor(now);
                    var created = !File.Exists(f);
                    using (var s = new FileStream(f, FileMode.Append, FileAccess.Write, FileShare.Read))
                    {
                        var b = new UTF8Encoding(false).GetBytes(o.ToString(Formatting.None) + "\n");
                        s.Write(b, 0, b.Length);
                    }
                    if (created) { try { Secrets.RestrictToAdmins(f); } catch (Exception e) { Log.Warn(Txt.F("AuditRestrictFailed", e.Message)); } }
                }
                catch (Exception e) { Log.Warn(Txt.F("AuditWriteFailed", e.Message)); }
            }
            var by = caller?.ToString() ?? "Bilem Agent";
            var line = action == Update ? Txt.F("AuditLineUpdate", title, by)
                : to == null ? Txt.F("AuditLinePrint", title, by)
                : action == Send ? Txt.F("AuditLineSend", title, to, by)
                : Txt.F("AuditLineSendFailed", title, to, by, smtp);
            if (action == SendFailed) Log.Fail(line); else Log.Ok(line);
            return o;
        }
    }
}
