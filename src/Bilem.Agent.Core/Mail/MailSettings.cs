using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace Bilem.Agent.Core.Mail
{
    /// <summary>
    /// SMTP-клиент агента — один почтовый ящик на все базы (пароль приложения вводится один раз).
    /// Пароль — только DPAPI для этой машины: в окне — точки, в журнал, логи и MCP не попадает.
    /// </summary>
    public sealed class SmtpAccount
    {
        /// <summary>Тумблер «SMTP-клиент включён» (и в меню трея). Выключен — send_document отказывает.</summary>
        public bool Enabled { get; set; }
        public string Host { get; set; }
        public int Port { get; set; } = 465;
        public SmtpSecurity Security { get; set; } = SmtpSecurity.SslOnConnect;
        public string Login { get; set; }
        public string PasswordProtected { get; set; }
        /// <summary>Адрес отправителя; пусто — логин.</summary>
        public string From { get; set; }
        /// <summary>«Список адресов для отправки доверенностей» (руководитель, бухгалтер): доверенности — только на них. Счета и акты сверки — на любой e-mail.</summary>
        public List<string> Trusted { get; set; } = new List<string>();
        /// <summary>Скрытая копия каждого письма; пусто — без копии.</summary>
        public string Bcc { get; set; }
        public int LimitHour { get; set; } = 10;
        public int LimitDay { get; set; } = 50;
        /// <summary>Последний адрес «Отправить тестовое письмо».</summary>
        public string TestTo { get; set; }

        [JsonIgnore] public string FromEffective => string.IsNullOrWhiteSpace(From) ? Login?.Trim() : From.Trim();
        [JsonIgnore] public bool Configured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromEffective) && !string.IsNullOrEmpty(PasswordProtected);

        public void SetPassword(string plain) => PasswordProtected = string.IsNullOrEmpty(plain) ? null : Secrets.Protect(plain);
        internal string Password() => string.IsNullOrEmpty(PasswordProtected) ? null : Secrets.Unprotect(PasswordProtected);

        public SmtpSender Sender() => new SmtpSender { Host = Host?.Trim(), Port = Port, Security = Security, Login = Login?.Trim(), Password = Password() };

        /// <summary>«a@b.kz, c@d.kz; e@f.kz» → список, только похожие на e-mail, без повторов.</summary>
        public static List<string> ParseList(string text) =>
            (text ?? "").Split(new[] { ',', ';', '\n', '\r', ' ' }, System.StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => MailService.EmailRx.IsMatch(x))
                .GroupBy(x => x.ToLowerInvariant()).Select(g => g.First()).ToList();
    }

    /// <summary>Письмо от базы: имя отправителя, шаблоны РУ/КЗ, подпись — своё у каждой базы (разные юрлица).</summary>
    public sealed class MailSettings
    {
        public string FromName { get; set; }
        /// <summary>ru или kk — язык шаблона письма.</summary>
        public string Language { get; set; } = "ru";
        public string SubjectRu { get; set; } = DefaultSubjectRu;
        public string BodyRu { get; set; } = DefaultBodyRu;
        public string SubjectKk { get; set; } = DefaultSubjectKk;
        public string BodyKk { get; set; } = DefaultBodyKk;
        public string Signature { get; set; }

        public const string DefaultSubjectRu = "Счёт на оплату №{номер} от {дата} — {организация}";
        public const string DefaultBodyRu = "Здравствуйте!\n\nВо вложении счёт на оплату №{номер} от {дата} на сумму {сумма} тенге.\n{приписка}\n\n{подпись}";
        public const string DefaultSubjectKk = "Төлем шоты №{номер}, {дата} — {организация}";
        public const string DefaultBodyKk = "Сәлеметсіз бе!\n\nТіркемеде {дата} күнгі №{номер} төлем шоты, сомасы {сумма} теңге.\n{приписка}\n\n{подпись}";

        // Доверенность и акт сверки — свои тексты (шаблоны вкладки «SMTP» — для счёта); приписка и подпись — те же
        public const string PoaSubjectRu = "Доверенность №{номер} от {дата} — {организация}";
        public const string PoaBodyRu = "Здравствуйте!\n\nВо вложении доверенность №{номер} от {дата}.\n{приписка}\n\n{подпись}";
        public const string PoaSubjectKk = "Сенімхат №{номер}, {дата} — {организация}";
        public const string PoaBodyKk = "Сәлеметсіз бе!\n\nТіркемеде {дата} күнгі №{номер} сенімхат.\n{приписка}\n\n{подпись}";
        public const string ActSubjectRu = "Акт сверки за {период} — {организация}";
        public const string ActBodyRu = "Здравствуйте!\n\nВо вложении акт сверки взаимных расчётов между {организация} и {контрагент} за период {период}. Просим сверить, подписать и вернуть один экземпляр.\n{приписка}\n\n{подпись}";
        public const string ActSubjectKk = "Салыстыру актісі, {период} — {организация}";
        public const string ActBodyKk = "Сәлеметсіз бе!\n\nТіркемеде {организация} мен {контрагент} арасындағы {период} кезеңге өзара есеп айырысуларды салыстыру актісі. Салыстырып, қол қойып, бір данасын қайтаруыңызды сұраймыз.\n{приписка}\n\n{подпись}";

        /// <summary>Тема и текст по виду документа и языку базы.</summary>
        public (string Subject, string Body) Templates(string kind)
        {
            var kk = string.Equals(Language, "kk", System.StringComparison.OrdinalIgnoreCase);
            switch (kind)
            {
                case MailService.DocPowerOfAttorney: return kk ? (PoaSubjectKk, PoaBodyKk) : (PoaSubjectRu, PoaBodyRu);
                case MailService.DocReconciliation: return kk ? (ActSubjectKk, ActBodyKk) : (ActSubjectRu, ActBodyRu);
                default: return kk ? (SubjectKk, BodyKk) : (SubjectRu, BodyRu);
            }
        }
    }

    /// <summary>Подсказки для почтовых сервисов: сервер, порт, где взять пароль приложения.</summary>
    public static class MailPresets
    {
        public static readonly (string Name, string Host, int Port, SmtpSecurity Security, string HintKey)[] All =
        {
            ("Gmail", "smtp.gmail.com", 465, SmtpSecurity.SslOnConnect, "SmtpHintGmail"),
            ("Mail.ru", "smtp.mail.ru", 465, SmtpSecurity.SslOnConnect, "SmtpHintMailru"),
            ("Yandex", "smtp.yandex.ru", 465, SmtpSecurity.SslOnConnect, "SmtpHintYandex"),
            ("Outlook", "smtp.office365.com", 587, SmtpSecurity.StartTls, "SmtpHintOutlook"),
            ("iCloud", "smtp.mail.me.com", 587, SmtpSecurity.StartTls, "SmtpHintIcloud"),
        };
    }
}
