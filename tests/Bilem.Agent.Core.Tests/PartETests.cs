using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Bilem.Agent.Core.Print;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Bilem.Agent.Core.Tests
{
    /// <summary>v0.3, часть E: доверенность, акт сверки, подвкладки «Печатных форм», журнал аудита.</summary>
    public class PartETests
    {
        private const string Cp = "11111111-1111-1111-1111-111111111111", Other = "22222222-2222-2222-2222-222222222222";
        private const string Org = "33333333-3333-3333-3333-333333333333", Org2 = "44444444-4444-4444-4444-444444444444";
        private const string A1210 = "a1210", A3510 = "a3510", A6010 = "a6010", A1030 = "a1030";
        private const string D1 = "d0000001-0000-0000-0000-000000000000", D2 = "d0000002-0000-0000-0000-000000000000";
        private const string CpT = "StandardODATA.Catalog_Контрагенты", DogT = "StandardODATA.Catalog_ДоговорыКонтрагентов";

        private static JObject Rec(string recorder, string date, string accDr, string dr1, string dr2, string accCr, string cr1, string cr2, decimal sum, string org = Org) => new JObject
        {
            ["Recorder"] = recorder, ["Recorder_Type"] = "StandardODATA.Document_РеализацияТоваровУслуг", ["Period"] = DateTime.Parse(date), ["Active"] = true,
            ["AccountDr_Key"] = accDr, ["ExtDimensionDr1"] = dr1, ["ExtDimensionDr1_Type"] = dr1 == null ? "" : CpT, ["ExtDimensionDr2"] = dr2, ["ExtDimensionDr2_Type"] = dr2 == null ? "" : DogT,
            ["AccountCr_Key"] = accCr, ["ExtDimensionCr1"] = cr1, ["ExtDimensionCr1_Type"] = cr1 == null ? "" : CpT, ["ExtDimensionCr2"] = cr2, ["ExtDimensionCr2_Type"] = cr2 == null ? "" : DogT,
            ["Организация_Key"] = org, ["Сумма"] = (double)sum,
        };

        private static readonly HashSet<string> Accounts = new HashSet<string> { A1210, A3510 };

        private static JArray Records() => new JArray
        {
            Rec("r1", "2026-07-05", A1210, Cp, D1, A6010, null, null, 150000),          // реализация: долг контрагента растёт
            Rec("r2", "2026-07-20", A1030, null, null, A1210, Cp, D1, 250000),          // оплата
            Rec("r3", "2026-08-10", A1210, Cp, D2, A6010, null, null, 200000),          // реализация по второму договору
            Rec("r4", "2026-08-11", A3510, Cp, D1, A1210, Cp, D1, 50000),               // зачёт аванса — внутренняя, в акт не идёт
            Rec("r5", "2026-08-12", A1210, Other, null, A6010, null, null, 999),        // другой контрагент
            Rec("r6", "2026-08-13", A1210, Cp, D1, A6010, null, null, 777, Org2),       // другая организация
            Rec("r7", "2026-09-15", A1030, null, null, A1210, Cp, D2, 120000),
        };

        private static JArray Balance() => new JArray
        {
            new JObject { ["Account_Key"] = A1210, ["ExtDimension1"] = Cp, ["ExtDimension2"] = D1, ["ExtDimension2_Type"] = DogT, ["Организация_Key"] = Org, ["СуммаBalance"] = 100000.0 },
            new JObject { ["Account_Key"] = A6010, ["ExtDimension1"] = Cp, ["Организация_Key"] = Org, ["СуммаBalance"] = 5.0 },   // не счёт расчётов
        };

        /// <summary>Сальдо, обороты и сальдо на конец — как в образце акта Славы (100 000 + 350 000 − 370 000 = 80 000).</summary>
        [Fact]
        public void Act_TotalsLikeSample_SkipsInternalOtherCpAndOrg()
        {
            var s = ReconciliationSource.Build(Cp, Org, null, false, Accounts, Balance(), Records());
            Assert.Single(s);
            Assert.Equal(100000m, s[0].Open);
            Assert.Equal(350000m, s[0].TurnDr);
            Assert.Equal(370000m, s[0].TurnCr);
            Assert.Equal(80000m, s[0].Close);
            Assert.Equal(new[] { "r1", "r2", "r3", "r7" }, s[0].Rows.Select(r => r.Recorder));
        }

        [Fact]
        public void Act_ByContractsAndOneContract()
        {
            var by = ReconciliationSource.Build(Cp, Org, null, true, Accounts, Balance(), Records());
            Assert.Equal(2, by.Count);
            var d1 = by.Single(x => x.ContractKey == D1);
            var d2 = by.Single(x => x.ContractKey == D2);
            Assert.Equal(100000m + 150000m - 250000m, d1.Close);
            Assert.Equal(200000m - 120000m, d2.Close);
            var only = ReconciliationSource.Build(Cp, Org, D2, false, Accounts, Balance(), Records());
            Assert.Equal(0m, only.Single().Open);
            Assert.Equal(80000m, only.Single().Close);
        }

        [Fact]
        public void Act_DocTitles_Mirror()
        {
            Assert.Equal(("Реализация № 41", "Поступление № 41"), ReconciliationSource.DocTitle("StandardODATA.Document_РеализацияТоваровУслуг", "41", null));
            Assert.Equal(("Оплата (ПП № 87)", "Оплата (ПП № 87)"), ReconciliationSource.DocTitle("StandardODATA.Document_ПлатежноеПоручениеВходящее", "87", null));
            Assert.Equal(("Оплата (ПКО)", "Оплата (РКО)"), ReconciliationSource.DocTitle("StandardODATA.Document_ПриходныйКассовыйОрдер", null, null));
            Assert.Equal(("Прочее № 5", "Прочее № 5"), ReconciliationSource.DocTitle("StandardODATA.Document_X", "5", set => "Прочее"));
        }

        [Fact]
        public void Act_DefaultPeriods()
        {
            var today = new DateTime(2026, 10, 18);
            Assert.Equal((new DateTime(2026, 7, 1), new DateTime(2026, 9, 30)), ReconciliationSource.DefaultPeriod("quarter", today));
            Assert.Equal((new DateTime(2026, 9, 1), new DateTime(2026, 9, 30)), ReconciliationSource.DefaultPeriod("month", today));
            Assert.Equal((new DateTime(2026, 1, 1), today), ReconciliationSource.DefaultPeriod("year", today));
            Assert.Equal((new DateTime(2025, 10, 1), new DateTime(2025, 12, 31)), ReconciliationSource.DefaultPeriod(null, new DateTime(2026, 2, 3)));
        }

        [Fact]
        public void Act_AccountsWithSubaccounts()
        {
            var chart = new JArray
            {
                new JObject { ["Ref_Key"] = "g", ["Code"] = "1200", ["Parent_Key"] = null },
                new JObject { ["Ref_Key"] = "p", ["Code"] = "1210", ["Parent_Key"] = "g" },
                new JObject { ["Ref_Key"] = "c", ["Code"] = "1211", ["Parent_Key"] = "p" },
                new JObject { ["Ref_Key"] = "x", ["Code"] = "6010", ["Parent_Key"] = null },
            };
            Assert.Equal(new[] { "c", "p" }, ReconciliationSource.AccountKeys(chart).OrderBy(x => x));
        }

        [Theory]
        [InlineData(1, "Один")]
        [InlineData(2, "Два")]
        [InlineData(21, "Двадцать один")]
        [InlineData(2.5, "2,5")]
        public void QtyWords(double v, string expected) => Assert.Equal(expected, Words.QtyWords((decimal)v));

        [Theory]
        [InlineData("Иванов Иван Иванович", "Иванов И.И.")]
        [InlineData("Тестова И.В.", "Тестова И.В.")]
        [InlineData("Петров Пётр", "Петров П.")]
        [InlineData("Директор", "Директор")]
        public void ShortName(string fio, string expected) => Assert.Equal(expected, Words.ShortName(fio));

        /// <summary>Подписанты: своё значение главнее 1С; пусто — из 1С; нет нигде — null («/______/»).</summary>
        [Fact]
        public void Signers_OwnOverOneC()
        {
            var ps = new PrintSettings();
            ps.Signers[Org] = OrgSigner.Parse(JObject.Parse("{\"" + Org + "\":{\"director\":{\"name\":\"Тестова Инна Валерьевна\",\"title\":\"Директор\"}}}"))[Org];
            Assert.Equal(("Тестова И.В.", "Директор"), ps.DirectorFor(Org));
            Assert.Null(ps.ChiefAccountantFor(Org));
            Assert.Equal((null, null), ps.DirectorFor(Org2));
            ps.Director = "Иванов И.И."; ps.DirectorTitle = "Генеральный директор"; ps.ChiefAccountant = "Петрова А.Б.";
            Assert.Equal(("Иванов И.И.", "Генеральный директор"), ps.DirectorFor(Org));
            Assert.Equal("Петрова А.Б.", ps.ChiefAccountantFor(Org2));
        }

        /// <summary>Стандартное вступление акта в config.json не пишется (его можно улучшать); старый стандартный текст читается как «пусто».</summary>
        [Fact]
        public void ActIntro_DefaultNotStored()
        {
            var ps = new PrintSettings();
            Assert.Equal(PrintSettings.DefaultActIntro, ps.ActIntro);
            Assert.DoesNotContain("нижеподписавшиеся", JsonConvert.SerializeObject(ps));
            var old = "Мы, нижеподписавшиеся, {подписант} {организация}, с одной стороны, и ____________________ {контрагент}, с другой стороны, составили настоящий акт сверки в том, что состояние взаимных расчётов по данным учёта следующее:";
            var back = JsonConvert.DeserializeObject<PrintSettings>(JsonConvert.SerializeObject(new { ActIntro = old }));
            Assert.Equal(PrintSettings.DefaultActIntro, back.ActIntro);
            ps.ActIntro = "Свой текст {контрагент}";
            Assert.Equal("Свой текст {контрагент}", JsonConvert.DeserializeObject<PrintSettings>(JsonConvert.SerializeObject(ps)).ActIntro);
        }

        private static OrgHeader OrgH() => new OrgHeader { Key = Org, Name = "ТОО «Тест»", ShortName = "ТОО «Тест»", Bin = "123456789012", Address = "г. Алматы", Iik = "KZ000", BankName = "Банк" };

        private static PoaData Poa()
        {
            var p = new PoaData
            {
                Number = "00-000001", PrintNumber = "1", Date = new DateTime(2026, 10, 18), ValidTo = new DateTime(2026, 10, 28), Org = OrgH(),
                PersonName = "Иванов Иван Иванович", PersonTitle = "менеджер", IdKind = "Удостоверение личности", IdText = "№ 987654321 от 01.01.2020, выдано МВД РК",
                From = "ТОО «Поставщик»", ByDocument = "счёту № 123 от 15.10.2026",
            };
            p.Lines.Add(new PoaLine { N = 1, Name = "Маршрутизатор", Unit = "шт", Qty = 2 });
            return p;
        }

        [Fact]
        public void Pdfs_Render()
        {
            var poa = PoaPdf.Render(Poa(), new PrintSettings(), null, null, null);
            Assert.Equal("%PDF", Encoding.ASCII.GetString(poa, 0, 4));
            var act = new ActData { Org = OrgH(), CounterpartyKey = Cp, Counterparty = "ТОО «Клиент»", CounterpartyShort = "ТОО «Клиент»", From = new DateTime(2026, 7, 1), To = new DateTime(2026, 9, 30) };
            foreach (var s in ReconciliationSource.Build(Cp, Org, null, true, Accounts, Balance(), Records())) act.Sections.Add(s);
            act.ByContracts = true;
            foreach (var r in act.Sections.SelectMany(s => s.Rows)) { r.Doc = "Реализация № 1"; r.MirrorDoc = "Поступление № 1"; }
            foreach (var mirror in new[] { false, true })
            {
                var pdf = ReconciliationPdf.Render(act, new PrintSettings { ActMirror = mirror }, null, null, null);
                Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
            }
        }

        /// <summary>Журнал аудита доверенности: документ и хеш PDF — да; ИИН и удостоверение личности — нет.</summary>
        [Fact]
        public void Audit_Poa_NoIdentityNoIin()
        {
            var p = Poa();
            var pdf = PoaPdf.Render(p, new PrintSettings(), null, null, null);
            var title = Mcp.Tools.PoaTitle(p, "t");
            var o = Audit.Write(Audit.Print, "t", Mail.MailService.DocPowerOfAttorney, title, @"C:\x\" + title + ".pdf", pdf,
                caller: new Mcp.McpCaller { Client = "claude-code 2.1", Remote = "127.0.0.1" });
            var line = File.ReadAllLines(Audit.FileFor(DateTime.Now)).Last();
            Assert.Equal(o.ToString(Formatting.None), line);
            Assert.DoesNotContain("987654321", line);         // номер удостоверения
            Assert.DoesNotContain("123456789012", line);      // БИН / ИИН
            Assert.DoesNotContain("МВД", line);
            Assert.Contains("Доверенность №1 от 18.10.2026 (t)", line);
            Assert.Equal(Audit.Sha256(pdf), (string)o["pdf"]["sha256"]);
        }

        [Fact]
        public void Readiness_Dots()
        {
            var b = new BaseConfig { Slug = "ready-" + Guid.NewGuid().ToString("N").Substring(0, 6), Print = new PrintSettings { IssuedBy = "Бухгалтер" } };
            Assert.Equal(ReadyLevel.Missing, PrintReadiness.Of(b, PrintForm.General).Level);
            b.Print.Invoice = new FormParts { Logo = false, Stamp = false, Sign = false };
            Assert.Equal(ReadyLevel.Ready, PrintReadiness.Of(b, PrintForm.Invoice).Level);
            b.Print.PowerOfAttorney = new FormParts { Logo = false, Stamp = false, Sign = false };
            Assert.Contains("руководителя", PrintReadiness.Of(b, PrintForm.PowerOfAttorney).Hint);
            b.Print.Director = "Иванов И.И.";
            Assert.Equal(ReadyLevel.Ready, PrintReadiness.Of(b, PrintForm.PowerOfAttorney).Level);
        }

        /// <summary>«Физические лица» — служебный объект (ФИО для доверенности): ИИ его не видит, пока не включён на «Доступе».</summary>
        [Fact]
        public void PersonalCatalog_ServiceButOffForAi()
        {
            Assert.True(Profiles.IsService("Справочник.ФизическиеЛица"));
            Assert.True(Profiles.IsService("РегистрСведений.ПаспортныеДанныеФизЛиц"));
            var b = new BaseConfig { ODataObjects = new List<string> { "Справочник.Контрагенты", "Справочник.ФизическиеЛица", "РегистрСведений.ПаспортныеДанныеФизЛиц" } };
            Assert.Equal(new[] { "Справочник.Контрагенты" }, b.ReadEffective);
            b.Read = new List<string> { "Справочник.Контрагенты", "Справочник.ФизическиеЛица" };
            Assert.Contains("Справочник.ФизическиеЛица", b.ReadEffective);
            Assert.False(Access.NeedsOneC(b, new HashSet<string>(b.Read), new HashSet<string>()));
            Assert.False(Access.NeedsOneC(new BaseConfig { ODataObjects = b.ODataObjects }, new HashSet<string> { "Справочник.Контрагенты" }, new HashSet<string>()));
        }
    }
}
