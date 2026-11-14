using System;
using System.Collections.Generic;
using System.Linq;
using Bilem.Agent.Core.Print;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent.Core.Mcp
{
    /// <summary>
    /// Таможенные декларации на импорт (#91): «ГТД по импорту» (Документ.ГТДИмпорт БП КЗ) — по какую дату отражены в 1С, сколько и на
    /// какую сумму за период, непроведённые и помеченные на удаление. Пошлина и НДС — из табличной части «Разделы». Только чтение.
    /// </summary>
    public static class Customs
    {
        public const string Set = "Document_ГТДИмпорт";
        public const string FullName = "Документ.ГТДИмпорт";
        private const string Select = "Ref_Key,Number,Date,Posted,DeletionMark,НомерГТД,Контрагент_Key,СуммаДокумента,ТаможенныйСбор,Разделы";

        public sealed class Decl
        {
            public string Key, Number, DeclarationNo, Counterparty;
            public DateTime Date;
            public bool Posted, Deleted;
            public decimal Total, Duty, Vat, Fee;
        }

        internal static Decl Parse(JObject r)
        {
            var d = new Decl
            {
                Key = (string)r["Ref_Key"], Number = InvoiceSource.PrintNumber((string)r["Number"]), Date = Od.HasDate(r["Date"]) ? (DateTime)r["Date"] : DateTime.MinValue,
                Posted = (bool?)r["Posted"] == true, Deleted = (bool?)r["DeletionMark"] == true,
                DeclarationNo = Od.NotEmpty((string)r["НомерГТД"]), Counterparty = Od.IsEmpty((string)r["Контрагент_Key"]) ? null : (string)r["Контрагент_Key"],
                Total = Od.Dec(r["СуммаДокумента"]), Fee = Od.Dec(r["ТаможенныйСбор"]),
            };
            if (r["Разделы"] is JArray sections)
                foreach (var s in sections) { d.Duty += Od.Dec(s["СуммаПошлины"]) + Od.Dec(s["СуммаПошлиныСпец"]); d.Vat += Od.Dec(s["СуммаНДС"]); }
            return d;
        }

        /// <summary>$select с табличной частью «Разделы» — не все версии платформы его принимают: при 400 — без $select.</summary>
        private static JArray Rows(ODataClient od, Dictionary<string, string> q)
        {
            try { return od.Rows(Set, q); }
            catch (AgentException e) when (e.Message.Contains("400") && q.ContainsKey("$select"))
            {
                var q2 = new Dictionary<string, string>(q);
                q2.Remove("$select");
                return od.Rows(Set, q2);
            }
        }

        /// <summary>Нет «ГТД по импорту» в составе OData или он выключен на «Доступе» — понятный ответ, не ошибка.</summary>
        private static JObject Unavailable(BaseRuntime b, string say) => new JObject
        {
            ["base"] = b.Config.Slug, ["document"] = "ГТДИмпорт", ["available"] = false, ["say"] = say,
        };

        /// <summary>Каталог «Доступа» загружен из самой конфигурации, и документа в нём нет — импорт в базе не ведут.</summary>
        private static bool NotInConfig(BaseConfig c) => c.Catalog != null && c.Catalog.KnownAbsent(FullName);

        /// <summary>
        /// Почему инструмент документ не видит (#104): нет в конфигурации; базе чтение по умолчанию ещё не предлагали (состав OData
        /// старый — нужно «Обновить права Bilem»); или было включено, а человек выключил на «Доступе».
        /// </summary>
        public static string WhyOff(BaseConfig c) =>
            NotInConfig(c) ? Txt.F("CustomsMissing", c.Slug)
            : (c.ReadDefaults ?? new List<string>()).Contains(FullName) ? Txt.F("CustomsOff", c.Slug)
            : Txt.F("CustomsRefresh", c.Slug);

        public static JObject Load(BaseRuntime b, JObject a, JObject asOf)
        {
            var od = b.OData;
            var today = DateTime.Today;
            var from = Tools.ParseDate(a, "from") ?? new DateTime(today.Year, 1, 1);
            var to = Tools.ParseDate(a, "to") ?? today;
            if (from > to) throw new AgentException(Txt.S("LedgerBadPeriod"));
            var top = Math.Min(200, Math.Max(1, (int?)a["top"] ?? 20));
            if (od.Deny?.Invoke(Set) != null) return Unavailable(b, WhyOff(b.Config));

            JArray last;
            try
            {
                last = Rows(od, new Dictionary<string, string>
                {
                    ["$select"] = Select, ["$filter"] = "Posted eq true and DeletionMark eq false", ["$orderby"] = "Date desc", ["$top"] = "1",
                });
            }
            catch (AgentException e) when (e.Message.Contains("404"))
            {
                // Объекта нет в составе OData базы: либо его нет в конфигурации, либо состав не обновлён после включения на «Доступе»
                return Unavailable(b, NotInConfig(b.Config) ? Txt.F("CustomsMissing", b.Config.Slug) : Txt.F("CustomsRefresh", b.Config.Slug));
            }
            var end = to.Date.AddDays(1).AddSeconds(-1);
            var period = Rows(od, new Dictionary<string, string>
            {
                ["$select"] = Select,
                ["$filter"] = $"Date ge datetime'{Od.D(from.Date)}' and Date le datetime'{Od.D(end)}'",
                ["$orderby"] = "Date desc",
            }).OfType<JObject>().Select(Parse).ToList();
            var lastPosted = last.OfType<JObject>().Select(Parse).FirstOrDefault();
            var posted = period.Where(d => d.Posted && !d.Deleted).ToList();
            var notPosted = period.Where(d => !d.Posted && !d.Deleted).ToList();
            var deleted = period.Count(d => d.Deleted);

            var shown = posted.Take(top).Concat(notPosted.Take(top)).Concat(lastPosted == null ? new Decl[0] : new[] { lastPosted }).ToList();
            var names = Ledger.Describe(b, shown.Where(d => d.Counterparty != null).Select(d => ("Catalog_Контрагенты", d.Counterparty)), new HashSet<string>());
            JObject Item(Decl d)
            {
                var o = new JObject { ["number"] = d.Number, ["date"] = Ledger.D(d.Date) };
                if (d.DeclarationNo != null) o["declaration_no"] = d.DeclarationNo;
                if (d.Counterparty != null && names.TryGetValue("Catalog_Контрагенты|" + d.Counterparty, out var n) && n is JObject cp) o["counterparty"] = cp["name"];
                o["total"] = Tools.Money((double)d.Total);
                o["duty"] = Tools.Money((double)d.Duty);
                o["vat"] = Tools.Money((double)d.Vat);
                if (d.Fee != 0) o["fee"] = Tools.Money((double)d.Fee);
                return o;
            }
            var p = $"{Ledger.DateRu(from)}–{Ledger.DateRu(to)}";
            var say = lastPosted == null
                ? Txt.F("CustomsNone", p)
                : Txt.F("CustomsSay", Ledger.DateRu(lastPosted.Date), lastPosted.DeclarationNo ?? lastPosted.Number, p, posted.Count, Words.Money(posted.Sum(d => d.Total)));
            if (notPosted.Count > 0) say += " " + Txt.F("CustomsNotPostedSay", notPosted.Count);
            var res = new JObject
            {
                ["base"] = b.Config.Slug,
                ["document"] = "ГТДИмпорт",
                ["available"] = true,
                ["period"] = new JObject { ["from"] = Ledger.D(from), ["to"] = Ledger.D(to) },
                ["last_posted"] = lastPosted == null ? JValue.CreateNull() : (JToken)Item(lastPosted),
                ["say"] = say,
                ["posted"] = new JObject
                {
                    ["count"] = posted.Count, ["total"] = Tools.Money((double)posted.Sum(d => d.Total)),
                    ["duty"] = Tools.Money((double)posted.Sum(d => d.Duty)), ["vat"] = Tools.Money((double)posted.Sum(d => d.Vat)),
                    ["list"] = new JArray(posted.Take(top).Select(Item)),
                },
                ["not_posted"] = new JObject { ["count"] = notPosted.Count, ["list"] = new JArray(notPosted.Take(top).Select(Item)) },
                ["marked_for_deletion"] = new JObject { ["count"] = deleted },
                ["currency"] = "KZT",
                ["note"] = Txt.S("CustomsNote"),
                ["as_of"] = asOf,
            };
            if (posted.Count > top) res["posted"]["more"] = Txt.F("ToolTruncated", top, posted.Count);
            return res;
        }
    }
}
