using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace Bilem.Agent.Core.Mail
{
    /// <summary>Итог отправки: кому, скрытая копия, время.</summary>
    public sealed class SentMail
    {
        public string To { get; set; }
        public string Bcc { get; set; }
        public DateTime At { get; set; }
    }

    /// <summary>
    /// Отправка документов (решение 0014, исправление Славы 18.10 вечер — без окна подтверждения): письмо уходит сразу;
    /// счёт и акт сверки — на любой e-mail, доверенность — только на «Список адресов для отправки доверенностей»;
    /// лимиты в час/день; тумблер «SMTP-клиент включён»; журнал и аудит — без содержимого письма.
    /// </summary>
    public static class MailService
    {
        private static readonly object Gate = new object();

        public static readonly Regex EmailRx = new Regex(@"^[^@\s<>,;""]+@[^@\s<>,;""]+\.[^@\s<>,;""]+$");

        public static string Mask(string email)
        {
            if (string.IsNullOrEmpty(email)) return "";
            var at = email.IndexOf('@');
            return at <= 0 ? "***" : email.Substring(0, 1) + "***" + email.Substring(at);
        }

        /// <summary>
        /// Адресат (исправление Славы 18.10): счёт и акт сверки — на любой e-mail (ответственность — на том, кто попросил:
        /// журнал, скрытая копия, лимиты); доверенность — только на «Список адресов для отправки доверенностей».
        /// </summary>
        public static string CheckRecipient(string kind, string to, IEnumerable<string> own)
        {
            var t = (to ?? "").Trim();
            if (!EmailRx.IsMatch(t)) throw new AgentException(Txt.F("MailBadAddress", t));
            if (kind != DocPowerOfAttorney) return t;
            if ((own ?? Enumerable.Empty<string>()).Any(a => string.Equals(a?.Trim(), t, StringComparison.OrdinalIgnoreCase))) return t;
            throw new AgentException(Txt.S("MailPoaOnlyOwn"));
        }

        public const string DocInvoice = "invoice", DocReconciliation = "reconciliation", DocPowerOfAttorney = "power_of_attorney";

        // ---------- лимиты: только время отправки, без адресов; ящик один на все базы ----------

        private static string SentLog => Path.Combine(Paths.Home, "mail-sent.json");

        private static List<DateTime> SentTimes()
        {
            try { return File.Exists(SentLog) ? JsonConvert.DeserializeObject<List<DateTime>>(File.ReadAllText(SentLog)) ?? new List<DateTime>() : new List<DateTime>(); }
            catch { return new List<DateTime>(); }
        }

        private static void RecordSent(DateTime at)
        {
            lock (Gate)
            {
                var l = SentTimes();
                l.Add(at);
                l.RemoveAll(x => at - x > TimeSpan.FromDays(2));
                Paths.Ensure();
                File.WriteAllText(SentLog, JsonConvert.SerializeObject(l));
            }
        }

        public static void CheckLimits(SmtpAccount acc, DateTime now, IEnumerable<DateTime> sent = null)
        {
            var times = (sent ?? SentTimes()).ToList();
            if (acc.LimitHour > 0 && times.Count(t => now - t < TimeSpan.FromHours(1)) >= acc.LimitHour) throw new AgentException(Txt.F("MailLimitHour", acc.LimitHour));
            if (acc.LimitDay > 0 && times.Count(t => now - t < TimeSpan.FromDays(1)) >= acc.LimitDay) throw new AgentException(Txt.F("MailLimitDay", acc.LimitDay));
        }

        public static string Fill(string template, IDictionary<string, string> v)
        {
            var s = template ?? "";
            foreach (var kv in v) s = s.Replace("{" + kv.Key + "}", kv.Value ?? "");
            s = Regex.Replace(s, @"\n{3,}", "\n\n");
            return s.Trim();
        }

        /// <summary>Общие проверки перед письмом: тумблер, ящик настроен.</summary>
        public static SmtpAccount Ready(AgentConfig cfg)
        {
            var acc = cfg.Smtp;
            if (acc == null || !acc.Enabled) throw new AgentException(Txt.S("MailIsOff"));
            if (!acc.Configured) throw new AgentException(Txt.S("MailNotConfigured"));
            return acc;
        }

        /// <summary>
        /// Отправить сразу: тумблер, ящик, лимиты → SMTP. Ошибка SMTP — AgentException с её текстом (в журнал — тоже).
        /// Один адресат + скрытая копия из вкладки SMTP; вложение — PDF агента.
        /// </summary>
        public static SentMail Send(BaseConfig b, AgentConfig cfg, string docTitle, string to, string subject, string body, string fileName, byte[] pdf)
        {
            var acc = Ready(cfg);
            lock (Gate)
            {
                CheckLimits(acc, DateTime.Now);
                var bcc = string.IsNullOrWhiteSpace(acc.Bcc) ? null : acc.Bcc.Trim();
                try
                {
                    acc.Sender().Send(new MailMessageData
                    {
                        FromName = b.Mail?.FromName, From = acc.FromEffective, To = to, Bcc = bcc,
                        Subject = subject, Body = body, AttachmentName = fileName, Attachment = pdf,
                    });
                }
                catch (Exception e)
                {
                    Log.Fail(Txt.F("MailFailedLog", docTitle, to, e.Message));
                    throw new AgentException(Txt.F("MailSmtpFailed", e.Message));
                }
                var at = DateTime.Now;
                RecordSent(at);
                return new SentMail { To = to, Bcc = bcc, At = at };
            }
        }

        /// <summary>«Отправить тестовое письмо» — на указанный адрес (любой: проверка настроек ящика, не документ).</summary>
        public static void Test(SmtpAccount acc, string to, string fromName = null)
        {
            var t = (to ?? "").Trim();
            if (!EmailRx.IsMatch(t)) throw new AgentException(Txt.F("MailBadAddress", t));
            acc.Sender().Send(new MailMessageData
            {
                FromName = fromName, From = acc.FromEffective, To = t,
                Subject = Txt.S("MailTestSubject"), Body = Txt.S("MailTestBody"),
            });
            Log.Ok(Txt.F("MailTestOk", Mask(t)));
        }
    }
}
