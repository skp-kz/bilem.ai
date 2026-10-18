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
        public void Workdays_SkipWeekend()
        {
            // пт 16.10.2026 → пн 19.10.2026: один рабочий день
            Assert.Equal(1, Freshness.WorkdaysBetween(new DateTime(2026, 10, 16), new DateTime(2026, 10, 19)));
            Assert.Equal(0, Freshness.WorkdaysBetween(new DateTime(2026, 10, 19), new DateTime(2026, 10, 19)));
            Assert.Equal(5, Freshness.WorkdaysBetween(new DateTime(2026, 10, 12), new DateTime(2026, 10, 19)));
        }

        [Fact]
        public void Say_NamesDateAndEveryBank()
        {
            var i = new Freshness.Info { LastDocument = new DateTime(2026, 10, 15) };
            i.Accounts.Add(new Freshness.Account { Bank = "Kaspi", Iik = "KZ00000000000000007183", Last = new DateTime(2026, 10, 15) });
            i.Accounts.Add(new Freshness.Account { Bank = "Halyk", Iik = "KZ00000000000000000001", Last = new DateTime(2026, 10, 12), Stale = true });
            i.Warnings.Add("выписки по Halyk не загружены с 12.10.2026");
            var o = Freshness.ToJson(i);
            Assert.Equal("Данные 1С на 15.10.2026 (выписка Kaspi ···7183 — 15.10.2026, Halyk ···0001 — 12.10.2026)", (string)o["say"]);
            Assert.Equal("2026-10-12", (string)((JArray)o["bank_statements"])[1]["last_statement"]);
            Assert.True((bool)((JArray)o["bank_statements"])[1]["stale"]);
            Assert.Single((JArray)o["warnings"]);
        }

        [Fact]
        public void McpInstructions_RequireDate()
        {
            Assert.Contains("as_of", Txt.S("McpInstructions"));
            Assert.Contains("по данным банка", Txt.S("McpInstructions"));
        }
    }
}
