using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent.Core.Print
{
    /// <summary>Общие мелочи чтения 1С для печатных форм (только чтение).</summary>
    internal static class Od
    {
        public const string EmptyKey = "00000000-0000-0000-0000-000000000000";

        public static bool IsEmpty(string key) => string.IsNullOrEmpty(key) || key == EmptyKey;
        public static string NotEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        public static decimal Dec(JToken t) => t == null || t.Type == JTokenType.Null ? 0m : Convert.ToDecimal((double)t, CultureInfo.InvariantCulture);
        public static string D(DateTime d) => d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        public static bool HasDate(JToken t) => t != null && t.Type == JTokenType.Date && ((DateTime)t).Year > 1;

        /// <summary>
        /// Один объект по ключу. check — проверка «Доступа» (объект включён для ИИ); служебные объекты (данные физлиц для доверенности)
        /// читаются без неё: наружу MCP их не отдаёт.
        /// </summary>
        public static JObject One(ODataClient od, string set, string key, string select, bool check = true)
        {
            if (IsEmpty(key)) return null;
            if (check) od.Check(set);
            var r = od.Send("GET", $"{od.BaseUrl}/{set}(guid'{key}')?$format=json" + (select == null ? "" : "&$select=" + Uri.EscapeDataString(select)));
            if (r.Code == 404) return null;
            if (r.Code != 200) throw new AgentException(Txt.F("ODataHttpError", r.Code, ODataClient.ErrorText(r.Body)));
            return JObject.Parse(r.Body);
        }

        /// <summary>Строки набора без проверки «Доступа» (служебные объекты); нет набора в составе — пусто.</summary>
        public static JArray Service(ODataClient od, string set, IDictionary<string, string> q)
        {
            var url = $"{od.BaseUrl}/{set}?$format=json";
            foreach (var kv in q) url += "&" + kv.Key + "=" + Uri.EscapeDataString(kv.Value);
            var r = od.Send("GET", url);
            if (r.Code != 200) return new JArray();
            return (JArray)JObject.Parse(r.Body)["value"] ?? new JArray();
        }

        /// <summary>Кэш «ключ → наименование» на время одной печати.</summary>
        public sealed class Names
        {
            private readonly ODataClient _od; private readonly Dictionary<string, string> _c = new Dictionary<string, string>();
            public Names(ODataClient od) { _od = od; }
            public string Of(string set, string key, string field = "Description", bool check = true)
            {
                if (IsEmpty(key)) return null;
                var k = set + "|" + key + "|" + field;
                if (!_c.TryGetValue(k, out var v)) _c[k] = v = NotEmpty((string)One(_od, set, key, field, check)?[field]);
                return v;
            }
        }
    }
}
