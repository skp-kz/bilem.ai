using System;
using Bilem.Agent.Core.Mcp;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Bilem.Agent.Core.Tests
{
    /// <summary>v0.3, часть D: «данные на …»; #102 — одна дата и одна фраза.</summary>
    public class FreshnessTests
    {
        [Fact]
        public void Say_OneDate_LaggingAccountOnSeparateLine()
        {
            var i = new Freshness.Info { LastCash = new DateTime(2026, 11, 5), LastTrade = new DateTime(2026, 11, 13) };
            i.Accounts.Add(new Freshness.Account { Bank = "Kaspi", Iik = "KZ00000000000000007183", Last = new DateTime(2026, 11, 12) });
            i.Accounts.Add(new Freshness.Account { Bank = "Halyk", Iik = "KZ00000000000000000001", Last = new DateTime(2026, 11, 10) });
            i.Accounts.Add(new Freshness.Account { Bank = "Forte", Iik = "KZ00000000000000000002", Last = new DateTime(2026, 10, 2) });
            var o = Freshness.ToJson(i, Freshness.Kind.Money);
            // Дата — самая поздняя выписка или касса; «последний документ» не показываем; отстающие больше недели — отдельной строкой
            Assert.Equal("Остатки по данным 1С на 12.11.2026.\nПо счёту Forte ···0002 выписки загружены по 02.10.2026.", (string)o["say"]);
            Assert.Equal("2026-11-12", (string)o["date"]);
            Assert.Null(o["last_document"]);
            Assert.Equal("2026-11-10", (string)((JArray)o["bank_statements"])[1]["last_statement"]);
            // Одинаково при каждом вызове
            Assert.Equal((string)o["say"], (string)Freshness.ToJson(i, Freshness.Kind.Money)["say"]);
        }

        [Fact]
        public void Say_Debts_CountsTradeAndCashLag()
        {
            var i = new Freshness.Info { LastCash = new DateTime(2026, 11, 1), LastTrade = new DateTime(2026, 11, 13) };
            i.Accounts.Add(new Freshness.Account { Bank = "Kaspi", Iik = "KZ00000000000000007183", Last = new DateTime(2026, 11, 12) });
            Assert.Equal("Долги по данным 1С на 13.11.2026.\nКассовые документы — по 01.11.2026.", (string)Freshness.ToJson(i, Freshness.Kind.Debts)["say"]);
        }

        [Fact]
        public void Say_UnknownWithoutDocuments()
        {
            Assert.Equal("Дата данных 1С не определена", (string)Freshness.ToJson(new Freshness.Info(), Freshness.Kind.Money)["say"]);
        }

        [Fact]
        public void Labels_DifferForSameTail()
        {
            var i = new Freshness.Info();
            i.Accounts.Add(new Freshness.Account { Bank = "Kaspi", Iik = "KZ72722S000012025" });
            i.Accounts.Add(new Freshness.Account { Bank = "Kaspi", Iik = "KZ72722S000022025" });
            i.Accounts.Add(new Freshness.Account { Bank = "Kaspi", Iik = "KZ72722S000022025", Name = "Kaspi USD" });
            i.Accounts.Add(new Freshness.Account { Bank = "Kaspi", Iik = "KZ72722C938" });
            Freshness.Labels(i.Accounts);
            Assert.Equal("Kaspi ···12025", i.Accounts[0].Label);
            Assert.Equal("Kaspi ···22025 (№1)", i.Accounts[1].Label);
            Assert.Equal("Kaspi ···22025 (Kaspi USD)", i.Accounts[2].Label);
            Assert.Equal("Kaspi ···2938", i.Accounts[3].Label);
        }

        [Fact]
        public void McpInstructions_RequireDate()
        {
            Assert.Contains("as_of", Txt.S("McpInstructions"));
            Assert.Contains("по данным банка", Txt.S("McpInstructions"));
        }
    }
}
