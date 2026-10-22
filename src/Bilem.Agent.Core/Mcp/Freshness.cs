using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent.Core.Mcp
{
    /// <summary>
    /// «Данные на …» (v0.3, часть D): учёт в 1С отстаёт от жизни, поэтому каждый ответ с цифрами называет дату —
    /// последняя проведённая банковская выписка по каждому счёту организации, последний проведённый документ, закрытый месяц.
    /// Только 1С: не «по данным банка», ничего не досчитываем. Один помощник на все инструменты, кэш — минута.
    /// </summary>
    public static class Freshness
    {
        /// <summary>Банковские документы (выписки) — по ним дата последней выписки по счёту.</summary>
        internal static readonly string[] BankSets =
        {
            "Document_ПлатежноеПоручениеВходящее", "Document_ПлатежноеПоручениеИсходящее",
            "Document_ПлатежныйОрдерПоступлениеДенежныхСредств", "Document_ПлатежныйОрдерСписаниеДенежныхСредств",
        };

        /// <summary>Документы, по которым считаем «последний проведённый документ в базе».</summary>
        internal static readonly string[] DocSets = BankSets.Concat(new[]
        {
            "Document_ПриходныйКассовыйОрдер", "Document_РасходныйКассовыйОрдер", "Document_РеализацияТоваровУслуг",
            "Document_ПоступлениеТоваровУслуг", "Document_ЧекККМ", "Document_ОтчетОРозничныхПродажах",
        }).ToArray();

        public sealed class Account { public string Key; public string Org; public string Bank; public string Iik; public DateTime? Last; }

        public sealed class Info
        {
            public List<Account> Accounts = new List<Account>();
            public DateTime? LastDocument;
            public DateTime? ClosedMonth;
        }

        private static readonly Dictionary<string, (DateTime At, Info Value)> Cache = new Dictionary<string, (DateTime, Info)>();

        /// <summary>Блок as_of для ответа инструмента; кэш — минута на базу.</summary>
        public static JObject AsOf(BaseRuntime b)
        {
            Info info;
            lock (Cache)
                if (Cache.TryGetValue(b.Config.Slug, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(1)) return ToJson(c.Value);
            info = Load(b, DateTime.Today);
            lock (Cache) Cache[b.Config.Slug] = (DateTime.UtcNow, info);
            return ToJson(info);
        }

        internal static JObject ToJson(Info i)
        {
            var ci = CultureInfo.InvariantCulture;
            string D(DateTime? d) => d?.ToString("yyyy-MM-dd", ci);
            var o = new JObject
            {
                ["last_document"] = D(i.LastDocument),
                ["bank_statements"] = new JArray(i.Accounts.Select(a => new JObject
                {
                    ["account"] = Label(a), ["organization"] = a.Org, ["last_statement"] = D(a.Last),
                })),
            };
            o["closed_month"] = i.ClosedMonth.HasValue ? (JToken)i.ClosedMonth.Value.ToString("yyyy-MM", ci) : JValue.CreateNull();
            o["say"] = Say(i);
            return o;
        }

        /// <summary>«Kaspi Bank ···7183» — банк и последние 4 знака ИИК.</summary>
        internal static string Label(Account a)
        {
            var tail = string.IsNullOrEmpty(a.Iik) ? "" : " ···" + a.Iik.Substring(Math.Max(0, a.Iik.Length - 4));
            return ((a.Bank ?? "").Trim() + tail).Trim();
        }

        /// <summary>Готовая фраза для модели: «Данные 1С на 15.10.2026 (выписка Kaspi ···7183 — 15.10.2026, Halyk ···0001 — 12.10.2026)».</summary>
        internal static string Say(Info i)
        {
            var ci = CultureInfo.InvariantCulture;
            var parts = i.Accounts.Where(a => a.Last.HasValue).Select(a => $"{Label(a)} — {a.Last.Value.ToString("dd.MM.yyyy", ci)}").ToList();
            var main = i.LastDocument.HasValue ? Txt.F("AsOfSay", i.LastDocument.Value) : Txt.S("AsOfSayUnknown");
            return parts.Count == 0 ? main : main + " " + Txt.F("AsOfSayBanks", string.Join(", ", parts));
        }

        internal static Info Load(BaseRuntime b, DateTime today)
        {
            var od = b.OData;
            var info = new Info();
            var ci = CultureInfo.InvariantCulture;

            // Счета организаций (не контрагентов), не закрытые
            var orgs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            JArray accs = Safe(() => od.Rows("Catalog_БанковскиеСчета", new Dictionary<string, string>
            {
                ["$select"] = "Ref_Key,Owner,Owner_Type,НомерСчета,Банк_Key,ДатаЗакрытия",
                ["$filter"] = "DeletionMark eq false and Owner_Type eq 'StandardODATA.Catalog_Организации'",
            }));
            var orgRows = Safe(() => od.Rows("Catalog_Организации", new Dictionary<string, string> { ["$select"] = "Ref_Key,Description" }));
            foreach (var o in orgRows) orgs[(string)o["Ref_Key"]] = (string)o["Description"];
            var banks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in accs)
            {
                var closed = (DateTime?)a["ДатаЗакрытия"];
                if (closed.HasValue && closed.Value.Year > 1 && closed.Value < today) continue;
                var bk = (string)a["Банк_Key"] ?? "";
                if (!banks.TryGetValue(bk, out var bn))
                    banks[bk] = bn = (string)Safe(() => od.Rows("Catalog_Банки", new Dictionary<string, string> { ["$select"] = "Description", ["$filter"] = $"Ref_Key eq guid'{bk}'" })).FirstOrDefault()?["Description"] ?? "";
                var acc = new Account { Key = (string)a["Ref_Key"], Iik = (string)a["НомерСчета"], Bank = bn, Org = orgs.TryGetValue((string)a["Owner"] ?? "", out var on) ? on : null };
                foreach (var set in BankSets)
                {
                    var row = Safe(() => od.Rows(set, new Dictionary<string, string>
                    {
                        ["$select"] = "Date,ДатаВыписки",
                        ["$filter"] = $"Posted eq true and СчетОрганизации_Key eq guid'{acc.Key}'",
                        ["$orderby"] = "ДатаВыписки desc,Date desc",
                        ["$top"] = "1",
                    })).FirstOrDefault();
                    if (row == null) continue;
                    var d = (DateTime?)row["ДатаВыписки"];
                    if (!d.HasValue || d.Value.Year < 2) d = (DateTime?)row["Date"];
                    if (d.HasValue && (!acc.Last.HasValue || d.Value > acc.Last.Value)) acc.Last = d.Value.Date;
                }
                info.Accounts.Add(acc);
            }

            // Последний проведённый документ
            foreach (var set in DocSets)
            {
                var row = Safe(() => od.Rows(set, new Dictionary<string, string> { ["$select"] = "Date", ["$filter"] = "Posted eq true", ["$orderby"] = "Date desc", ["$top"] = "1" })).FirstOrDefault();
                var d = (DateTime?)row?["Date"];
                if (d.HasValue && (!info.LastDocument.HasValue || d.Value > info.LastDocument.Value)) info.LastDocument = d.Value.Date;
            }

            // Закрытие месяца — если документ есть в составе OData
            var cm = Safe(() => od.Rows("Document_ЗакрытиеМесяца", new Dictionary<string, string> { ["$select"] = "Date", ["$filter"] = "Posted eq true", ["$orderby"] = "Date desc", ["$top"] = "1" })).FirstOrDefault();
            if (cm != null) { var d = (DateTime)cm["Date"]; info.ClosedMonth = new DateTime(d.Year, d.Month, 1); }

            // Предупреждения «выписка устарела» убраны (Слава 22.10): агент только называет даты, «по состоянию на …» — в скилле
            return info;
        }

        /// <summary>Чего нет в составе OData или выключено на «Доступе» — пропускаем, свежесть не ломает ответ.</summary>
        private static JArray Safe(Func<JArray> f)
        {
            try { return f() ?? new JArray(); } catch { return new JArray(); }
        }
    }
}
