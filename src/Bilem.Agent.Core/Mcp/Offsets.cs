using System;
using System.Collections.Generic;
using System.Linq;
using Bilem.Agent.Core.Print;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent.Core.Mcp
{
    /// <summary>
    /// Встречные сальдо (#92): контрагенты, у которых одновременно аванс и долг — суммы к зачёту. Поставщики: аванс выданный Дт 1710,
    /// долг Кт 3310; покупатели: аванс полученный Кт 3510, долг Дт 1210. Режим contract — только совпадающий договор на обоих счетах;
    /// counterparty — договоры не важны, same_contract = false, если аванс и долг на разных договорах. Только чтение.
    /// </summary>
    public static class Offsets
    {
        public sealed class Pair { public string Name, Advance, Debt; public int AdvSign, DebtSign; }

        /// <summary>Знак — в какую сторону сальдо считается авансом / долгом (+1 Дт, −1 Кт).</summary>
        internal static Pair PairOf(string name) => (name ?? "suppliers").Trim().ToLowerInvariant() == "buyers"
            ? new Pair { Name = "buyers", Advance = "3510", Debt = "1210", AdvSign = -1, DebtSign = 1 }
            : new Pair { Name = "suppliers", Advance = "1710", Debt = "3310", AdvSign = 1, DebtSign = -1 };

        public sealed class Line
        {
            public string Org, Counterparty, Contract;
            public decimal Advance, Debt;
            public decimal Offset => Math.Min(Advance, Debt);
            public bool? SameContract;
        }

        /// <summary>
        /// Свёртка строк ОСВ (счёт = "adv" / "debt", разрез counterparty, contract) в строки зачёта. В режиме contract — пара по договору;
        /// different — контрагенты, у которых аванс и долг есть, но ни на одном договоре не совпадают.
        /// </summary>
        internal static (List<Line> Lines, List<Line> Different) Match(List<Ledger.Row> rows, Pair p, bool byContract)
        {
            var per = new Dictionary<string, Dictionary<string, (decimal Adv, decimal Debt)>>();
            foreach (var r in rows)
            {
                var cp = r.Keys[0].Value;
                if (cp == null) continue;
                var contract = r.Keys.Count > 1 ? r.Keys[1].Value : null;
                var k = r.Org + "|" + cp;
                if (!per.TryGetValue(k, out var byC)) per[k] = byC = new Dictionary<string, (decimal, decimal)>();
                byC.TryGetValue(contract ?? "", out var v);
                if (r.Account == "adv") v.Adv += r.Close * p.AdvSign; else v.Debt += r.Close * p.DebtSign;
                byC[contract ?? ""] = v;
            }
            var lines = new List<Line>();
            var different = new List<Line>();
            foreach (var kv in per)
            {
                var parts = kv.Key.Split('|');
                var contracts = kv.Value.Where(c => c.Value.Adv > 0 || c.Value.Debt > 0).ToList();
                var same = contracts.Where(c => c.Value.Adv > 0 && c.Value.Debt > 0).ToList();
                if (byContract)
                {
                    foreach (var c in same)
                        lines.Add(new Line { Org = parts[0], Counterparty = parts[1], Contract = c.Key.Length == 0 ? null : c.Key, Advance = c.Value.Adv, Debt = c.Value.Debt });
                    if (same.Count == 0 && contracts.Any(c => c.Value.Adv > 0) && contracts.Any(c => c.Value.Debt > 0))
                        different.Add(new Line
                        {
                            Org = parts[0], Counterparty = parts[1], SameContract = false,
                            Advance = contracts.Where(c => c.Value.Adv > 0).Sum(c => c.Value.Adv), Debt = contracts.Where(c => c.Value.Debt > 0).Sum(c => c.Value.Debt),
                        });
                }
                else
                {
                    // По контрагенту: аванс и долг — сумма положительных сальдо по всем договорам
                    var adv = contracts.Where(c => c.Value.Adv > 0).Sum(c => c.Value.Adv);
                    var debt = contracts.Where(c => c.Value.Debt > 0).Sum(c => c.Value.Debt);
                    if (adv > 0 && debt > 0)
                        lines.Add(new Line { Org = parts[0], Counterparty = parts[1], Advance = adv, Debt = debt, SameContract = same.Count > 0 });
                }
            }
            return (lines.OrderByDescending(l => l.Offset).ToList(), different.OrderByDescending(l => l.Offset).ToList());
        }

        public static JObject Load(BaseRuntime b, JObject a, JObject asOf)
        {
            var p = PairOf((string)a["pair"]);
            var mode = ((string)a["mode"] ?? "counterparty").Trim().ToLowerInvariant() == "contract" ? "contract" : "counterparty";
            var top = Math.Min(200, Math.Max(1, (int?)a["top"] ?? 20));
            var to = Tools.ParseDate(a, "to") ?? DateTime.Today;
            var chart = Ledger.Chart(b);
            var (advTop, advKeys) = Ledger.Resolve(chart, p.Advance);
            var (debtTop, debtKeys) = Ledger.Resolve(chart, p.Debt);
            // Подмена счёта на "adv" / "debt": свёртка Ledger сложит субсчета в одну сторону пары
            var accByKey = new Dictionary<string, Ledger.Acc>();
            foreach (var k in advKeys) accByKey[k] = new Ledger.Acc { Key = k, Code = "adv" };
            foreach (var k in debtKeys) accByKey[k] = new Ledger.Acc { Key = k, Code = "debt" };
            var q = new Ledger.TbRequest { Accounts = new List<string> { p.Advance, p.Debt }, To = to, By = new List<string> { "counterparty", "contract" }, Organization = (string)a["organization"] };
            var rows = Ledger.Rows(b, q, accByKey, out var orgNames);
            var (lines, different) = Match(rows, p, mode == "contract");
            var shown = lines.Take(top).ToList();
            var names = Ledger.Describe(b, shown.Concat(different.Take(top)).SelectMany(l => new[] { ("Catalog_Контрагенты", l.Counterparty), ("Catalog_ДоговорыКонтрагентов", l.Contract) })
                .Where(v => v.Item2 != null), new HashSet<string>());
            var multiOrg = lines.Concat(different).Select(l => l.Org).Distinct().Count() > 1;
            JToken Name(string set, string key) => key == null ? JValue.CreateNull() : names.TryGetValue(set + "|" + key, out var n) && n != null ? n.DeepClone() : (JToken)key;
            JObject Row(Line l)
            {
                var o = new JObject { ["counterparty"] = Name("Catalog_Контрагенты", l.Counterparty) };
                if (multiOrg) o["organization"] = orgNames.TryGetValue(l.Org, out var on) ? on : l.Org;
                if (mode == "contract" && l.SameContract == null) o["contract"] = l.Contract == null ? (JToken)Txt.S("PrintNoContract") : Name("Catalog_ДоговорыКонтрагентов", l.Contract);
                o["advance"] = Tools.Money((double)l.Advance);
                o["debt"] = Tools.Money((double)l.Debt);
                o["offset"] = Tools.Money((double)l.Offset);
                o["advance_left"] = Tools.Money((double)(l.Advance - l.Offset));
                o["debt_left"] = Tools.Money((double)(l.Debt - l.Offset));
                if (l.SameContract.HasValue) o["same_contract"] = l.SameContract.Value;
                return o;
            }
            var total = lines.Sum(l => l.Offset);
            var cps = lines.Select(l => l.Org + "|" + l.Counterparty).Distinct().Count();
            var res = new JObject
            {
                ["base"] = b.Config.Slug,
                ["to"] = Ledger.D(to),
                ["pair"] = new JObject { ["advance"] = advTop?.Code ?? p.Advance, ["debt"] = debtTop?.Code ?? p.Debt, ["kind"] = p.Name },
                ["mode"] = mode,
                ["rows"] = new JArray(shown.Select(Row)),
                ["total"] = new JObject
                {
                    ["count"] = lines.Count, ["counterparties"] = cps,
                    ["advance"] = Tools.Money((double)lines.Sum(l => l.Advance)), ["debt"] = Tools.Money((double)lines.Sum(l => l.Debt)),
                    ["offset"] = Tools.Money((double)total),
                },
                ["say"] = lines.Count == 0
                    ? Txt.F(p.Name == "buyers" ? "OffsetAllClearBuyers" : "OffsetAllClear", Ledger.DateRu(to))
                    : Txt.F("OffsetOpen", cps, Words.Money(total), Ledger.DateRu(to)),
                ["currency"] = "KZT",
            };
            var rest = lines.Skip(shown.Count).ToList();
            if (rest.Count > 0) res["rest"] = new JObject { ["count"] = rest.Count, ["offset"] = Tools.Money((double)rest.Sum(l => l.Offset)) };
            var warnings = new JArray();
            if (mode == "contract" && different.Count > 0)
            {
                res["different_contracts"] = new JObject
                {
                    ["count"] = different.Count, ["offset"] = Tools.Money((double)different.Sum(l => l.Offset)),
                    ["rows"] = new JArray(different.Take(top).Select(Row)),
                };
                warnings.Add(Txt.F("OffsetDifferentContracts", different.Count, Words.Money(different.Sum(l => l.Offset))));
            }
            if (warnings.Count > 0) res["warnings"] = warnings;
            res["note"] = Txt.S(p.Name == "buyers" ? "OffsetNoteBuyers" : "OffsetNote");
            res["as_of"] = asOf;
            return res;
        }
    }
}
