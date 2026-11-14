using System;
using System.Collections.Generic;
using System.Linq;
using Bilem.Agent.Core.Mcp;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Bilem.Agent.Core.Tests
{
    /// <summary>trial_balance / account_entries (#90): счета с субсчетами, свёртка по разрезам, развёрнутое сальдо, сотрудники, проводки.</summary>
    public class LedgerTests
    {
        private const string Org = "33333333-3333-3333-3333-333333333333";
        private const string Cp1 = "11111111-1111-1111-1111-111111111111", Cp2 = "22222222-2222-2222-2222-222222222222";
        private const string K1 = "c0000001-0000-0000-0000-000000000000", K2 = "c0000002-0000-0000-0000-000000000000";
        private const string E1 = "e0000001-0000-0000-0000-000000000000", E2 = "e0000002-0000-0000-0000-000000000000";
        private const string CpT = "StandardODATA.Catalog_Контрагенты", KT = "StandardODATA.Catalog_ДоговорыКонтрагентов", ET = "StandardODATA.Catalog_ФизическиеЛица";

        private static readonly List<Ledger.Acc> Chart = new List<Ledger.Acc>
        {
            new Ledger.Acc { Key = "g1700", Code = "1700", Name = "Краткосрочные авансы выданные", Kind = "active" },
            new Ledger.Acc { Key = "a1710", Code = "1710", Name = "Краткосрочные авансы выданные", Kind = "active", Parent = "g1700" },
            new Ledger.Acc { Key = "a1711", Code = "1711", Name = "Субсчёт", Kind = "active", Parent = "a1710" },
            new Ledger.Acc { Key = "a1720", Code = "1720", Name = "Расходы будущих периодов", Kind = "active", Parent = "g1700" },
            new Ledger.Acc { Key = "a3310", Code = "3310", Name = "Кредиторка поставщикам", Kind = "passive" },
            new Ledger.Acc { Key = "a1251", Code = "1251", Name = "Подотчётные лица", Kind = "active" },
            new Ledger.Acc { Key = "a1030", Code = "1030", Name = "Деньги на счетах", Kind = "active" },
        };

        private static Dictionary<string, Ledger.Acc> ByKey => Chart.ToDictionary(a => a.Key);

        private static Ledger.Cell Cell(string acc, string t1, string v1, string t2, string v2, decimal open, decimal dr, decimal cr) => new Ledger.Cell
        {
            Account = acc, Org = Org, Types = new[] { t1, t2, "" }, Values = new[] { v1, v2, null },
            Open = open, TurnDr = dr, TurnCr = cr, Close = open + dr - cr,
        };

        [Theory]
        [InlineData("17.10", "1710")]
        [InlineData("17 10", "1710")]
        [InlineData("1251", "1251")]
        public void NormCode_VoiceDots(string s, string code) => Assert.Equal(code, Ledger.NormCode(s));

        [Fact]
        public void Resolve_AccountWithSubaccounts_GroupByPrefix_Unknown()
        {
            var (top, keys) = Ledger.Resolve(Chart, "1710");
            Assert.Equal("1710", top.Code);
            Assert.Equal(new[] { "a1710", "a1711" }, keys.OrderBy(x => x));
            var (top2, keys2) = Ledger.Resolve(Chart, "1700");
            Assert.Equal(4, keys2.Count);
            Assert.NotNull(top2);
            var (g, gk) = Ledger.Resolve(Chart, "17");
            Assert.Null(g);
            Assert.Equal(4, gk.Count);
            Assert.Throws<AgentException>(() => Ledger.Resolve(Chart, "9999"));
            Assert.Throws<AgentException>(() => Ledger.Resolve(Chart, "счёт"));
        }

        [Fact]
        public void Aggregate_ByCounterparty_MergesContractsAndSubaccounts()
        {
            var cells = new[]
            {
                Cell("a1710", CpT, Cp1, KT, K1, 100, 50, 0),
                Cell("a1710", CpT, Cp1, KT, K2, 0, 0, 30),
                Cell("a1710", CpT, Cp2, KT, K1, 10, 0, 10),   // закрылся в ноль за период — строка есть (как в ОСВ)
                Cell("a1710", CpT, Cp2, KT, K2, 0, 0, 0),     // пустая — строки нет
            };
            var rows = Ledger.Aggregate(cells, ByKey, new[] { "counterparty" }, null, null);
            Assert.Equal(2, rows.Count);
            var r = rows.Single(x => x.Keys[0].Value == Cp1);
            Assert.Equal(0m, rows.Single(x => x.Keys[0].Value == Cp2).Close);
            Assert.Equal(100m, r.Open);
            Assert.Equal(50m, r.TurnDr);
            Assert.Equal(30m, r.TurnCr);
            Assert.Equal(120m, r.Close);
            // Без разреза — одна строка на счёт, нулевая по Cp2 не мешает
            var total = Assert.Single(Ledger.Aggregate(cells, ByKey, new string[0], null, null));
            Assert.Equal(120m, total.Close);
        }

        [Fact]
        public void Aggregate_CounterpartyAndContract_KeepsAdvanceAndDebtApart()
        {
            var cells = new[] { Cell("a1710", CpT, Cp1, KT, K1, 0, 500, 0), Cell("a3310", CpT, Cp1, KT, K2, 0, 0, 800) };
            var rows = Ledger.Aggregate(cells, ByKey, new[] { "counterparty", "contract" }, null, null);
            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, r => r.Account == "1710" && r.Keys[1].Value == K1 && r.Close == 500);
            Assert.Contains(rows, r => r.Account == "3310" && r.Keys[1].Value == K2 && r.Close == -800);
            var (dt, kt) = Ledger.Split(rows.Select(r => r.Close));
            Assert.Equal(500m, dt);
            Assert.Equal(800m, kt);
        }

        [Fact]
        public void Aggregate_EmployeesHidden_CollapseIntoOneRow()
        {
            var cells = new[] { Cell("a1251", ET, E1, "", null, 0, 100, 0), Cell("a1251", ET, E2, "", null, 0, 40, 0) };
            var open = Ledger.Aggregate(cells, ByKey, new[] { "employee" }, null, new HashSet<string>());
            Assert.Equal(2, open.Count);
            var hidden = Ledger.Aggregate(cells, ByKey, new[] { "employee" }, null, new HashSet<string> { "Catalog_ФизическиеЛица" });
            var r = Assert.Single(hidden);
            Assert.Null(r.Keys[0].Value);
            Assert.Equal("Catalog_ФизическиеЛица", r.Keys[0].Set);
            Assert.Equal(140m, r.Close);
        }

        [Fact]
        public void Matches_AliasAndSubcontoName()
        {
            Assert.True(Ledger.Matches("counterparty", "Catalog_Контрагенты", null));
            Assert.False(Ledger.Matches("counterparty", "Catalog_ДоговорыКонтрагентов", null));
            Assert.True(Ledger.Matches("employee", "Catalog_Сотрудники", null));
            Assert.True(Ledger.Matches("Основные средства", "Catalog_ОсновныеСредства", null));
            Assert.True(Ledger.Matches("склад", "Catalog_Склады", null));
            Assert.True(Ledger.Matches("Работники организации", "Catalog_X", s => "Работники организации"));
            Assert.False(Ledger.Matches("ab", "Catalog_Abc", null));
            Assert.Null(Ledger.SetOf("StandardODATA.Undefined"));
            Assert.Null(Ledger.SetOf(""));
            Assert.Equal("Catalog_Склады", Ledger.SetOf("StandardODATA.Catalog_Склады"));
        }

        [Fact]
        public void Pass_FilterOnAnySubcontoPosition()
        {
            var f = new List<(string[], HashSet<string>)> { (new[] { "Catalog_ДоговорыКонтрагентов" }, new HashSet<string> { K2 }) };
            Assert.True(Ledger.Pass(new[] { CpT, KT, "" }, new[] { Cp1, K2, null }, f));
            Assert.False(Ledger.Pass(new[] { CpT, KT, "" }, new[] { Cp1, K1, null }, f));
            Assert.True(Ledger.Pass(new[] { CpT, KT, "" }, new[] { Cp1, K1, null }, new List<(string[], HashSet<string>)>()));
        }

        private static JObject Rec(string id, string date, int line, string dr, string drT, string drV, string cr, string crT, string crV, decimal sum) => new JObject
        {
            ["Recorder"] = id, ["Recorder_Type"] = "StandardODATA.Document_АвансовыйОтчет", ["Period"] = DateTime.Parse(date), ["LineNumber"] = line.ToString(),
            ["Active"] = true, ["Организация_Key"] = Org, ["Сумма"] = (double)sum, ["Содержание"] = "",
            ["AccountDr_Key"] = dr, ["ExtDimensionDr1"] = drV, ["ExtDimensionDr1_Type"] = drT,
            ["AccountCr_Key"] = cr, ["ExtDimensionCr1"] = crV, ["ExtDimensionCr1_Type"] = crT,
        };

        [Fact]
        public void Pick_SidesInternalEntriesAndFilter()
        {
            var keys = new HashSet<string> { "a1251" };
            var recs = new JArray(
                Rec("d2", "2026-10-05", 1, "a1251", ET, E1, "a1030", "", null, 100),       // выдали под отчёт
                Rec("d1", "2026-10-01", 1, "a1030", "", null, "a1251", ET, E1, 30),        // вернул
                Rec("d3", "2026-10-07", 1, "a1251", ET, E2, "a1251", ET, E1, 20),          // перенос между сотрудниками
                Rec("d4", "2026-10-08", 1, "a1030", "", null, "a3310", CpT, Cp1, 999));   // не наш счёт
            var all = Ledger.Pick(recs, keys, "both", null, new List<(string[], HashSet<string>)>());
            Assert.Equal(new[] { "d1", "d2", "d3" }, all.Select(e => e.Recorder));
            Assert.Equal(120m, all.Where(e => e.Dt).Sum(e => e.Amount));
            Assert.Equal(50m, all.Where(e => e.Kt).Sum(e => e.Amount));
            var dt = Ledger.Pick(recs, keys, "dt", null, new List<(string[], HashSet<string>)>());
            Assert.True(dt.All(e => e.Dt && !e.Kt));
            // Отбор по сотруднику — по субконто нашей стороны
            var f = new List<(string[], HashSet<string>)> { (new[] { "Catalog_ФизическиеЛица" }, new HashSet<string> { E1 }) };
            var e1 = Ledger.Pick(recs, keys, "both", null, f);
            Assert.Equal(100m, e1.Where(e => e.Dt).Sum(e => e.Amount));
            Assert.Equal(50m, e1.Where(e => e.Kt).Sum(e => e.Amount));
            Assert.Empty(Ledger.Pick(recs, keys, "both", "другая", f));
        }

        private static Ledger.Row Bal(string side, string cp, string contract, decimal close) => new Ledger.Row
        {
            Account = side, Org = Org, Close = close,
            Keys = new List<(string, string, string)> { ("counterparty", "Catalog_Контрагенты", cp), ("contract", "Catalog_ДоговорыКонтрагентов", contract) },
        };

        [Fact]
        public void Offsets_ByCounterparty_AndByContract()
        {
            var p = Offsets.PairOf("suppliers");
            var rows = new List<Ledger.Row>
            {
                Bal("adv", Cp1, K1, 500), Bal("debt", Cp1, K1, -800),     // аванс и долг по одному договору
                Bal("adv", Cp2, K1, 300), Bal("debt", Cp2, K2, -100),     // на разных договорах
                Bal("adv", "33333333-0000-0000-0000-000000000001", K1, 50), // только аванс
            };
            var (byCp, _) = Offsets.Match(rows, p, false);
            Assert.Equal(2, byCp.Count);
            Assert.Equal(Cp1, byCp[0].Counterparty);
            Assert.Equal(500m, byCp[0].Offset);
            Assert.True(byCp[0].SameContract);
            Assert.Equal(100m, byCp[1].Offset);
            Assert.False(byCp[1].SameContract);
            var (byK, diff) = Offsets.Match(rows, p, true);
            var one = Assert.Single(byK);
            Assert.Equal(K1, one.Contract);
            Assert.Equal(500m, one.Advance);
            Assert.Equal(800m, one.Debt);
            Assert.Equal(Cp2, Assert.Single(diff).Counterparty);
        }

        [Fact]
        public void Offsets_BuyersSigns_CreditAdvanceDebitDebt()
        {
            var p = Offsets.PairOf("buyers");
            Assert.Equal("3510", p.Advance);
            var (lines, _) = Offsets.Match(new List<Ledger.Row> { Bal("adv", Cp1, K1, -200), Bal("debt", Cp1, K1, 700), Bal("adv", Cp2, K1, 90) }, p, false);
            var l = Assert.Single(lines);
            Assert.Equal(200m, l.Offset);
            Assert.Equal(500m, l.Debt - l.Offset);
        }

        [Fact]
        public void Age_Fifo_PaymentsCloseOldest_BucketsAndOldest()
        {
            var accByKey = ByKey;
            var by = new[] { "employee" };
            var row = new Ledger.Row { Account = "1251", Org = Org, Close = 150, Keys = new List<(string, string, string)> { ("employee", "Catalog_ФизическиеЛица", E1) } };
            var asOf = new DateTime(2026, 11, 14);
            var keys = new HashSet<string> { "a1251" };
            var recs = new JArray(
                Rec("old", "2026-01-10", 1, "a1251", ET, E1, "a1030", "", null, 100),   // 308 дней — погашена возвратом
                Rec("mid", "2026-07-01", 1, "a1251", ET, E1, "a1030", "", null, 100),   // 136 дней
                Rec("new", "2026-11-01", 1, "a1251", ET, E1, "a1030", "", null, 50),    // 13 дней
                Rec("ret", "2026-08-01", 1, "a1030", "", null, "a1251", ET, E1, 100),
                Rec("mv", "2026-10-01", 1, "a1251", ET, E1, "a1251", ET, E1, 999));    // перенос внутри строки — не выдача
            var entries = Ledger.Pick(recs, keys, "both", null, new List<(string[], HashSet<string>)>());
            Assert.True(Ledger.Age(new List<Ledger.Row> { row }, entries, accByKey, by, null, null, asOf));
            Assert.Equal(new[] { 50m, 0m, 100m, 0m }, row.Aging);
            Assert.Equal("mid", row.Oldest.Recorder);
            // Сальдо больше всех выдач в окне — остаток в «больше 180» и признак окна
            var big = new Ledger.Row { Account = "1251", Org = Org, Close = 400, Keys = row.Keys };
            Assert.False(Ledger.Age(new List<Ledger.Row> { big }, entries, accByKey, by, null, null, asOf));
            Assert.Equal(150m, big.Older);
            Assert.Equal(250m, big.Aging[3]);
        }

        // ---------- Имена сотрудников по умолчанию (#105) ----------

        private static BaseRuntime Runtime(bool? names)
        {
            var cfg = new BaseConfig
            {
                Slug = "inv-demo", Configuration = "БухгалтерияДляКазахстана", OData = "http://127.0.0.1/x", Version = "3.0",
                ODataObjects = new List<string> { "Справочник.Контрагенты", "Справочник.ФизическиеЛица", "РегистрБухгалтерии.Типовой" },
            };
            if (names.HasValue) cfg.EmployeeNames = names.Value;
            return new BaseRuntime(cfg, "p");
        }

        [Fact]
        public void EmployeeNames_OnByDefault_WithoutSalaryAccess()
        {
            var b = Runtime(null);
            Assert.True(b.Config.EmployeeNames);
            // «Физические лица» — служебный объект под замком «Доступа», а имена в разрезе всё равно видны
            Assert.DoesNotContain("Справочник.ФизическиеЛица", b.Config.ReadEffective);
            Assert.Empty(Ledger.HiddenSets(b.OData));
            Assert.Null(b.OData.Deny("Catalog_ФизическиеЛица"));
            Assert.Null(b.OData.Deny("Catalog_Сотрудники"));
            // остальное под замком по-прежнему закрыто
            Assert.NotNull(b.OData.Deny("Document_АвансовыйОтчет"));
            Assert.NotNull(b.OData.Deny("Catalog_КонтактныеЛица"));
        }

        [Fact]
        public void EmployeeNames_Off_HidesEmployees()
        {
            var b = Runtime(false);
            Assert.Equal(new HashSet<string>(Ledger.Aliases["employee"]), Ledger.HiddenSets(b.OData));
            Assert.NotNull(b.OData.Deny("Catalog_ФизическиеЛица"));
        }

        [Fact]
        public void EmployeeNames_OldConfigWithoutField_DefaultsOn()
        {
            var cfg = Newtonsoft.Json.JsonConvert.DeserializeObject<BaseConfig>("{\"Slug\":\"old\"}");
            Assert.True(cfg.EmployeeNames);
        }
    }
}
