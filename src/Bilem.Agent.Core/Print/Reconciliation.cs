using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent.Core.Print
{
    public sealed class ActRow
    {
        public DateTime Date { get; set; }
        public string Recorder { get; set; }
        /// <summary>«Реализация № 41», «Оплата (ПП № 87)».</summary>
        public string Doc { get; set; }
        /// <summary>Как документ выглядит у контрагента (зеркальная половина): «Поступление № 41».</summary>
        public string MirrorDoc { get; set; }
        public decimal Dr { get; set; }
        public decimal Cr { get; set; }
    }

    /// <summary>Расчёты по одному договору (или по всем сразу, если без разбивки).</summary>
    public sealed class ActSection
    {
        public string ContractKey { get; set; }
        public string Contract { get; set; }
        public decimal Open { get; set; }
        public List<ActRow> Rows { get; } = new List<ActRow>();
        public decimal TurnDr => Rows.Sum(r => r.Dr);
        public decimal TurnCr => Rows.Sum(r => r.Cr);
        public decimal Close => Open + TurnDr - TurnCr;
    }

    /// <summary>Акт сверки взаимных расчётов: наши данные из 1С (регистр бухгалтерии, проведённые документы). Сальдо &gt; 0 — долг контрагента нам.</summary>
    public sealed class ActData
    {
        public OrgHeader Org { get; set; }
        public string CounterpartyKey { get; set; }
        public string Counterparty { get; set; }
        public string CounterpartyShort { get; set; }
        public string CounterpartyBin { get; set; }
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        /// <summary>Договор, если акт по одному договору (параметр contract или договор у контрагента один).</summary>
        public string Contract { get; set; }
        public bool ByContracts { get; set; }
        public List<ActSection> Sections { get; } = new List<ActSection>();
        public decimal Open => Sections.Sum(s => s.Open);
        public decimal TurnDr => Sections.Sum(s => s.TurnDr);
        public decimal TurnCr => Sections.Sum(s => s.TurnCr);
        public decimal Close => Sections.Sum(s => s.Close);
        public int RowCount => Sections.Sum(s => s.Rows.Count);
    }

    /// <summary>
    /// Акт сверки по регистру бухгалтерии — как типовой «Акт сверки» 1С: счета расчётов (список типового документа), субконто 1 — контрагент,
    /// субконто 2 — договор; проводки между счетами расчётов одного контрагента (зачёт аванса) в акт не идут. Только чтение.
    /// </summary>
    public static class ReconciliationSource
    {
        /// <summary>Счета расчётов с контрагентами — список «Акта сверки» БП для Казахстана 3.0 (с субсчетами).</summary>
        public static readonly string[] SettlementAccounts =
        {
            "1210", "1220", "1230", "1240", "1260", "1151", "1271", "1272", "1274", "1710", "2110", "2120", "2130", "2140", "2160", "2050",
            "2171", "2172", "2174", "2210", "2910", "3040", "3310", "3320", "3330", "3340", "3360", "3370", "3050", "3381", "3382", "3383",
            "3387", "3410", "3420", "3510", "3530", "3560", "4110", "4120", "4130", "4140", "4150", "4050", "4161", "4162", "4163", "4167",
            "4210", "4220", "4410", "2010", "4010", "3010", "1110", "3060",
        };

        private const string CpType = "StandardODATA.Catalog_Контрагенты";
        private const string ContractType = "StandardODATA.Catalog_ДоговорыКонтрагентов";

        /// <summary>Период по умолчанию по настройке: прошлый месяц, прошлый квартал или с начала года.</summary>
        public static (DateTime From, DateTime To) DefaultPeriod(string kind, DateTime today)
        {
            today = today.Date;
            switch ((kind ?? "quarter").ToLowerInvariant())
            {
                case "month":
                    var m = new DateTime(today.Year, today.Month, 1).AddMonths(-1);
                    return (m, m.AddMonths(1).AddDays(-1));
                case "year":
                    return (new DateTime(today.Year, 1, 1), today);
                default:
                    var q = new DateTime(today.Year, (today.Month - 1) / 3 * 3 + 1, 1).AddMonths(-3);
                    return (q, q.AddMonths(3).AddDays(-1));
            }
        }

        /// <summary>Ключи счетов расчётов (с субсчетами) по плану счетов.</summary>
        internal static HashSet<string> AccountKeys(JArray chart)
        {
            var byKey = chart.OfType<JObject>().ToDictionary(a => (string)a["Ref_Key"], a => a);
            var list = new HashSet<string>(SettlementAccounts);
            var res = new HashSet<string>();
            foreach (var a in byKey.Values)
            {
                for (JObject x = a; x != null; x = byKey.TryGetValue((string)x["Parent_Key"] ?? "", out var p) ? p : null)
                    if (list.Contains((string)x["Code"])) { res.Add((string)a["Ref_Key"]); break; }
            }
            return res;
        }

        /// <summary>
        /// Строки акта из записей регистра (RecordsWithExtDimensions) и остатков на начало: по документам, по договорам (byContracts).
        /// Проводка между счетами расчётов того же контрагента (и того же договора при разбивке) — внутренняя, пропускаем.
        /// </summary>
        internal static List<ActSection> Build(string cp, string orgKey, string onlyContract, bool byContracts, HashSet<string> accounts, JArray balance, JArray records)
        {
            var sections = new Dictionary<string, ActSection>();
            ActSection Sec(string contract)
            {
                var k = byContracts ? (contract ?? "") : "";
                if (!sections.TryGetValue(k, out var s)) sections[k] = s = new ActSection { ContractKey = byContracts ? contract : null };
                return s;
            }
            bool OurOrg(JToken r) => orgKey == null || (string)r["Организация_Key"] == orgKey;
            string Contract(JToken r, string side) => (string)r["ExtDimension" + side + "2_Type"] == ContractType ? (string)r["ExtDimension" + side + "2"] : null;
            bool Wanted(string contract) => onlyContract == null || contract == onlyContract;

            foreach (var b in balance.Where(OurOrg))
            {
                if (!accounts.Contains((string)b["Account_Key"]) || (string)b["ExtDimension1"] != cp) continue;
                var contract = (string)b["ExtDimension2_Type"] == ContractType ? (string)b["ExtDimension2"] : null;
                if (!Wanted(contract)) continue;
                Sec(contract).Open += Od.Dec(b["СуммаBalance"]);
            }
            var rows = new Dictionary<string, ActRow>();
            foreach (var r in records.Where(OurOrg))
            {
                if ((bool?)r["Active"] == false) continue;
                var dr = accounts.Contains((string)r["AccountDr_Key"]) && (string)r["ExtDimensionDr1_Type"] == CpType && (string)r["ExtDimensionDr1"] == cp;
                var cr = accounts.Contains((string)r["AccountCr_Key"]) && (string)r["ExtDimensionCr1_Type"] == CpType && (string)r["ExtDimensionCr1"] == cp;
                if (!dr && !cr) continue;
                var cd = Contract(r, "Dr"); var cc = Contract(r, "Cr");
                if (dr && cr && (!byContracts || cd == cc)) continue;
                var sum = Od.Dec(r["Сумма"]);
                void Add(string contract, bool debit)
                {
                    if (!Wanted(contract)) return;
                    var s = Sec(contract);
                    var key = (byContracts ? contract : "") + "|" + (string)r["Recorder"];
                    if (!rows.TryGetValue(key, out var row))
                    {
                        rows[key] = row = new ActRow { Date = ((DateTime)r["Period"]).Date, Recorder = (string)r["Recorder"], Doc = (string)r["Recorder_Type"] };
                        s.Rows.Add(row);
                    }
                    if (debit) row.Dr += sum; else row.Cr += sum;
                }
                if (dr) Add(cd, true);
                if (cr) Add(cc, false);
            }
            foreach (var s in sections.Values) s.Rows.Sort((a, b) => a.Date != b.Date ? a.Date.CompareTo(b.Date) : 0);
            return sections.Values.Where(s => s.Open != 0 || s.Rows.Count > 0 || sections.Count == 1).OrderBy(s => s.ContractKey ?? "").ToList();
        }

        /// <summary>Документ строки: короткое имя по виду + номер, если документ включён на «Доступе»; зеркало — как у контрагента.</summary>
        internal static (string Doc, string Mirror) DocTitle(string type, string number, Func<string, string> titleOfSet)
        {
            var set = (type ?? "").Replace("StandardODATA.", "");
            var n = string.IsNullOrEmpty(number) ? "" : " № " + number;
            switch (set)
            {
                case "Document_РеализацияТоваровУслуг": return ("Реализация" + n, "Поступление" + n);
                case "Document_ПоступлениеТоваровУслуг": return ("Поступление" + n, "Реализация" + n);
                case "Document_ВозвратТоваровОтПокупателя": return ("Возврат от покупателя" + n, "Возврат поставщику" + n);
                case "Document_ВозвратТоваровПоставщику": return ("Возврат поставщику" + n, "Возврат от покупателя" + n);
                case "Document_ПлатежноеПоручениеВходящее": case "Document_ПлатежноеПоручениеИсходящее":
                    return ($"Оплата (ПП{n})", $"Оплата (ПП{n})");
                case "Document_ПлатежныйОрдерПоступлениеДенежныхСредств": case "Document_ПлатежныйОрдерСписаниеДенежныхСредств":
                    return ($"Оплата (ПО{n})", $"Оплата (ПО{n})");
                case "Document_ПриходныйКассовыйОрдер": return ($"Оплата (ПКО{n})", $"Оплата (РКО{n})");
                case "Document_РасходныйКассовыйОрдер": return ($"Оплата (РКО{n})", $"Оплата (ПКО{n})");
                case "Document_ОплатаОтПокупателяПлатежнойКартой": return ($"Оплата картой{n}", $"Оплата картой{n}");
                case "Document_КорректировкаДолга": return ("Корректировка долга" + n, "Корректировка долга" + n);
                default:
                    var t = titleOfSet?.Invoke(set) ?? set.Replace("Document_", "");
                    return (t + n, t + n);
            }
        }

        /// <summary>Контрагент по ключу, БИН/ИИН или части названия — ровно один, иначе отказ со списком.</summary>
        public static (string Key, string Name, string Bin) Counterparty(ODataClient od, string q)
        {
            var found = InvoiceSource.Buyers(od, (q ?? "").Trim());
            if (found.Count == 0) throw new AgentException(Txt.F("ActNoCounterparty", q));
            if (found.Count > 1)
            {
                var exact = found.Where(kv => kv.Value.Bin == q.Trim() || string.Equals(kv.Value.Name, q.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
                if (exact.Count == 1) return (exact[0].Key, exact[0].Value.Name, exact[0].Value.Bin);
                throw new AgentException(Txt.F("ActManyCounterparties", q, string.Join("; ", found.Values.Take(10).Select(v => v.Name + (string.IsNullOrEmpty(v.Bin) ? "" : " (" + v.Bin + ")")))));
            }
            var f = found.First();
            return (f.Key, f.Value.Name, f.Value.Bin);
        }

        public static ActData Load(ODataClient od, ConfigProfile profile, PrintSettings ps, string counterparty, DateTime from, DateTime to,
            string contract = null, string organization = null, Func<string, string> titleOfSet = null)
        {
            ps = ps ?? new PrintSettings();
            if (to < from) throw new AgentException(Txt.S("ActBadPeriod"));
            var (cp, cpName, cpBin) = Counterparty(od, counterparty);
            var reg = "AccountingRegister_" + profile.AccountingRegister;
            var cast = $"cast(guid'{cp}', 'Catalog_Контрагенты')";

            // Договор: часть названия / номера среди договоров контрагента
            string contractKey = null, contractName = null;
            var contracts = od.Rows("Catalog_ДоговорыКонтрагентов", new Dictionary<string, string>
            {
                ["$select"] = "Ref_Key,Description", ["$filter"] = $"Owner_Key eq guid'{cp}' and DeletionMark eq false",
            }).ToDictionary(c => (string)c["Ref_Key"], c => (string)c["Description"]);
            if (!string.IsNullOrWhiteSpace(contract))
            {
                var hit = contracts.Where(c => (c.Value ?? "").IndexOf(contract.Trim(), StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (hit.Count == 0) throw new AgentException(Txt.F("ActNoContract", contract));
                if (hit.Count > 1) throw new AgentException(Txt.F("ActManyContracts", contract, string.Join("; ", hit.Take(10).Select(h => h.Value))));
                (contractKey, contractName) = (hit[0].Key, hit[0].Value);
            }

            var chart = od.Rows("ChartOfAccounts_" + profile.ChartOfAccounts, new Dictionary<string, string> { ["$select"] = "Ref_Key,Code,Parent_Key" });
            var accounts = AccountKeys(chart);
            var balance = od.Rows($"{reg}/Balance(Period=datetime'{Od.D(from.Date)}')", new Dictionary<string, string> { ["$filter"] = $"ExtDimension1 eq {cast}" });
            var records = od.Rows($"{reg}/RecordsWithExtDimensions(StartPeriod=datetime'{Od.D(from.Date)}',EndPeriod=datetime'{Od.D(to.Date.AddDays(1).AddSeconds(-1))}')",
                new Dictionary<string, string> { ["$filter"] = $"ExtDimensionDr1 eq {cast} or ExtDimensionCr1 eq {cast}" });

            // Организация: указана — она; иначе единственная в расчётах с контрагентом (несколько — просим уточнить)
            string orgKey = null;
            var orgs = balance.Select(x => (string)x["Организация_Key"]).Concat(records.Select(x => (string)x["Организация_Key"])).Where(k => !Od.IsEmpty(k)).Distinct().ToList();
            if (!string.IsNullOrWhiteSpace(organization))
            {
                var all = od.Rows("Catalog_Организации", new Dictionary<string, string> { ["$select"] = "Ref_Key,Description,ИдентификационныйНомер" });
                var o = all.FirstOrDefault(x => (string)x["ИдентификационныйНомер"] == organization.Trim())
                    ?? all.FirstOrDefault(x => ((string)x["Description"] ?? "").IndexOf(organization.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                    ?? throw new AgentException(Txt.F("ActNoOrganization", organization));
                orgKey = (string)o["Ref_Key"];
            }
            else if (orgs.Count == 1) orgKey = orgs[0];
            else if (orgs.Count > 1)
            {
                var names = orgs.Select(k => (string)Od.One(od, "Catalog_Организации", k, "Description")?["Description"]).ToList();
                throw new AgentException(Txt.F("ActManyOrganizations", string.Join("; ", names)));
            }
            else
            {
                orgKey = (string)od.Rows("Catalog_Организации", new Dictionary<string, string> { ["$select"] = "Ref_Key", ["$filter"] = "DeletionMark eq false", ["$top"] = "1" }).FirstOrDefault()?["Ref_Key"];
            }

            var byContracts = contractKey == null && ps.ActByContracts && contracts.Count > 1;
            var act = new ActData
            {
                Org = OrgHeader.Load(od, orgKey, ps), CounterpartyKey = cp, CounterpartyBin = cpBin, From = from.Date, To = to.Date, ByContracts = byContracts,
            };
            var cpFull = Od.One(od, "Catalog_Контрагенты", cp, "Description,НаименованиеПолное");
            act.CounterpartyShort = Od.NotEmpty((string)cpFull?["Description"]) ?? cpName;
            act.Counterparty = Od.NotEmpty((string)cpFull?["НаименованиеПолное"]) ?? act.CounterpartyShort;
            foreach (var s in Build(cp, orgKey, contractKey, byContracts, accounts, balance, records)) act.Sections.Add(s);
            if (contractKey != null) act.Contract = contractName;
            else if (!byContracts && contracts.Count == 1) act.Contract = contracts.Values.First();
            foreach (var s in act.Sections.Where(s => s.ContractKey != null))
                s.Contract = contracts.TryGetValue(s.ContractKey, out var cn) ? cn : Txt.S("PrintNoContract");
            foreach (var s in act.Sections.Where(s => byContracts && s.ContractKey == null)) s.Contract = Txt.S("PrintNoContract");
            // Договоров несколько, а расчёты за период — по одному: без разбивки, договор — в шапке
            if (byContracts && act.Sections.Count == 1) { act.ByContracts = false; act.Contract = act.Sections[0].Contract; }

            // Номера документов — только у видов, включённых на «Доступе»; иначе — вид и дата
            var numbers = new Dictionary<string, string>();
            foreach (var g in act.Sections.SelectMany(s => s.Rows).GroupBy(r => r.Doc))
            {
                var set = g.Key.Replace("StandardODATA.", "");
                var allowed = od.Deny == null || od.Deny(set) == null;
                foreach (var row in g)
                {
                    string num = null;
                    if (allowed && !numbers.TryGetValue(set + row.Recorder, out num))
                    {
                        var d = Od.One(od, set, row.Recorder, "Number");
                        numbers[set + row.Recorder] = num = d == null ? null : InvoiceSource.PrintNumber((string)d["Number"]);
                    }
                    var (doc, mirror) = DocTitle(g.Key, num, titleOfSet);
                    row.Doc = doc; row.MirrorDoc = mirror;
                }
            }
            return act;
        }

        public static string Period(DateTime from, DateTime to) =>
            $"{from.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}–{to.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}";
    }
}
