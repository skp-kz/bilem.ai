using System;
using Bilem.Agent.Core.Mcp;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Bilem.Agent.Core.Tests
{
    /// <summary>v0.3, часть D: «данные на …».</summary>
    public class FreshnessTests
    {
        [Fact]
        public void Say_NamesDateAndEveryBank()
        {
            var i = new Freshness.Info { LastDocument = new DateTime(2026, 10, 15) };
            i.Accounts.Add(new Freshness.Account { Bank = "Kaspi", Iik = "KZ00000000000000007183", Last = new DateTime(2026, 10, 15) });
            i.Accounts.Add(new Freshness.Account { Bank = "Halyk", Iik = "KZ00000000000000000001", Last = new DateTime(2026, 10, 12) });
            var o = Freshness.ToJson(i);
            Assert.Equal("Данные 1С на 15.10.2026 (выписка Kaspi ···7183 — 15.10.2026, Halyk ···0001 — 12.10.2026)", (string)o["say"]);
            Assert.Equal("2026-10-12", (string)((JArray)o["bank_statements"])[1]["last_statement"]);
            // «Выписка устарела» убрано 22.10: только даты, без признака давности и предупреждений
            Assert.Null(((JArray)o["bank_statements"])[1]["stale"]);
            Assert.Null(o["warnings"]);
        }

        [Fact]
        public void McpInstructions_RequireDate()
        {
            Assert.Contains("as_of", Txt.S("McpInstructions"));
            Assert.Contains("по данным банка", Txt.S("McpInstructions"));
        }
    }
}
