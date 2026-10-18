using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent.Core.Mail
{
    /// <summary>
    /// E-mail контрагента из контактной информации 1С — только для проверки адресата send_document.
    /// Регистр «Контактная информация» — служебный объект состава OData: в дереве «Доступа» его нет, инструменты ИИ его не читают
    /// (там и данные физлиц); фильтр — только этот контрагент и только тип «адрес электронной почты».
    /// </summary>
    public static class ContactEmails
    {
        public const string Register = "РегистрСведений.КонтактнаяИнформация";
        public const string Set = "InformationRegister_КонтактнаяИнформация";

        internal static string Filter(string counterpartyKey) =>
            // «Объект» — составного типа: 1С ждёт cast(guid'…', 'Catalog_…')
            $"Объект eq cast(guid'{counterpartyKey}', 'Catalog_Контрагенты') and Тип eq 'АдресЭлектроннойПочты'";

        public static List<string> Of(BaseConfig b, ODataClient od, string counterpartyKey)
        {
            if (string.IsNullOrEmpty(counterpartyKey) || !(b.ODataObjects ?? new List<string>()).Contains(Register)) return new List<string>();
            var r = od.Send("GET", $"{od.BaseUrl}/{Set}?$format=json&$select=Представление&$filter=" + System.Uri.EscapeDataString(Filter(counterpartyKey)));
            if (r.Code != 200) return new List<string>();
            return ((JArray)JObject.Parse(r.Body)["value"] ?? new JArray())
                .Select(x => ((string)x["Представление"] ?? "").Trim())
                .Where(x => MailService.EmailRx.IsMatch(x))
                .Distinct(System.StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
