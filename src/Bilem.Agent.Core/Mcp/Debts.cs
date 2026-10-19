using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bilem.Agent.Core.Print;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent.Core.Mcp
{
    /// <summary>Чья задолженность: нам (дебиторка, receivables) или наша (кредиторка, payables).</summary>
    public enum DebtKind { Receivables, Payables }

    /// <summary>Непогашенный документ: часть его суммы ещё не закрыта оплатой (FIFO).</summary>
    public sealed class DebtDoc
    {
        public string Recorder;
        public string Type;
        public DateTime Date;
        public string ContractKey;
        public decimal Amount;
        public decimal Unpaid;
        public int Days;
        public string Number;
        public string InvoiceNumber;
        public DateTime? InvoiceDate;
    }

    /// <summary>Расчёты с контрагентом по одному договору на дату.</summary>
    public sealed class DebtContract
    {
        public string Key;
        public string Name;
        /// <summary>Сальдо счёта расчётов (1210 / 3310) — в нашу сторону: долг контрагента нам / наш долг ему.</summary>
        public decimal Main;
        /// <summary>Аванс по тому же договору (3510 / 1710), положительный.</summary>
        public decimal Advance;
        /// <summary>Долг за вычетом аванса.</summary>
        public decimal Owed => Main - Advance;
        public List<DebtDoc> Docs = new List<DebtDoc>();
        /// <summary>Часть долга, которой не хватило документов в окне выборки (старше начала окна).</summary>
        public decimal Older;
    }

    public sealed class Debtor
    {
        public string Org;
        public string Key;
        public string Name;
        public string Bin;
        public List<DebtContract> Contracts = new List<DebtContract>();
        public decimal Amount => Contracts.Where(c => c.Owed > 0).Sum(c => c.Owed);
        public decimal Advance => Contracts.Sum(c => c.Advance);
        public IEnumerable<DebtDoc> Docs => Contracts.SelectMany(c => c.Docs);
        /// <summary>Долг по документам старше min_days / выставленным до issued_before — то, что считаем «просроченным» по запросу.</summary>
        public decimal Selected;
        public DebtDoc Oldest => Docs.OrderBy(d => d.Date).FirstOrDefault();
    }

    /// <summary>
    /// «Кто нам должен» и «кому должны мы» по регистру бухгалтерии («Типовой»): сальдо счетов расчётов по контрагентам и договорам
    /// (1210 / 3310 с субсчетами) минус авансы по тому же контрагенту и договору (3510 / 1710). Возраст долга — FIFO: долг закрывается
    /// оплатами от самых старых документов, остаток приходится на последние документы, увеличившие долг. Только чтение.
    /// </summary>
    public static class Debts
    {
        public const string CpType = "StandardODATA.Catalog_Контрагенты";
        public const string ContractType = "StandardODATA.Catalog_ДоговорыКонтрагентов";

        /// <summary>Счёт расчётов и счёт авансов (с субсчетами) для вида задолженности.</summary>
        public static (string Main, string Advance) Accounts(DebtKind k) => k == DebtKind.Receivables ? ("1210", "3510") : ("3310", "1710");

        /// <summary>Ключи счёта и его субсчетов по плану счетов.</summary>
        internal static HashSet<string> KeysOf(JArray chart, string code)
        {
            var byKey = chart.OfType<JObject>().ToDictionary(a => (string)a["Ref_Key"], a => a);
            var res = new HashSet<string>();
            foreach (var a in byKey.Values)
                for (JObject x = a; x != null; x = byKey.TryGetValue((string)x["Parent_Key"] ?? "", out var p) ? p : null)
                    if ((string)x["Code"] == code) { res.Add((string)a["Ref_Key"]); break; }
            return res;
        }

        /// <summary>
        /// Должники из остатков на дату: группировка организация → контрагент → договор; знак — в нашу сторону
        /// (дебиторка: Дт 1210 — долг, Кт 3510 — аванс; кредиторка: Кт 3310 — долг, Дт 1710 — аванс).
        /// </summary>
        internal static List<Debtor> FromBalance(DebtKind kind, JArray balance, HashSet<string> main, HashSet<string> adv)
        {
            var sign = kind == DebtKind.Receivables ? 1m : -1m;
            var map = new Dictionary<string, Debtor>();
            foreach (var b in balance)
            {
                var acc = (string)b["Account_Key"];
                var isMain = main.Contains(acc);
                if (!isMain && !adv.Contains(acc)) continue;
                if ((string)b["ExtDimension1_Type"] != CpType) continue;
                var cp = (string)b["ExtDimension1"];
                if (Od.IsEmpty(cp)) continue;
                var org = (string)b["Организация_Key"] ?? "";
                var contract = (string)b["ExtDimension2_Type"] == ContractType ? (string)b["ExtDimension2"] : null;
                if (Od.IsEmpty(contract)) contract = null;
                var k = org + "|" + cp;
                if (!map.TryGetValue(k, out var d)) map[k] = d = new Debtor { Org = org, Key = cp };
                var c = d.Contracts.FirstOrDefault(x => x.Key == contract);
                if (c == null) d.Contracts.Add(c = new DebtContract { Key = contract });
                var sum = Od.Dec(b["СуммаBalance"]) * sign;
                if (isMain) c.Main += sum; else c.Advance -= sum;
            }
            return map.Values.ToList();
        }

        /// <summary>
        /// FIFO: для каждого договора с долгом — документы, увеличившие долг (Дт 1210 / Кт 3310 по этому контрагенту и договору),
        /// от последнего к первому, пока их сумма не покроет долг. Возвращает true, если хватило окна выборки у всех.
        /// </summary>
        internal static bool Age(DebtKind kind, List<Debtor> debtors, JArray records, HashSet<string> main, DateTime asOf)
        {
            var side = kind == DebtKind.Receivables ? "Dr" : "Cr";
            var other = side == "Dr" ? "Cr" : "Dr";
            // Увеличения долга: организация|контрагент|договор → документы (сумма по регистратору)
            var inc = new Dictionary<string, Dictionary<string, DebtDoc>>();
            foreach (var r in records)
            {
                if ((bool?)r["Active"] == false) continue;
                if (!main.Contains((string)r["Account" + side + "_Key"])) continue;
                if ((string)r["ExtDimension" + side + "1_Type"] != CpType) continue;
                var cp = (string)r["ExtDimension" + side + "1"];
                // Проводка внутри расчётов того же контрагента (перенос между субсчетами 1210) — не новый долг
                if (main.Contains((string)r["Account" + other + "_Key"]) && (string)r["ExtDimension" + other + "1"] == cp) continue;
                var contract = (string)r["ExtDimension" + side + "2_Type"] == ContractType ? (string)r["ExtDimension" + side + "2"] : null;
                if (Od.IsEmpty(contract)) contract = null;
                var sum = Od.Dec(r["Сумма"]);
                if (sum <= 0) continue;   // сторно и возвраты уменьшают долг — их учитывает остаток, не возраст
                var k = ((string)r["Организация_Key"] ?? "") + "|" + cp + "|" + contract;
                if (!inc.TryGetValue(k, out var docs)) inc[k] = docs = new Dictionary<string, DebtDoc>();
                var rec = (string)r["Recorder"];
                if (!docs.TryGetValue(rec, out var doc))
                    docs[rec] = doc = new DebtDoc { Recorder = rec, Type = ((string)r["Recorder_Type"] ?? "").Replace("StandardODATA.", ""), Date = ((DateTime)r["Period"]).Date, ContractKey = contract };
                doc.Amount += sum;
            }
            var enough = true;
            foreach (var d in debtors)
                foreach (var c in d.Contracts)
                {
                    c.Docs.Clear(); c.Older = 0;
                    var left = c.Owed;
                    if (left <= 0) continue;
                    if (inc.TryGetValue(d.Org + "|" + d.Key + "|" + c.Key, out var docs))
                        foreach (var doc in docs.Values.OrderByDescending(x => x.Date).ThenByDescending(x => x.Recorder))
                        {
                            if (left <= 0) break;
                            doc.Unpaid = Math.Min(doc.Amount, left);
                            doc.Days = Math.Max(0, (int)(asOf.Date - doc.Date).TotalDays);
                            left -= doc.Unpaid;
                            c.Docs.Add(doc);
                        }
                    if (left > 0) { c.Older = left; enough = false; }
                    c.Docs.Reverse();   // от старого к новому
                }
            return enough;
        }

        /// <summary>Корзины возраста 0–30 / 31–60 / 61–90 / 90+ по непогашенным частям документов; неразобранный остаток — в 90+.</summary>
        internal static decimal[] Buckets(IEnumerable<Debtor> debtors)
        {
            var b = new decimal[4];
            foreach (var d in debtors)
                foreach (var c in d.Contracts.Where(c => c.Owed > 0))
                {
                    foreach (var doc in c.Docs) b[doc.Days <= 30 ? 0 : doc.Days <= 60 ? 1 : doc.Days <= 90 ? 2 : 3] += doc.Unpaid;
                    b[3] += c.Older;
                }
            return b;
        }

        /// <summary>
        /// Отбор по запросу: min_days — долг по документам не моложе N дней; issued_before — по документам, выставленным раньше даты
        /// («не оплатили с прошлого месяца» = до 1-го числа текущего месяца). Неразобранный остаток считается старым.
        /// </summary>
        internal static void Select(IEnumerable<Debtor> debtors, int minDays, DateTime? issuedBefore)
        {
            foreach (var d in debtors)
                d.Selected = d.Contracts.Where(c => c.Owed > 0).Sum(c =>
                    c.Docs.Where(x => x.Days >= minDays && (!issuedBefore.HasValue || x.Date < issuedBefore.Value.Date)).Sum(x => x.Unpaid) + c.Older);
        }

        // ---------- Загрузка из 1С ----------

        public sealed class Request
        {
            public DebtKind Kind;
            public DateTime AsOf = DateTime.Today;
            public string Organization;
            public string Counterparty;
            public int MinDays;
            public decimal MinAmount;
            public DateTime? IssuedBefore;
            public int Top = 20;
            /// <summary>Документов в ответе на одного контрагента (самые старые).</summary>
            public int DocsPerDebtor = 10;
        }

        public sealed class Result
        {
            public List<Debtor> All = new List<Debtor>();
            public List<Debtor> Shown = new List<Debtor>();
            public decimal[] Buckets = new decimal[4];
            /// <summary>Как в ОСВ: сальдо счёта расчётов в нашу сторону по контрагентам и договорам (Дт 1210 / Кт 3310) и сальдо авансов.</summary>
            public decimal OsvMain, OsvAdvance;
            public bool Fifo = true;
            public DateTime WindowFrom;
            public Dictionary<string, string> OrgNames = new Dictionary<string, string>();
        }

        /// <summary>Окно выборки документов для FIFO: два года до даты; не хватило — до начала учёта (2000 г.).</summary>
        public const int WindowMonths = 24;

        public static Result Load(BaseRuntime b, Request q)
        {
            var od = b.OData;
            var reg = "AccountingRegister_" + b.Profile.AccountingRegister;
            var chart = od.Rows("ChartOfAccounts_" + b.Profile.ChartOfAccounts, new Dictionary<string, string> { ["$select"] = "Ref_Key,Code,Parent_Key" });
            var (mainCode, advCode) = Accounts(q.Kind);
            var main = KeysOf(chart, mainCode);
            var adv = KeysOf(chart, advCode);
            if (main.Count == 0) throw new AgentException(Txt.F("DebtNoAccount", mainCode));
            var end = q.AsOf.Date.AddDays(1).AddSeconds(-1);

            string cpKey = null;
            if (!string.IsNullOrWhiteSpace(q.Counterparty)) cpKey = ReconciliationSource.Counterparty(od, q.Counterparty).Key;
            string Accs(IEnumerable<string> keys, string field) => string.Join(" or ", keys.Select(k => $"{field} eq guid'{k}'"));
            var cpCond = cpKey == null ? "" : $" and ExtDimension1 eq cast(guid'{cpKey}', 'Catalog_Контрагенты')";
            // Условие по счетам — в $filter (не в сегменте пути: лимит http.sys на длину сегмента, known-issues 10)
            var balance = od.Rows($"{reg}/Balance(Period=datetime'{Od.D(end)}')",
                new Dictionary<string, string> { ["$filter"] = "(" + Accs(main.Concat(adv), "Account_Key") + ")" + cpCond });

            var res = new Result();
            var orgs = od.Rows("Catalog_Организации", new Dictionary<string, string> { ["$select"] = "Ref_Key,Description,ИдентификационныйНомер" });
            foreach (var o in orgs) res.OrgNames[(string)o["Ref_Key"]] = (string)o["Description"];
            string orgKey = null;
            if (!string.IsNullOrWhiteSpace(q.Organization))
            {
                var o = orgs.FirstOrDefault(x => (string)x["ИдентификационныйНомер"] == q.Organization.Trim())
                    ?? orgs.FirstOrDefault(x => ((string)x["Description"] ?? "").IndexOf(q.Organization.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                    ?? throw new AgentException(Txt.F("ActNoOrganization", q.Organization));
                orgKey = (string)o["Ref_Key"];
            }
            var all = FromBalance(q.Kind, balance, main, adv).Where(d => orgKey == null || d.Org == orgKey).ToList();
            res.OsvMain = all.SelectMany(d => d.Contracts).Where(c => c.Main > 0).Sum(c => c.Main);
            res.OsvAdvance = all.SelectMany(d => d.Contracts).Sum(c => c.Advance);
            var debtors = all.Where(d => d.Amount > 0).ToList();

            // Возраст: документы, увеличившие долг, за окно; не хватило — расширяем до начала учёта
            if (debtors.Count > 0)
            {
                var side = q.Kind == DebtKind.Receivables ? "Dr" : "Cr";
                res.WindowFrom = q.AsOf.Date.AddMonths(-WindowMonths);
                JArray Records(DateTime from) => od.Rows($"{reg}/RecordsWithExtDimensions(StartPeriod=datetime'{Od.D(from)}',EndPeriod=datetime'{Od.D(end)}')",
                    new Dictionary<string, string>
                    {
                        ["$filter"] = "(" + Accs(main, "Account" + side + "_Key") + ")" + (cpKey == null ? "" : $" and ExtDimension{side}1 eq cast(guid'{cpKey}', 'Catalog_Контрагенты')"),
                    });
                if (!Age(q.Kind, debtors, Records(res.WindowFrom), main, q.AsOf))
                {
                    res.WindowFrom = new DateTime(2000, 1, 1);
                    Age(q.Kind, debtors, Records(res.WindowFrom), main, q.AsOf);
                }
            }
            res.Buckets = Buckets(debtors);
            Select(debtors, q.MinDays, q.IssuedBefore);
            res.All = debtors.Where(d => d.Selected > 0 && d.Selected >= q.MinAmount).OrderByDescending(d => d.Selected).ToList();
            res.Shown = res.All.Take(Math.Max(1, q.Top)).ToList();

            Describe(od, q, res.Shown);
            return res;
        }

        /// <summary>Имена, БИН (у физлиц — нет), договоры, номера документов и счета-фактуры — только для показанных строк.</summary>
        private static void Describe(ODataClient od, Request q, List<Debtor> shown)
        {
            if (shown.Count == 0) return;
            string In(IEnumerable<string> keys) => string.Join(" or ", keys.Select(k => $"Ref_Key eq guid'{k}'"));
            foreach (var chunk in Chunks(shown.Select(d => d.Key).Distinct(), 25))
                foreach (var c in od.Rows("Catalog_Контрагенты", new Dictionary<string, string>
                {
                    ["$select"] = "Ref_Key,Description,ИдентификационныйКодЛичности,ЮрФизЛицо,ИндивидуальныйПредпринимательАдвокатЧастныйНотариус",
                    ["$filter"] = In(chunk),
                }))
                    foreach (var d in shown.Where(x => x.Key == (string)c["Ref_Key"]))
                    {
                        d.Name = (string)c["Description"];
                        // ИИН физлица наружу не отдаём (как везде); БИН организаций и ИП — да
                        var person = (string)c["ЮрФизЛицо"] == "ФизЛицо" && (bool?)c["ИндивидуальныйПредпринимательАдвокатЧастныйНотариус"] != true;
                        d.Bin = person ? null : Od.NotEmpty((string)c["ИдентификационныйКодЛичности"]);
                    }
            var contractKeys = shown.SelectMany(d => d.Contracts).Select(c => c.Key).Where(k => k != null).Distinct().ToList();
            foreach (var chunk in Chunks(contractKeys, 25))
                foreach (var c in od.Rows("Catalog_ДоговорыКонтрагентов", new Dictionary<string, string> { ["$select"] = "Ref_Key,Description", ["$filter"] = In(chunk) }))
                    foreach (var x in shown.SelectMany(d => d.Contracts).Where(x => x.Key == (string)c["Ref_Key"])) x.Name = (string)c["Description"];

            // Документы в ответе — самые старые непогашенные; номера — только у видов, включённых на «Доступе»
            foreach (var d in shown)
                foreach (var c in d.Contracts)
                    if (c.Docs.Count > q.DocsPerDebtor) c.Docs = c.Docs.Take(q.DocsPerDebtor).ToList();
            foreach (var g in shown.SelectMany(d => d.Docs).GroupBy(x => x.Type))
            {
                if (od.Deny != null && od.Deny(g.Key) != null) continue;
                foreach (var chunk in Chunks(g.Select(x => x.Recorder).Distinct(), 25))
                {
                    JArray rows;
                    try { rows = od.Rows(g.Key, new Dictionary<string, string> { ["$select"] = "Ref_Key,Number", ["$filter"] = In(chunk) }); }
                    catch (AgentException) { continue; }   // вида нет в составе OData — без номера
                    foreach (var r in rows)
                        foreach (var x in g.Where(x => x.Recorder == (string)r["Ref_Key"])) x.Number = InvoiceSource.PrintNumber((string)r["Number"]);
                }
            }

            // Счёт-фактура к документу: выданный (дебиторка) / полученный (кредиторка), по основанию
            var set = q.Kind == DebtKind.Receivables ? "Document_СчетФактураВыданный" : "Document_СчетФактураПолученный";
            if (od.Deny != null && od.Deny(set) != null) return;
            foreach (var d in shown)
            {
                var docs = d.Docs.ToList();
                if (docs.Count == 0) continue;
                var from = docs.Min(x => x.Date).AddDays(-31);
                JArray inv;
                try
                {
                    inv = od.Rows(set, new Dictionary<string, string>
                    {
                        ["$select"] = "Number,Date,ДокументОснование,ДокументыОснования",
                        ["$filter"] = $"Контрагент_Key eq guid'{d.Key}' and Posted eq true and Date ge datetime'{Od.D(from)}'",
                    });
                }
                catch (AgentException) { continue; }
                foreach (var f in inv)
                {
                    var bases = new List<string> { (string)f["ДокументОснование"] };
                    if (f["ДокументыОснования"] is JArray tab) bases.AddRange(tab.Select(t => (string)t["ДокументОснование"]));
                    foreach (var x in docs.Where(x => x.InvoiceNumber == null && bases.Contains(x.Recorder)))
                    {
                        x.InvoiceNumber = InvoiceSource.PrintNumber((string)f["Number"]);
                        x.InvoiceDate = Od.HasDate(f["Date"]) ? ((DateTime)f["Date"]).Date : (DateTime?)null;
                    }
                }
            }
        }

        private static IEnumerable<List<string>> Chunks(IEnumerable<string> items, int n)
        {
            var list = items.ToList();
            for (var i = 0; i < list.Count; i += n) yield return list.Skip(i).Take(n).ToList();
        }

        // ---------- Ответ инструмента ----------

        internal static JObject ToJson(BaseRuntime b, Request q, Result r, JObject asOf)
        {
            var ci = CultureInfo.InvariantCulture;
            string D(DateTime d) => d.ToString("yyyy-MM-dd", ci);
            var multiOrg = r.All.Select(d => d.Org).Distinct().Count() > 1;
            var rec = q.Kind == DebtKind.Receivables;
            var (mainCode, advCode) = Accounts(q.Kind);
            var list = new JArray();
            foreach (var d in r.Shown)
            {
                var o = new JObject { ["counterparty"] = d.Name ?? d.Key };
                o["bin"] = d.Bin == null ? JValue.CreateNull() : (JToken)d.Bin;
                if (multiOrg) o["organization"] = r.OrgNames.TryGetValue(d.Org, out var on) ? on : d.Org;
                o["amount"] = Tools.Money((double)d.Selected);
                if (d.Selected != d.Amount) o["total_debt"] = Tools.Money((double)d.Amount);
                if (d.Advance != 0) o["advance"] = Tools.Money((double)d.Advance);
                var oldest = d.Oldest;
                if (oldest != null) { o["oldest_date"] = D(oldest.Date); o["days"] = oldest.Days; }
                if (d.Contracts.Any(c => c.Older > 0)) { o["older_than_window"] = Tools.Money((double)d.Contracts.Sum(c => c.Older)); o["days"] = Math.Max((int?)o["days"] ?? 0, (int)(q.AsOf - r.WindowFrom).TotalDays); }
                var withDebt = d.Contracts.Where(c => c.Owed > 0).ToList();
                if (withDebt.Count > 1 || withDebt.Any(c => c.Name != null))
                    o["contracts"] = new JArray(withDebt.Select(c => new JObject
                    {
                        ["contract"] = c.Name ?? (c.Key == null ? Txt.S("PrintNoContract") : c.Key),
                        ["amount"] = Tools.Money((double)c.Owed),
                    }));
                o["documents"] = new JArray(d.Docs.OrderBy(x => x.Date).Select(x =>
                {
                    var j = new JObject
                    {
                        ["document"] = ReconciliationSource.DocTitle("StandardODATA." + x.Type, x.Number, null).Doc,
                        ["date"] = D(x.Date),
                        ["amount"] = Tools.Money((double)x.Amount),
                        ["paid"] = Tools.Money((double)(x.Amount - x.Unpaid)),
                        ["unpaid"] = Tools.Money((double)x.Unpaid),
                        ["days"] = x.Days,
                    };
                    if (x.InvoiceNumber != null) j["invoice"] = new JObject { ["number"] = x.InvoiceNumber, ["date"] = x.InvoiceDate.HasValue ? (JToken)D(x.InvoiceDate.Value) : JValue.CreateNull() };
                    return j;
                }));
                list.Add(o);
            }
            var res = new JObject
            {
                ["base"] = b.Config.Slug,
                ["kind"] = rec ? "receivables" : "payables",
                ["as_of_date"] = D(q.AsOf),
                ["currency"] = "KZT",
                ["aging_method"] = "fifo",
                ["total"] = Tools.Money((double)r.All.Sum(d => d.Selected)),
                ["count"] = r.All.Count,
                ["shown"] = r.Shown.Count,
                ["buckets"] = new JObject
                {
                    ["0_30"] = Tools.Money((double)r.Buckets[0]), ["31_60"] = Tools.Money((double)r.Buckets[1]),
                    ["61_90"] = Tools.Money((double)r.Buckets[2]), ["90_plus"] = Tools.Money((double)r.Buckets[3]),
                },
                [rec ? "debtors" : "creditors"] = list,
                ["osv"] = new JObject
                {
                    ["account"] = mainCode, ["balance"] = Tools.Money((double)r.OsvMain),
                    ["advance_account"] = advCode, ["advance_balance"] = Tools.Money((double)r.OsvAdvance),
                },
                ["note"] = Txt.S(rec ? "DebtNoteReceivables" : "DebtNotePayables"),
                ["as_of"] = asOf,
            };
            if (q.MinDays > 0) res["min_days"] = q.MinDays;
            if (q.IssuedBefore.HasValue) res["issued_before"] = D(q.IssuedBefore.Value);
            if (r.All.Count > r.Shown.Count) res["more"] = Txt.F("ToolTruncated", r.Shown.Count, r.All.Count);
            // Давность выписок — первым предупреждением: должник мог уже заплатить, а выписка не загружена
            var warn = (asOf?["warnings"] as JArray)?.Select(w => (string)w).Where(w => w != null).ToList() ?? new List<string>();
            if (warn.Count > 0) res["warning"] = Txt.S(rec ? "DebtStaleReceivables" : "DebtStalePayables") + " " + string.Join(" ", warn);
            return res;
        }
    }
}
