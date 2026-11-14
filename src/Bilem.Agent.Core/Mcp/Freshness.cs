using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent.Core.Mcp
{
    /// <summary>
    /// «Данные на …» (v0.3, часть D; одна дата — #102): учёт в 1С отстаёт от жизни, поэтому каждый ответ с цифрами называет дату —
    /// самая поздняя проведённая выписка или кассовый документ (для долгов — ещё продажи и покупки) и счета, где выписки отстают.
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

        /// <summary>Кассовые документы — дата кассы в «Остатки по данным 1С на …».</summary>
        internal static readonly string[] CashSets = { "Document_ПриходныйКассовыйОрдер", "Document_РасходныйКассовыйОрдер" };

        /// <summary>Продажи и покупки — для долгов дата берётся и по ним.</summary>
        internal static readonly string[] TradeSets =
        {
            "Document_РеализацияТоваровУслуг", "Document_ПоступлениеТоваровУслуг", "Document_ЧекККМ", "Document_ОтчетОРозничныхПродажах",
        };

        /// <summary>Счёт или касса отстают от общей даты больше чем на столько дней — отдельная строка «загружены по …».</summary>
        internal const int LagDays = 7;

        /// <summary>Для чего фраза: остатки денег или долги (дебиторка, кредиторка, акт сверки).</summary>
        public enum Kind { Money, Debts }

        public sealed class Account { public string Key; public string Org; public string Bank; public string Iik; public string Name; public DateTime? Last; public string Label; }

        public sealed class Info
        {
            public List<Account> Accounts = new List<Account>();
            /// <summary>Последний проведённый ПКО/РКО.</summary>
            public DateTime? LastCash;
            /// <summary>Последняя проведённая реализация, поступление, чек ККМ или отчёт о розничных продажах.</summary>
            public DateTime? LastTrade;
            public DateTime? ClosedMonth;
        }

        private static readonly Dictionary<string, (DateTime At, Info Value)> Cache = new Dictionary<string, (DateTime, Info)>();

        /// <summary>Блок as_of для ответа инструмента; кэш — минута на базу.</summary>
        public static JObject AsOf(BaseRuntime b, Kind kind)
        {
            Info info;
            lock (Cache)
                if (Cache.TryGetValue(b.Config.Slug, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(1)) return ToJson(c.Value, kind);
            info = Load(b, DateTime.Today);
            lock (Cache) Cache[b.Config.Slug] = (DateTime.UtcNow, info);
            return ToJson(info, kind);
        }

        /// <summary>Одна дата данных: самая поздняя выписка или кассовый документ; для долгов — ещё продажи и покупки.</summary>
        internal static DateTime? DataDate(Info i, Kind kind)
        {
            var all = i.Accounts.Select(a => a.Last).Append(i.LastCash);
            if (kind == Kind.Debts) all = all.Append(i.LastTrade);
            return all.Where(x => x.HasValue).Max();
        }

        internal static JObject ToJson(Info i, Kind kind)
        {
            var ci = CultureInfo.InvariantCulture;
            string D(DateTime? d) => d?.ToString("yyyy-MM-dd", ci);
            if (i.Accounts.Any(a => a.Label == null)) Labels(i.Accounts);
            var o = new JObject
            {
                ["date"] = D(DataDate(i, kind)),
                ["bank_statements"] = new JArray(i.Accounts.Select(a => new JObject
                {
                    ["account"] = a.Label, ["organization"] = a.Org, ["last_statement"] = D(a.Last),
                })),
                ["cash_documents"] = D(i.LastCash),
            };
            o["closed_month"] = i.ClosedMonth.HasValue ? (JToken)i.ClosedMonth.Value.ToString("yyyy-MM", ci) : JValue.CreateNull();
            o["say"] = Say(i, kind);
            return o;
        }

        /// <summary>Только цифры ИИК (без «KZ» и букв кода банка).</summary>
        private static string Digits(string iik) => new string((iik ?? "").Where(char.IsDigit).ToArray());

        /// <summary>
        /// «АО "KASPI BANK" ···7183» — банк и последние 4 цифры ИИК. Совпали у нескольких счетов — берём столько цифр, чтобы различались;
        /// номера одинаковые или пустые — добавляем наименование счёта из 1С, иначе порядковый номер (#102: «···2025» трижды).
        /// </summary>
        internal static void Labels(List<Account> accs)
        {
            string Tail(Account a, int n) { var d = Digits(a.Iik); return d.Length == 0 ? "" : " ···" + d.Substring(Math.Max(0, d.Length - n)); }
            string Bank(Account a) => (a.Bank ?? "").Trim();
            foreach (var a in accs) a.Label = (Bank(a) + Tail(a, 4)).Trim();
            foreach (var g in accs.GroupBy(a => a.Label).Where(g => g.Count() > 1).ToList())
            {
                var list = g.ToList();
                var max = list.Max(a => Digits(a.Iik).Length);
                var numbers = list.Select(a => Digits(a.Iik)).Distinct().Count();
                for (var n = 5; n <= max; n++)
                {
                    if (list.Select(a => Tail(a, n)).Distinct().Count() < numbers) continue;
                    foreach (var a in list) a.Label = (Bank(a) + Tail(a, n)).Trim();
                    break;
                }
                foreach (var h in list.GroupBy(a => a.Label).Where(h => h.Count() > 1).ToList())
                {
                    var same = h.ToList();
                    var k = 0;
                    foreach (var a in same)
                    {
                        var name = (a.Name ?? "").Trim();
                        var unique = name.Length > 0 && same.Count(x => (x.Name ?? "").Trim() == name) == 1;
                        a.Label += unique ? $" ({name})" : $" (№{++k})";
                    }
                }
            }
        }

        /// <summary>
        /// Готовая фраза для модели (#102): одна дата и одна формулировка при каждом вызове —
        /// «Остатки по данным 1С на 12.11.2026» и отдельной строкой каждый счёт (и касса), где документы отстают больше чем на неделю.
        /// «Последний документ в базе» не называем: он не объясняет остаток и путается с датой выписки.
        /// </summary>
        internal static string Say(Info i, Kind kind)
        {
            var date = DataDate(i, kind);
            if (!date.HasValue) return Txt.S("AsOfSayUnknown");
            if (i.Accounts.Any(a => a.Label == null)) Labels(i.Accounts);
            var lines = new List<string> { Txt.F(kind == Kind.Money ? "AsOfSayMoney" : "AsOfSayDebts", date.Value) };
            foreach (var a in i.Accounts.Where(a => a.Last.HasValue && (date.Value - a.Last.Value).TotalDays > LagDays))
                lines.Add(Txt.F("AsOfSayBankLag", a.Label, a.Last.Value));
            if (i.LastCash.HasValue && (date.Value - i.LastCash.Value).TotalDays > LagDays)
                lines.Add(Txt.F("AsOfSayCashLag", i.LastCash.Value));
            return string.Join("\n", lines);
        }

        internal static Info Load(BaseRuntime b, DateTime today)
        {
            var od = b.OData;
            var info = new Info();

            // Счета организаций (не контрагентов), не закрытые
            var orgs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            JArray accs = Safe(() => od.Rows("Catalog_БанковскиеСчета", new Dictionary<string, string>
            {
                ["$select"] = "Ref_Key,Description,Owner,Owner_Type,НомерСчета,Банк_Key,ДатаЗакрытия",
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
                var acc = new Account { Key = (string)a["Ref_Key"], Iik = (string)a["НомерСчета"], Name = (string)a["Description"], Bank = bn, Org = orgs.TryGetValue((string)a["Owner"] ?? "", out var on) ? on : null };
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
            Labels(info.Accounts);

            // Касса и продажи/покупки — последний проведённый документ по дате документа
            DateTime? Last(IEnumerable<string> sets)
            {
                DateTime? max = null;
                foreach (var set in sets)
                {
                    var row = Safe(() => od.Rows(set, new Dictionary<string, string> { ["$select"] = "Date", ["$filter"] = "Posted eq true", ["$orderby"] = "Date desc", ["$top"] = "1" })).FirstOrDefault();
                    var d = (DateTime?)row?["Date"];
                    if (d.HasValue && (!max.HasValue || d.Value > max.Value)) max = d.Value.Date;
                }
                return max;
            }
            info.LastCash = Last(CashSets);
            info.LastTrade = Last(TradeSets);

            // Закрытие месяца — если документ есть в составе OData
            var cm = Safe(() => od.Rows("Document_ЗакрытиеМесяца", new Dictionary<string, string> { ["$select"] = "Date", ["$filter"] = "Posted eq true", ["$orderby"] = "Date desc", ["$top"] = "1" })).FirstOrDefault();
            if (cm != null) { var d = (DateTime)cm["Date"]; info.ClosedMonth = new DateTime(d.Year, d.Month, 1); }

            // Предупреждения «выписка устарела» убраны (Слава 22.10); с #102 — только строка «выписки загружены по …» у отстающего счёта
            return info;
        }

        /// <summary>Чего нет в составе OData или выключено на «Доступе» — пропускаем, свежесть не ломает ответ.</summary>
        private static JArray Safe(Func<JArray> f)
        {
            try { return f() ?? new JArray(); } catch { return new JArray(); }
        }
    }
}
