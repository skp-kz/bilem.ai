using System;
using System.Collections.Generic;
using System.Linq;
using Bilem.Agent.Core.Mcp;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Bilem.Agent.Core.Tests
{
    /// <summary>«Кто нам должен» / «кому должны мы»: остатки по договорам, зачёт аванса, возраст долга FIFO, отборы.</summary>
    public class DebtsTests
    {
        private const string Org = "33333333-3333-3333-3333-333333333333";
        private const string Cp = "11111111-1111-1111-1111-111111111111", Cp2 = "22222222-2222-2222-2222-222222222222";
        private const string K1 = "c0000001-0000-0000-0000-000000000000", K2 = "c0000002-0000-0000-0000-000000000000";
        private const string A1210 = "a1210", A3510 = "a3510", A6010 = "a6010", A1030 = "a1030", A3310 = "a3310", A1710 = "a1710", A1310 = "a1310";
        private static readonly HashSet<string> Main = new HashSet<string> { A1210 }, Adv = new HashSet<string> { A3510 };
        private static readonly DateTime AsOf = new DateTime(2026, 10, 19);

        private static JObject Bal(string acc, string cp, string contract, decimal sum) => new JObject
        {
            ["Account_Key"] = acc, ["Организация_Key"] = Org, ["ExtDimension1"] = cp, ["ExtDimension1_Type"] = Debts.CpType,
            ["ExtDimension2"] = contract, ["ExtDimension2_Type"] = contract == null ? "" : Debts.ContractType, ["СуммаBalance"] = (double)sum,
        };

        private static JObject Rec(string id, string date, string dr, string drCp, string cr, string crCp, decimal sum, string contract = K1) => new JObject
        {
            ["Recorder"] = id, ["Recorder_Type"] = "StandardODATA.Document_РеализацияТоваровУслуг", ["Period"] = DateTime.Parse(date), ["Active"] = true,
            ["Организация_Key"] = Org, ["Сумма"] = (double)sum,
            ["AccountDr_Key"] = dr, ["ExtDimensionDr1"] = drCp, ["ExtDimensionDr1_Type"] = drCp == null ? "" : Debts.CpType,
            ["ExtDimensionDr2"] = drCp == null ? null : contract, ["ExtDimensionDr2_Type"] = drCp == null ? "" : Debts.ContractType,
            ["AccountCr_Key"] = cr, ["ExtDimensionCr1"] = crCp, ["ExtDimensionCr1_Type"] = crCp == null ? "" : Debts.CpType,
            ["ExtDimensionCr2"] = crCp == null ? null : contract, ["ExtDimensionCr2_Type"] = crCp == null ? "" : Debts.ContractType,
        };

        [Fact]
        public void Balance_AdvanceOnSameContractIsNetted_OtherContractIsNot()
        {
            var bal = new JArray(Bal(A1210, Cp, K1, 1000), Bal(A3510, Cp, K1, -300), Bal(A3510, Cp, K2, -500), Bal(A1210, Cp2, K1, -50), Bal(A6010, Cp, K1, 999));
            var d = Debts.FromBalance(DebtKind.Receivables, bal, Main, Adv);
            var first = d.Single(x => x.Key == Cp);
            Assert.Equal(700, first.Amount);         // 1000 − 300 по договору K1; аванс по K2 с долгом K1 не зачитывается
            Assert.Equal(800, first.Advance);
            Assert.Equal(0, d.Single(x => x.Key == Cp2).Amount);   // Кт 1210 (переплата) — не долг
        }

        [Fact]
        public void Payables_SignMirrored()
        {
            var bal = new JArray(Bal(A3310, Cp, K1, -1200), Bal(A1710, Cp, K1, 200));
            var d = Debts.FromBalance(DebtKind.Payables, bal, new HashSet<string> { A3310 }, new HashSet<string> { A1710 }).Single();
            Assert.Equal(1000, d.Amount);
            Assert.Equal(200, d.Advance);
        }

        [Fact]
        public void Fifo_UnpaidAreLatestDocuments_PartialOldest()
        {
            // Реализации 100 (янв), 200 (фев), 300 (мар); оплачено 250 → долг 350: март целиком и 50 от февраля
            var bal = new JArray(Bal(A1210, Cp, K1, 350));
            var rec = new JArray(
                Rec("d1", "2026-01-10", A1210, Cp, A6010, null, 100), Rec("d2", "2026-02-10", A1210, Cp, A6010, null, 200),
                Rec("d3", "2026-03-10", A1210, Cp, A6010, null, 300), Rec("p1", "2026-03-20", A1030, null, A1210, Cp, 250));
            var d = Debts.FromBalance(DebtKind.Receivables, bal, Main, Adv);
            Assert.True(Debts.Age(DebtKind.Receivables, d, rec, Main, AsOf));
            var docs = d[0].Docs.ToList();
            Assert.Equal(new[] { "d2", "d3" }, docs.Select(x => x.Recorder));
            Assert.Equal(50, docs[0].Unpaid);
            Assert.Equal(150, docs[0].Amount - docs[0].Unpaid);   // оплачено
            Assert.Equal(300, docs[1].Unpaid);
            Assert.Equal((int)(AsOf - new DateTime(2026, 2, 10)).TotalDays, d[0].Oldest.Days);
        }

        [Fact]
        public void Fifo_NotEnoughDocumentsInWindow_RestIsOlder()
        {
            var bal = new JArray(Bal(A1210, Cp, K1, 500));
            var rec = new JArray(Rec("d1", "2026-09-01", A1210, Cp, A6010, null, 200));
            var d = Debts.FromBalance(DebtKind.Receivables, bal, Main, Adv);
            Assert.False(Debts.Age(DebtKind.Receivables, d, rec, Main, AsOf));
            Assert.Equal(300, d[0].Contracts[0].Older);
            Assert.Equal(300, Debts.Buckets(d)[3]);   // неразобранный остаток — в 90+
        }

        [Fact]
        public void Fifo_TransferBetweenSubaccountsOfSameCounterparty_IsNotNewDebt()
        {
            var bal = new JArray(Bal(A1210, Cp, K1, 100));
            var rec = new JArray(Rec("d1", "2026-08-01", A1210, Cp, A6010, null, 100), Rec("t1", "2026-09-01", A1210, Cp, A1210, Cp, 100));
            var d = Debts.FromBalance(DebtKind.Receivables, bal, Main, Adv);
            Debts.Age(DebtKind.Receivables, d, rec, Main, AsOf);
            Assert.Equal(new[] { "d1" }, d[0].Docs.Select(x => x.Recorder));
        }

        [Fact]
        public void Select_MinDaysAndIssuedBefore()
        {
            var bal = new JArray(Bal(A1210, Cp, K1, 600));
            var rec = new JArray(Rec("d1", "2026-07-01", A1210, Cp, A6010, null, 100), Rec("d2", "2026-09-01", A1210, Cp, A6010, null, 200),
                Rec("d3", "2026-10-15", A1210, Cp, A6010, null, 300));
            var d = Debts.FromBalance(DebtKind.Receivables, bal, Main, Adv);
            Debts.Age(DebtKind.Receivables, d, rec, Main, AsOf);
            Debts.Select(d, 30, null);
            Assert.Equal(300, d[0].Selected);                         // d1 + d2 старше 30 дней
            Debts.Select(d, 0, new DateTime(2026, 10, 1));            // «не оплатили с прошлого месяца»
            Assert.Equal(300, d[0].Selected);
            Debts.Select(d, 0, null);
            Assert.Equal(600, d[0].Selected);
            var b = Debts.Buckets(d);
            Assert.Equal(new decimal[] { 300, 200, 0, 100 }, b);       // 4 дня; 48 дней; 110 дней
        }

        [Fact]
        public void Payables_IncreaseIsCreditSide()
        {
            var main = new HashSet<string> { A3310 };
            var bal = new JArray(Bal(A3310, Cp, K1, -400));
            var rec = new JArray(Rec("r1", "2026-09-01", A1310, null, A3310, Cp, 400), Rec("x", "2026-09-05", A3310, Cp, A1030, null, 999));
            var d = Debts.FromBalance(DebtKind.Payables, bal, main, new HashSet<string> { A1710 });
            Assert.True(Debts.Age(DebtKind.Payables, d, rec, main, AsOf));
            Assert.Equal(new[] { "r1" }, d[0].Docs.Select(x => x.Recorder));
        }
    }
}
