using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Bilem.Agent.Core.Print;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent.Core.Mcp
{
    /// <summary>
    /// ОСВ по любому счёту (trial_balance) и движения по счёту (account_entries) — по регистру бухгалтерии «Типовой» (#90).
    /// Сальдо считаем свёрнутым по каждой строке разреза (Дт − Кт), развёрнутое — из него: так же, как ОСВ 1С по субконто.
    /// Сотрудники — поимённо по умолчанию (#105, без услуги «Зарплата»); выключен показ имён в базе (BaseConfig.EmployeeNames) — без имён. Только чтение.
    /// </summary>
    public static class Ledger
    {
        public const int MaxAccounts = 10;
        public const int MaxBy = 3;

        /// <summary>Разрезы по умолчанию → типы значений субконто (наборы OData).</summary>
        internal static readonly Dictionary<string, string[]> Aliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["counterparty"] = new[] { "Catalog_Контрагенты" },
            ["contract"] = new[] { "Catalog_ДоговорыКонтрагентов" },
            ["employee"] = new[] { "Catalog_ФизическиеЛица", "Catalog_Сотрудники", "Catalog_Работники" },
            ["item"] = new[] { "Catalog_Номенклатура" },
        };

        internal static bool IsEmployeeSet(string set) => Aliases["employee"].Contains(set);

        public sealed class Acc
        {
            public string Key, Code, Name, Kind, Parent;
        }

        /// <summary>Строка регистра (остатки и обороты): счёт, организация, до трёх субконто; суммы свёрнутые (+ Дт, − Кт).</summary>
        public sealed class Cell
        {
            public string Account, Org;
            public string[] Types = new string[3], Values = new string[3];
            public decimal Open, TurnDr, TurnCr, Close;
        }

        public sealed class Row
        {
            public string Account, Org;
            /// <summary>Разрез → (набор OData, значение); значение null — субконто нет или скрыто.</summary>
            public List<(string By, string Set, string Value)> Keys = new List<(string, string, string)>();
            public decimal Open, TurnDr, TurnCr, Close;
            public bool Zero => Open == 0 && TurnDr == 0 && TurnCr == 0 && Close == 0;
            public decimal Size => Math.Max(Math.Abs(Close), Math.Max(Math.Abs(Open), Math.Max(TurnDr, TurnCr)));
            /// <summary>aging: непогашенный остаток по корзинам возраста, самое старое увеличение и часть старше окна истории.</summary>
            public decimal[] Aging;
            public Entry Oldest;
            public decimal Older;
        }

        // ---------- План счетов ----------

        internal static List<Acc> Chart(BaseRuntime b) => b.Cached("ledger-chart", () =>
            b.OData.Rows("ChartOfAccounts_" + b.Profile.ChartOfAccounts, new Dictionary<string, string> { ["$select"] = "Ref_Key,Code,Description,Type,Parent_Key" })
                .Select(a => new Acc
                {
                    Key = (string)a["Ref_Key"], Code = (string)a["Code"], Name = (string)a["Description"], Parent = (string)a["Parent_Key"],
                    Kind = (string)a["Type"] == "Active" ? "active" : (string)a["Type"] == "Passive" ? "passive" : "mixed",
                }).ToList());

        /// <summary>«17.10», «1710», «17 10» → «1710»: только цифры (голос даёт точки, #85).</summary>
        internal static string NormCode(string s) => Regex.Replace(s ?? "", @"\D", "");

        /// <summary>
        /// Счёт по коду с субсчетами: есть такой код — он и все его потомки; нет и код короче 4 цифр («17») — все счета, чей код
        /// с него начинается (группа). Возвращает найденный счёт верхнего уровня (или null для группы) и ключи.
        /// </summary>
        internal static (Acc Top, HashSet<string> Keys) Resolve(List<Acc> chart, string code)
        {
            var c = NormCode(code);
            if (c.Length == 0) throw new AgentException(Txt.F("LedgerBadAccount", code));
            var byKey = chart.Where(a => a.Key != null).GroupBy(a => a.Key).ToDictionary(g => g.Key, g => g.First());
            var top = chart.FirstOrDefault(a => a.Code == c);
            var keys = new HashSet<string>();
            if (top != null)
            {
                foreach (var a in chart)
                    for (var x = a; x != null; x = x.Parent != null && byKey.TryGetValue(x.Parent, out var p) ? p : null)
                        if (x.Key == top.Key) { keys.Add(a.Key); break; }
                return (top, keys);
            }
            if (c.Length < 4) foreach (var a in chart.Where(a => (a.Code ?? "").StartsWith(c, StringComparison.Ordinal))) keys.Add(a.Key);
            if (keys.Count == 0) throw new AgentException(Txt.F("LedgerNoAccount", code));
            return (null, keys);
        }

        // ---------- Разрезы ----------

        /// <summary>Подходит ли субконто с типом set под разрез by: псевдоним (counterparty…) или часть имени вида субконто / набора.</summary>
        internal static bool Matches(string by, string set, Func<string, string> titleOf)
        {
            if (string.IsNullOrEmpty(set)) return false;
            if (Aliases.TryGetValue(by, out var sets)) return sets.Contains(set);
            string N(string s) => Regex.Replace((s ?? "").ToLowerInvariant().Replace('ё', 'е'), @"[^\p{L}\p{Nd}]", "");
            var q = N(by);
            if (q.Length < 3) return false;
            var name = set.Substring(set.IndexOf('_') + 1);
            return N(name).Contains(q) || N(titleOf?.Invoke(set)).Contains(q);
        }

        /// <summary>Набор OData из типа субконто («StandardODATA.Catalog_Контрагенты» → «Catalog_Контрагенты»).</summary>
        internal static string SetOf(string type) =>
            string.IsNullOrEmpty(type) || !type.StartsWith("StandardODATA.", StringComparison.Ordinal) || type == "StandardODATA.Undefined" ? null : type.Substring("StandardODATA.".Length);

        internal static string Alias(string set)
        {
            foreach (var kv in Aliases) if (kv.Value.Contains(set)) return kv.Key;
            return set == null ? "other" : set.Substring(set.IndexOf('_') + 1);
        }

        /// <summary>Отбор строк: каждое условие — значение одного из субконто строки среди допустимых ключей.</summary>
        internal static bool Pass(string[] types, string[] values, List<(string[] Sets, HashSet<string> Keys)> filters)
        {
            foreach (var f in filters)
            {
                var ok = false;
                for (var i = 0; i < 3 && !ok; i++)
                    ok = f.Sets.Contains(SetOf(types[i])) && values[i] != null && f.Keys.Contains(values[i]);
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>
        /// Свёртка строк регистра в строки ответа: счёт (код), организация, значения выбранных разрезов. hidden — разрезы,
        /// значения которых не показываем (сотрудники, когда показ имён выключен): строки по ним сливаются.
        /// </summary>
        internal static List<Row> Aggregate(IEnumerable<Cell> cells, Dictionary<string, Acc> accByKey, IList<string> by, Func<string, string> titleOf, ISet<string> hidden)
        {
            var map = new Dictionary<string, Row>();
            foreach (var c in cells)
            {
                if (!accByKey.TryGetValue(c.Account ?? "", out var acc)) continue;
                var keys = KeysOf(c.Types, c.Values, by, titleOf, hidden);
                var k = RowKey(acc.Code, c.Org, keys);
                if (!map.TryGetValue(k, out var r)) map[k] = r = new Row { Account = acc.Code, Org = c.Org ?? "", Keys = keys };
                else
                    // одна строка, а тип субконто разный (у разных субсчетов) — оставляем первый непустой
                    for (var i = 0; i < keys.Count; i++) if (r.Keys[i].Set == null && keys[i].Item2 != null) r.Keys[i] = keys[i];
                r.Open += c.Open; r.TurnDr += c.TurnDr; r.TurnCr += c.TurnCr; r.Close += c.Close;
            }
            return map.Values.Where(r => !r.Zero).ToList();
        }

        /// <summary>Значения разрезов by по субконто строки (регистра или одной стороны проводки).</summary>
        internal static List<(string By, string Set, string Value)> KeysOf(string[] types, string[] values, IList<string> by, Func<string, string> titleOf, ISet<string> hidden)
        {
            var keys = new List<(string, string, string)>();
            foreach (var t in by)
            {
                string set = null, val = null;
                for (var i = 0; i < 3; i++)
                {
                    var s = SetOf(types[i]);
                    if (Matches(t, s, titleOf)) { set = s; val = Od.IsEmpty(values[i]) ? null : values[i]; break; }
                }
                if (set != null && hidden != null && hidden.Contains(set)) val = null;
                keys.Add((t, set, val));
            }
            return keys;
        }

        internal static string RowKey(string code, string org, List<(string By, string Set, string Value)> keys) =>
            code + "|" + (org ?? "") + "|" + string.Join("|", keys.Select(x => x.Value));

        internal static (decimal Dt, decimal Kt) Split(IEnumerable<decimal> nets)
        {
            decimal dt = 0, kt = 0;
            foreach (var n in nets) { if (n > 0) dt += n; else kt -= n; }
            return (dt, kt);
        }

        internal static List<Cell> Cells(JArray rows, bool turnovers)
        {
            var list = new List<Cell>();
            foreach (var r in rows)
            {
                var c = new Cell { Account = (string)r["Account_Key"], Org = (string)r["Организация_Key"] ?? "" };
                for (var i = 0; i < 3; i++) { c.Types[i] = (string)r["ExtDimension" + (i + 1) + "_Type"]; c.Values[i] = (string)r["ExtDimension" + (i + 1)]; }
                if (turnovers)
                {
                    // Свёрнутые остатки: у части версий 1С поля …BalanceDr/Cr со странным знаком, свёрнутое — надёжно
                    c.Open = Od.Dec(r["СуммаOpeningBalance"]); c.Close = Od.Dec(r["СуммаClosingBalance"]);
                    c.TurnDr = Od.Dec(r["СуммаTurnoverDr"]); c.TurnCr = Od.Dec(r["СуммаTurnoverCr"]);
                }
                else c.Close = Od.Dec(r["СуммаBalance"]);
                list.Add(c);
            }
            return list;
        }

        // ---------- Запрос ----------

        public sealed class Filter
        {
            public string Counterparty, Contract, Employee;
            public bool Any => !string.IsNullOrWhiteSpace(Counterparty) || !string.IsNullOrWhiteSpace(Contract) || !string.IsNullOrWhiteSpace(Employee);
        }

        public sealed class TbRequest
        {
            public List<string> Accounts = new List<string>();
            public DateTime? From;
            public DateTime To = DateTime.Today;
            public List<string> By = new List<string>();
            public Filter Filter = new Filter();
            public string Organization;
            public int Top = 20;
            public decimal MinAmount;
            public bool Aging;
        }

        public sealed class TbResult
        {
            public List<(string Code, Acc Acc)> Accounts = new List<(string, Acc)>();
            public List<Row> All = new List<Row>(), Shown = new List<Row>();
            public List<string> Warnings = new List<string>();
            public Dictionary<string, string> OrgNames = new Dictionary<string, string>();
            public Dictionary<string, JToken> Names = new Dictionary<string, JToken>();
            public DateTime WindowFrom;
        }

        public const int AgingYears = 3;

        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        internal static string D(DateTime d) => d.ToString("yyyy-MM-dd", Ci);

        private static string Reg(BaseRuntime b) => "AccountingRegister_" + b.Profile.AccountingRegister;

        private static Func<string, string> Titles(BaseRuntime b)
        {
            var titles = Access.CatalogOf(b.Config).Items.GroupBy(i => ODataClient.SetName(i.FullName)).ToDictionary(g => g.Key, g => g.First().Title);
            return set => set != null && titles.TryGetValue(set, out var t) ? t : null;
        }

        private static IEnumerable<List<string>> Chunks(IEnumerable<string> items, int n)
        {
            var list = items.ToList();
            for (var i = 0; i < list.Count; i += n) yield return list.Skip(i).Take(n).ToList();
        }

        /// <summary>Запрос к регистру с условием по счетам в $filter — частями: длинный $filter 1С и http.sys не любят.</summary>
        private static JArray Query(ODataClient od, string resource, IEnumerable<string> keys, Func<string, string> cond, string extra = null)
        {
            var all = new JArray();
            foreach (var chunk in Chunks(keys, 30))
                foreach (var r in od.Rows(resource, new Dictionary<string, string>
                {
                    ["$filter"] = "(" + string.Join(" or ", chunk.Select(cond)) + ")" + (extra == null ? "" : " and " + extra),
                }))
                    all.Add(r);
            return all;
        }

        /// <summary>Отборы filter → наборы и допустимые ключи; сотрудник при выключенном показе имён — отказ.</summary>
        internal static List<(string[] Sets, HashSet<string> Keys)> Filters(ODataClient od, Filter f, List<string> warnings)
        {
            var res = new List<(string[], HashSet<string>)>();
            if (!string.IsNullOrWhiteSpace(f.Counterparty))
                res.Add((Aliases["counterparty"], new HashSet<string> { ReconciliationSource.Counterparty(od, f.Counterparty).Key }));
            if (!string.IsNullOrWhiteSpace(f.Contract))
            {
                var rows = od.Rows("Catalog_ДоговорыКонтрагентов", new Dictionary<string, string>
                {
                    ["$select"] = "Ref_Key", ["$filter"] = $"substringof({ODataClient.Lit(f.Contract.Trim())}, Description) and DeletionMark eq false",
                });
                if (rows.Count == 0) throw new AgentException(Txt.F("LedgerNoContract", f.Contract));
                res.Add((Aliases["contract"], new HashSet<string>(rows.Select(r => (string)r["Ref_Key"]))));
            }
            if (!string.IsNullOrWhiteSpace(f.Employee))
            {
                var keys = new HashSet<string>();
                string denied = null;
                foreach (var set in Aliases["employee"])
                {
                    var deny = od.Deny?.Invoke(set);
                    if (deny != null) { denied = denied ?? deny; continue; }
                    try
                    {
                        foreach (var r in od.Rows(set, new Dictionary<string, string>
                        {
                            ["$select"] = "Ref_Key", ["$filter"] = $"substringof({ODataClient.Lit(f.Employee.Trim())}, Description)",
                        }))
                            keys.Add((string)r["Ref_Key"]);
                    }
                    catch (AgentException) { }   // набора нет в конфигурации
                }
                if (keys.Count == 0) throw new AgentException(denied != null ? Txt.S("LedgerEmployeeHidden") : Txt.F("LedgerNoEmployee", f.Employee));
                res.Add((Aliases["employee"], keys));
            }
            return res;
        }

        private static string OrgKey(BaseRuntime b, string organization, Dictionary<string, string> names)
        {
            var orgs = b.OData.Rows("Catalog_Организации", new Dictionary<string, string> { ["$select"] = "Ref_Key,Description,ИдентификационныйНомер" });
            foreach (var o in orgs) names[(string)o["Ref_Key"]] = (string)o["Description"];
            if (string.IsNullOrWhiteSpace(organization)) return null;
            var q = organization.Trim();
            var f = orgs.FirstOrDefault(x => (string)x["ИдентификационныйНомер"] == q)
                ?? orgs.FirstOrDefault(x => ((string)x["Description"] ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                ?? throw new AgentException(Txt.F("ActNoOrganization", organization));
            return (string)f["Ref_Key"];
        }

        /// <summary>Сотрудники скрыты (показ имён выключен в базе и справочник не открыт на «Доступе»)?</summary>
        internal static HashSet<string> HiddenSets(ODataClient od) =>
            new HashSet<string>(Aliases["employee"].Where(s => od.Deny != null && od.Deny(s) != null));

        /// <summary>Строки ОСВ без отбора по сумме и имён: для встречных сальдо (свой accByKey — сторона пары вместо кода счёта).</summary>
        internal static List<Row> Rows(BaseRuntime b, TbRequest q, Dictionary<string, Acc> accByKey, out Dictionary<string, string> orgNames)
        {
            var od = b.OData;
            orgNames = new Dictionary<string, string>();
            var orgKey = OrgKey(b, q.Organization, orgNames);
            var filters = Filters(od, q.Filter, new List<string>());
            var end = q.To.Date.AddDays(1).AddSeconds(-1);
            var cells = Cells(Query(od, $"{Reg(b)}/Balance(Period=datetime'{Od.D(end)}')", accByKey.Keys, k => $"Account_Key eq guid'{k}'"), false)
                .Where(c => orgKey == null || c.Org == orgKey)
                .Where(c => Pass(c.Types, c.Values, filters));
            return Aggregate(cells, accByKey, q.By, Titles(b), null);
        }

        internal static string DateRu(DateTime d) => d.ToString("dd.MM.yyyy", Ci);

        public static TbResult TrialBalance(BaseRuntime b, TbRequest q)
        {
            var od = b.OData;
            if (q.Accounts.Count == 0) throw new AgentException(Txt.S("LedgerNeedAccount"));
            if (q.Accounts.Count > MaxAccounts) throw new AgentException(Txt.F("LedgerTooManyAccounts", MaxAccounts));
            if (q.By.Count > MaxBy) throw new AgentException(Txt.F("LedgerTooManyBy", MaxBy));
            if (q.From.HasValue && q.From.Value.Date > q.To.Date) throw new AgentException(Txt.S("LedgerBadPeriod"));
            var res = new TbResult();
            var chart = Chart(b);
            var keys = new HashSet<string>();
            foreach (var code in q.Accounts)
            {
                var (top, k) = Resolve(chart, code);
                res.Accounts.Add((NormCode(code), top));
                keys.UnionWith(k);
            }
            var accByKey = chart.Where(a => keys.Contains(a.Key)).GroupBy(a => a.Key).ToDictionary(g => g.Key, g => g.First());
            var orgKey = OrgKey(b, q.Organization, res.OrgNames);
            var filters = Filters(od, q.Filter, res.Warnings);
            var hidden = HiddenSets(od);
            var titleOf = Titles(b);

            var end = q.To.Date.AddDays(1).AddSeconds(-1);
            var resource = q.From.HasValue
                ? $"{Reg(b)}/BalanceAndTurnovers(StartPeriod=datetime'{Od.D(q.From.Value.Date)}',EndPeriod=datetime'{Od.D(end)}')"
                : $"{Reg(b)}/Balance(Period=datetime'{Od.D(end)}')";
            var cells = Cells(Query(od, resource, keys, k => $"Account_Key eq guid'{k}'"), q.From.HasValue)
                .Where(c => orgKey == null || c.Org == orgKey)
                .Where(c => Pass(c.Types, c.Values, filters));
            var rows = Aggregate(cells, accByKey, q.By, titleOf, hidden);

            foreach (var t in q.By)
                if (!Aliases.ContainsKey(t) && rows.All(r => r.Keys.First(x => x.By == t).Set == null))
                    res.Warnings.Add(Txt.F("LedgerByUnknown", t));
            if (rows.Any(r => r.Keys.Any(x => x.Set != null && hidden.Contains(x.Set))))
                res.Warnings.Add(Txt.S("LedgerEmployeeHidden"));

            res.All = rows.Where(r => r.Size >= q.MinAmount)
                .OrderByDescending(r => Math.Abs(r.Close)).ThenByDescending(r => r.Size).ThenBy(r => r.Account, StringComparer.Ordinal).ToList();
            res.Shown = res.All.Take(Math.Max(1, q.Top)).ToList();
            if (q.Aging && res.All.Count > 0)
            {
                // Окно истории: 3 года; не хватило — с начала учёта (2000 г.)
                res.WindowFrom = q.To.Date.AddYears(-AgingYears);
                List<Entry> History(DateTime from)
                {
                    var resource = $"{Reg(b)}/RecordsWithExtDimensions(StartPeriod=datetime'{Od.D(from)}',EndPeriod=datetime'{Od.D(end)}')";
                    var recs = new JArray();
                    foreach (var r in Query(od, resource, keys, k => $"AccountDr_Key eq guid'{k}'")) recs.Add(r);
                    foreach (var r in Query(od, resource, keys, k => $"AccountCr_Key eq guid'{k}'"))
                        if (!keys.Contains((string)r["AccountDr_Key"] ?? "")) recs.Add(r);
                    return Pick(recs, keys, "both", orgKey, filters);
                }
                if (!Age(res.All, History(res.WindowFrom), accByKey, q.By, titleOf, hidden, q.To))
                {
                    res.WindowFrom = new DateTime(2000, 1, 1);
                    Age(res.All, History(res.WindowFrom), accByKey, q.By, titleOf, hidden, q.To);
                }
                FillNumbers(od, res.Shown.Select(r => r.Oldest));
            }
            res.Names = Describe(b, res.Shown.SelectMany(r => r.Keys).Where(x => x.Value != null).Select(x => (x.Set, x.Value)), hidden);
            return res;
        }

        /// <summary>Имена значений субконто (только показанные): справочник — наименование (контрагент — с БИН), документ — вид, № и дата.</summary>
        internal static Dictionary<string, JToken> Describe(BaseRuntime b, IEnumerable<(string Set, string Value)> values, ISet<string> hidden)
        {
            var od = b.OData;
            var names = new Dictionary<string, JToken>();
            var titleOf = Titles(b);
            foreach (var g in values.Where(v => v.Set != null && v.Value != null).Distinct().GroupBy(v => v.Set))
            {
                var set = g.Key;
                if (hidden.Contains(set)) continue;
                var isGuid = g.All(v => Guid.TryParse(v.Value, out _));
                if (!isGuid || od.Deny?.Invoke(set) != null)
                {
                    foreach (var v in g) names[set + "|" + v.Value] = isGuid ? null : v.Value;
                    continue;
                }
                var cp = set == "Catalog_Контрагенты";
                var doc = set.StartsWith("Document_", StringComparison.Ordinal);
                var select = cp ? "Ref_Key,Description,ИдентификационныйКодЛичности,ЮрФизЛицо,ИндивидуальныйПредпринимательАдвокатЧастныйНотариус"
                    : doc ? "Ref_Key,Number,Date" : "Ref_Key,Description";
                foreach (var chunk in Chunks(g.Select(v => v.Value), 25))
                {
                    JArray rows;
                    try { rows = od.Rows(set, new Dictionary<string, string> { ["$select"] = select, ["$filter"] = string.Join(" or ", chunk.Select(k => $"Ref_Key eq guid'{k}'")) }); }
                    catch (AgentException) { continue; }
                    foreach (var r in rows)
                    {
                        JToken n;
                        if (cp)
                        {
                            var person = (string)r["ЮрФизЛицо"] == "ФизЛицо" && (bool?)r["ИндивидуальныйПредпринимательАдвокатЧастныйНотариус"] != true;
                            var bin = person ? null : Od.NotEmpty((string)r["ИдентификационныйКодЛичности"]);
                            n = new JObject { ["name"] = (string)r["Description"], ["bin"] = bin == null ? JValue.CreateNull() : (JToken)bin };
                        }
                        else if (doc)
                            n = ReconciliationSource.DocTitle(set, InvoiceSource.PrintNumber((string)r["Number"]), titleOf).Doc
                                + (Od.HasDate(r["Date"]) ? " от " + ((DateTime)r["Date"]).ToString("dd.MM.yyyy", Ci) : "");
                        else n = (string)r["Description"];
                        names[set + "|" + (string)r["Ref_Key"]] = n;
                    }
                }
            }
            return names;
        }

        private static JObject Pair(decimal dt, decimal kt) => new JObject { ["dt"] = Tools.Money((double)dt), ["kt"] = Tools.Money((double)kt) };
        private static JObject Pair((decimal Dt, decimal Kt) p) => Pair(p.Dt, p.Kt);

        private static JToken Name(Dictionary<string, JToken> names, string set, string value, ISet<string> hidden)
        {
            if (value == null) return set != null && hidden != null && hidden.Contains(set) ? (JToken)Txt.S("LedgerHiddenName") : JValue.CreateNull();
            return names.TryGetValue(set + "|" + value, out var n) && n != null ? n.DeepClone() : (JToken)value;
        }

        private static JObject AgingJson(decimal[] a) => new JObject
        {
            ["d0_30"] = Tools.Money((double)a[0]), ["d31_90"] = Tools.Money((double)a[1]),
            ["d91_180"] = Tools.Money((double)a[2]), ["d181_plus"] = Tools.Money((double)a[3]),
        };

        private static JObject Totals(IList<Row> rows, bool turnovers)
        {
            var o = new JObject();
            if (turnovers)
            {
                o["opening"] = Pair(Split(rows.Select(r => r.Open)));
                o["turnover"] = Pair(rows.Sum(r => r.TurnDr), rows.Sum(r => r.TurnCr));
            }
            o["closing"] = Pair(Split(rows.Select(r => r.Close)));
            o["net"] = Tools.Money((double)rows.Sum(r => r.Close));
            return o;
        }

        internal static JObject TbToJson(BaseRuntime b, TbRequest q, TbResult r, JObject asOf, ISet<string> hidden)
        {
            var turnovers = q.From.HasValue;
            var multiOrg = r.All.Select(x => x.Org).Distinct().Count() > 1;
            var rows = new JArray();
            foreach (var x in r.Shown)
            {
                var o = new JObject { ["account"] = x.Account };
                if (multiOrg) o["organization"] = r.OrgNames.TryGetValue(x.Org, out var on) ? on : x.Org;
                if (x.Keys.Count > 0)
                {
                    var k = new JObject();
                    foreach (var key in x.Keys) k[key.By] = Name(r.Names, key.Set, key.Value, hidden);
                    o["keys"] = k;
                }
                if (turnovers)
                {
                    o["opening"] = Pair(Split(new[] { x.Open }));
                    o["turnover"] = Pair(x.TurnDr, x.TurnCr);
                }
                o["closing"] = Pair(Split(new[] { x.Close }));
                o["net"] = Tools.Money((double)x.Close);
                if (q.Aging && x.Aging != null)
                {
                    o["aging"] = AgingJson(x.Aging);
                    var titleOf = Titles(b);
                    if (x.Older > 0)
                        o["oldest"] = new JObject
                        {
                            ["date"] = JValue.CreateNull(), ["note"] = Txt.F("LedgerOlderThan", r.WindowFrom.ToString("dd.MM.yyyy", Ci)),
                            ["days"] = Math.Max(0, (int)(q.To.Date - r.WindowFrom).TotalDays),
                        };
                    else if (x.Oldest != null)
                        o["oldest"] = new JObject
                        {
                            ["date"] = D(x.Oldest.Date), ["days"] = Math.Max(0, (int)(q.To.Date - x.Oldest.Date).TotalDays),
                            ["document"] = new JObject
                            {
                                ["type"] = x.Oldest.RecorderType == null ? Txt.S("LedgerDocUnknown") : ReconciliationSource.DocTitle(x.Oldest.RecorderType, null, titleOf).Doc,
                                ["number"] = x.Oldest.Number == null ? JValue.CreateNull() : (JToken)x.Oldest.Number,
                                ["date"] = x.Oldest.DocDate.HasValue ? (JToken)D(x.Oldest.DocDate.Value) : JValue.CreateNull(),
                            },
                        };
                }
                rows.Add(o);
            }
            var chart = Chart(b);
            var res = new JObject
            {
                ["base"] = b.Config.Slug,
                ["to"] = D(q.To),
                ["accounts"] = new JArray(r.Accounts.Select(a => a.Acc != null
                    ? new JObject { ["code"] = a.Acc.Code, ["name"] = a.Acc.Name, ["kind"] = a.Acc.Kind }
                    : new JObject { ["code"] = a.Code, ["name"] = Txt.F("LedgerGroup", a.Code), ["kind"] = "mixed" })),
                ["by"] = new JArray(q.By),
                ["rows"] = rows,
                ["count"] = r.All.Count,
                ["shown"] = r.Shown.Count,
                ["total"] = Totals(r.All, turnovers),
                ["currency"] = "KZT",
                ["note"] = Txt.S(turnovers ? "LedgerTbNote" : "LedgerTbNoteBalance"),
            };
            if (turnovers) res["from"] = D(q.From.Value);
            if (q.Aging)
            {
                var sum = new decimal[4];
                foreach (var x in r.All.Where(x => x.Aging != null)) for (var i = 0; i < 4; i++) sum[i] += x.Aging[i];
                res["aging_total"] = AgingJson(sum);
                res["aging_method"] = "fifo";
            }
            var rest = r.All.Skip(r.Shown.Count).ToList();
            if (rest.Count > 0)
            {
                var o = Totals(rest, turnovers);
                o.AddFirst(new JProperty("count", rest.Count));
                res["rest"] = o;
                res["more"] = Txt.F("LedgerMore", rest.Count, Tools.Money((double)rest.Sum(x => Math.Abs(x.Close))));
            }
            if (r.Warnings.Count > 0) res["warnings"] = new JArray(r.Warnings.Distinct());
            res["as_of"] = asOf;
            return res;
        }

        // ---------- account_entries ----------

        public sealed class EntriesRequest
        {
            public string Account;
            public DateTime From, To;
            public Filter Filter = new Filter();
            public string Organization;
            /// <summary>dt — только проводки по дебету счёта, kt — по кредиту, both — все.</summary>
            public string Side = "both";
            public int Top = 50;
        }

        public sealed class Entry
        {
            public DateTime Date;
            public string Recorder, RecorderType, Content, Org;
            public int Line;
            public decimal Amount;
            public string DrAccount, CrAccount;
            public string[] DrTypes = new string[3], DrValues = new string[3], CrTypes = new string[3], CrValues = new string[3];
            /// <summary>Проводка попала в отбор по дебету / кредиту нашего счёта.</summary>
            public bool Dt, Kt;
            public string Number;
            public DateTime? DocDate;
        }

        /// <summary>Проводки по нашему счёту из записей регистра: сторона (dt/kt), отбор по субконто нашей стороны, организация.</summary>
        internal static List<Entry> Pick(JArray records, HashSet<string> keys, string side, string orgKey, List<(string[] Sets, HashSet<string> Keys)> filters)
        {
            var list = new List<Entry>();
            foreach (var r in records)
            {
                if ((bool?)r["Active"] == false) continue;
                var e = new Entry
                {
                    Date = (DateTime)r["Period"], Recorder = (string)r["Recorder"], RecorderType = SetOf((string)r["Recorder_Type"]),
                    Line = (int?)r["LineNumber"] ?? 0, Amount = Od.Dec(r["Сумма"]), Content = Od.NotEmpty((string)r["Содержание"]),
                    DrAccount = (string)r["AccountDr_Key"], CrAccount = (string)r["AccountCr_Key"], Org = (string)r["Организация_Key"] ?? "",
                };
                if (orgKey != null && e.Org != orgKey) continue;
                for (var i = 0; i < 3; i++)
                {
                    e.DrTypes[i] = (string)r["ExtDimensionDr" + (i + 1) + "_Type"];
                    e.DrValues[i] = (string)r["ExtDimensionDr" + (i + 1)];
                    e.CrTypes[i] = (string)r["ExtDimensionCr" + (i + 1) + "_Type"];
                    e.CrValues[i] = (string)r["ExtDimensionCr" + (i + 1)];
                }
                e.Dt = side != "kt" && keys.Contains(e.DrAccount ?? "") && Pass(e.DrTypes, e.DrValues, filters);
                e.Kt = side != "dt" && keys.Contains(e.CrAccount ?? "") && Pass(e.CrTypes, e.CrValues, filters);
                if (e.Dt || e.Kt) list.Add(e);
            }
            return list.OrderBy(x => x.Date).ThenBy(x => x.Recorder, StringComparer.Ordinal).ThenBy(x => x.Line).ToList();
        }

        /// <summary>Номер и дата документа-регистратора — только у видов, включённых на «Доступе».</summary>
        internal static void FillNumbers(ODataClient od, IEnumerable<Entry> list)
        {
            foreach (var g in list.Where(x => x != null).GroupBy(x => x.RecorderType))
            {
                if (g.Key == null || od.Deny?.Invoke(g.Key) != null) continue;
                foreach (var chunk in Chunks(g.Select(x => x.Recorder).Distinct(), 25))
                {
                    JArray rows;
                    try { rows = od.Rows(g.Key, new Dictionary<string, string> { ["$select"] = "Ref_Key,Number,Date", ["$filter"] = string.Join(" or ", chunk.Select(k => $"Ref_Key eq guid'{k}'")) }); }
                    catch (AgentException) { continue; }
                    foreach (var r in rows)
                        foreach (var x in g.Where(x => x.Recorder == (string)r["Ref_Key"]))
                        {
                            x.Number = InvoiceSource.PrintNumber((string)r["Number"]);
                            x.DocDate = Od.HasDate(r["Date"]) ? ((DateTime)r["Date"]).Date : (DateTime?)null;
                        }
                }
            }
        }

        // ---------- Возраст сальдо (aging, #93) ----------

        /// <summary>Корзины возраста: до 30, 31–90, 91–180, больше 180 дней.</summary>
        internal static int Bucket(int days) => days <= 30 ? 0 : days <= 90 ? 1 : days <= 180 ? 2 : 3;

        /// <summary>
        /// FIFO по строкам ОСВ: для дебетового сальдо «выдачи» — суммы по дебету строки, кредит гасит самые старые; для кредитового — зеркально.
        /// Остаток сальдо приходится на последние увеличения (от позднего к раннему, пока не покроют |сальдо|). Перенос внутри той же
        /// строки (Дт и Кт — один счёт и те же субконто) новым долгом не считается. Возвращает false, если окна истории не хватило.
        /// </summary>
        internal static bool Age(List<Row> rows, IEnumerable<Entry> entries, Dictionary<string, Acc> accByKey, IList<string> by, Func<string, string> titleOf, ISet<string> hidden, DateTime asOf)
        {
            var inc = new Dictionary<string, Dictionary<string, Entry>>();
            string KeyOf(string acc, string[] types, string[] values, string org) =>
                acc != null && accByKey.TryGetValue(acc, out var a) ? RowKey(a.Code, org, KeysOf(types, values, by, titleOf, hidden)) : null;
            var sign = rows.ToDictionary(r => RowKey(r.Account, r.Org, r.Keys), r => Math.Sign(r.Close));
            foreach (var e in entries)
            {
                if (e.Amount <= 0) continue;   // сторно уменьшают сальдо — их учитывает остаток, не возраст
                var dr = KeyOf(e.DrAccount, e.DrTypes, e.DrValues, e.Org);
                var cr = KeyOf(e.CrAccount, e.CrTypes, e.CrValues, e.Org);
                if (dr != null && dr == cr) continue;
                foreach (var (k, s) in new[] { (dr, 1), (cr, -1) })
                {
                    if (k == null || !sign.TryGetValue(k, out var sg) || sg != s) continue;
                    if (!inc.TryGetValue(k, out var docs)) inc[k] = docs = new Dictionary<string, Entry>();
                    if (!docs.TryGetValue(e.Recorder ?? "", out var d))
                        docs[e.Recorder ?? ""] = d = new Entry { Recorder = e.Recorder, RecorderType = e.RecorderType, Date = e.Date.Date };
                    d.Amount += e.Amount;
                }
            }
            var enough = true;
            foreach (var r in rows)
            {
                r.Aging = new decimal[4]; r.Oldest = null; r.Older = 0;
                var left = Math.Abs(r.Close);
                if (left == 0) continue;
                if (inc.TryGetValue(RowKey(r.Account, r.Org, r.Keys), out var docs))
                    foreach (var d in docs.Values.OrderByDescending(x => x.Date).ThenByDescending(x => x.Recorder, StringComparer.Ordinal))
                    {
                        if (left <= 0) break;
                        var part = Math.Min(d.Amount, left);
                        r.Aging[Bucket(Math.Max(0, (int)(asOf.Date - d.Date).TotalDays))] += part;
                        left -= part;
                        r.Oldest = d;
                    }
                if (left > 0) { r.Older = left; r.Aging[3] += left; enough = false; }
            }
            return enough;
        }

        public sealed class EntriesResult
        {
            public Acc Acc;
            public List<Entry> All = new List<Entry>(), Shown = new List<Entry>();
            public decimal Open, Close;
            public List<string> Warnings = new List<string>();
            public Dictionary<string, JToken> Names = new Dictionary<string, JToken>();
            public Dictionary<string, Acc> Chart;
            public HashSet<string> Hidden;
        }

        public static EntriesResult Entries(BaseRuntime b, EntriesRequest q)
        {
            var od = b.OData;
            if (string.IsNullOrWhiteSpace(q.Account)) throw new AgentException(Txt.S("LedgerNeedAccount"));
            if (q.From.Date > q.To.Date) throw new AgentException(Txt.S("LedgerBadPeriod"));
            var chart = Chart(b);
            var (top, keys) = Resolve(chart, q.Account);
            var res = new EntriesResult { Acc = top, Chart = chart.Where(a => a.Key != null).GroupBy(a => a.Key).ToDictionary(g => g.Key, g => g.First()), Hidden = HiddenSets(od) };
            var orgs = new Dictionary<string, string>();
            var orgKey = OrgKey(b, q.Organization, orgs);
            var filters = Filters(od, q.Filter, res.Warnings);
            var end = q.To.Date.AddDays(1).AddSeconds(-1);

            // Сальдо на начало и конец — с тем же отбором (без разреза), свёрнутое
            var bt = Cells(Query(od, $"{Reg(b)}/BalanceAndTurnovers(StartPeriod=datetime'{Od.D(q.From.Date)}',EndPeriod=datetime'{Od.D(end)}')", keys, k => $"Account_Key eq guid'{k}'"), true)
                .Where(c => orgKey == null || c.Org == orgKey).Where(c => Pass(c.Types, c.Values, filters)).ToList();
            res.Open = bt.Sum(c => c.Open); res.Close = bt.Sum(c => c.Close);

            var side = q.Side == "dt" || q.Side == "kt" ? q.Side : "both";
            var resource = $"{Reg(b)}/RecordsWithExtDimensions(StartPeriod=datetime'{Od.D(q.From.Date)}',EndPeriod=datetime'{Od.D(end)}')";
            var records = new JArray();
            if (side != "kt") foreach (var r in Query(od, resource, keys, k => $"AccountDr_Key eq guid'{k}'")) records.Add(r);
            if (side != "dt")
                foreach (var r in Query(od, resource, keys, k => $"AccountCr_Key eq guid'{k}'"))
                    // проводка внутри счёта (Дт и Кт — наш счёт) пришла уже по дебету
                    if (side == "kt" || !keys.Contains((string)r["AccountDr_Key"] ?? "")) records.Add(r);
            res.All = Pick(records, keys, side, orgKey, filters);
            // Не влезли — показываем последние: по ним чаще вопрос («что было в конце месяца»); раньше — одной строкой rest
            res.Shown = res.All.Skip(Math.Max(0, res.All.Count - Math.Max(1, q.Top))).ToList();

            FillNumbers(od, res.Shown);
            IEnumerable<(string, string)> Vals(Entry e) =>
                Enumerable.Range(0, 3).Select(i => (SetOf(e.DrTypes[i]), e.DrValues[i])).Concat(Enumerable.Range(0, 3).Select(i => (SetOf(e.CrTypes[i]), e.CrValues[i])))
                    .Where(v => v.Item1 != null && !Od.IsEmpty(v.Item2));
            res.Names = Describe(b, res.Shown.SelectMany(Vals), res.Hidden);
            if (res.Shown.Any(e => Vals(e).Any(v => res.Hidden.Contains(v.Item1)))) res.Warnings.Add(Txt.S("LedgerEmployeeHidden"));
            return res;
        }

        private static JObject Side(EntriesResult r, string acc, string[] types, string[] values)
        {
            var o = new JObject { ["account"] = acc != null && r.Chart.TryGetValue(acc, out var a) ? a.Code : null };
            var k = new JObject();
            for (var i = 0; i < 3; i++)
            {
                var set = SetOf(types[i]);
                if (set == null) continue;
                var val = Od.IsEmpty(values[i]) ? null : values[i];
                var name = Alias(set);
                while (k[name] != null) name += "_" + (i + 1);
                k[name] = r.Hidden.Contains(set) ? (JToken)Txt.S("LedgerHiddenName") : Name(r.Names, set, val, r.Hidden);
            }
            if (k.Count > 0) o["keys"] = k;
            return o;
        }

        internal static JObject EntriesToJson(BaseRuntime b, EntriesRequest q, EntriesResult r, JObject asOf)
        {
            var titleOf = Titles(b);
            var entries = new JArray(r.Shown.Select(e => new JObject
            {
                ["date"] = D(e.Date),
                ["document"] = new JObject
                {
                    ["type"] = e.RecorderType == null ? Txt.S("LedgerDocUnknown") : ReconciliationSource.DocTitle(e.RecorderType, null, titleOf).Doc,
                    ["number"] = e.Number == null ? JValue.CreateNull() : (JToken)e.Number,
                    ["date"] = e.DocDate.HasValue ? (JToken)D(e.DocDate.Value) : JValue.CreateNull(),
                },
                ["dt"] = Side(r, e.DrAccount, e.DrTypes, e.DrValues),
                ["kt"] = Side(r, e.CrAccount, e.CrTypes, e.CrValues),
                ["amount"] = Tools.Money((double)e.Amount),
                ["content"] = e.Content == null ? JValue.CreateNull() : (JToken)e.Content,
            }));
            decimal Dt(IEnumerable<Entry> xs) => xs.Where(x => x.Dt).Sum(x => x.Amount);
            decimal Kt(IEnumerable<Entry> xs) => xs.Where(x => x.Kt).Sum(x => x.Amount);
            var res = new JObject
            {
                ["base"] = b.Config.Slug,
                ["account"] = r.Acc != null ? new JObject { ["code"] = r.Acc.Code, ["name"] = r.Acc.Name, ["kind"] = r.Acc.Kind } : new JObject { ["code"] = NormCode(q.Account), ["name"] = Txt.F("LedgerGroup", NormCode(q.Account)), ["kind"] = "mixed" },
                ["from"] = D(q.From), ["to"] = D(q.To), ["side"] = q.Side ?? "both",
                ["opening"] = Pair(Split(new[] { r.Open })),
                ["closing"] = Pair(Split(new[] { r.Close })),
                ["net"] = Tools.Money((double)r.Close),
                ["entries"] = entries,
                ["count"] = r.All.Count,
                ["shown"] = r.Shown.Count,
                ["totals"] = Pair(Dt(r.All), Kt(r.All)),
                ["currency"] = "KZT",
                ["note"] = Txt.S("LedgerEntriesNote"),
            };
            var rest = r.All.Take(r.All.Count - r.Shown.Count).ToList();
            if (rest.Count > 0)
            {
                res["rest"] = new JObject { ["count"] = rest.Count, ["dt"] = Tools.Money((double)Dt(rest)), ["kt"] = Tools.Money((double)Kt(rest)) };
                res["more"] = Txt.F("LedgerEntriesMore", rest.Count, D(rest.First().Date), D(rest.Last().Date));
            }
            if (r.Warnings.Count > 0) res["warnings"] = new JArray(r.Warnings.Distinct());
            res["as_of"] = asOf;
            return res;
        }
    }
}
