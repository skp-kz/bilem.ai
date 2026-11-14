using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Bilem.Agent.Core;
using Bilem.Agent.Core.Mcp;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Bilem.Agent.Core.Tests
{
    /// <summary>#99: счёт на оплату по черновику — суммы, НДС, одноразовый черновик, только POST счёта.</summary>
    public class InvoiceCreateTests
    {
        [Theory]
        [InlineData(null, null, 500000.0, 1.0, 500000.0, 500000.0)]
        [InlineData(3.0, 1000.0, null, 3.0, 1000.0, 3000.0)]
        [InlineData(3.0, null, 1000.0, 3.0, 333.33, 1000.0)]
        [InlineData(2.0, 150.5, 301.0, 2.0, 150.5, 301.0)]
        public void Amounts(double? qty, double? price, double? sum, double q, double p, double s)
        {
            var r = InvoiceCreate.Amounts((decimal?)qty, (decimal?)price, (decimal?)sum);
            Assert.Equal((decimal)q, r.Qty);
            Assert.Equal((decimal)p, r.Price);
            Assert.Equal((decimal)s, r.Sum);
        }

        [Fact]
        public void AmountsRejectNonsense()
        {
            Assert.Throws<AgentException>(() => InvoiceCreate.Amounts(1, null, null));
            Assert.Throws<AgentException>(() => InvoiceCreate.Amounts(0, 10, null));
            Assert.Throws<AgentException>(() => InvoiceCreate.Amounts(1, -5, null));
            Assert.Throws<AgentException>(() => InvoiceCreate.Amounts(2, 100, 300));
        }

        [Fact]
        public void VatIncludedAndOnTop()
        {
            // 500 000 с НДС 12%: НДС = 500000 × 12 / 112
            Assert.Equal(53571.43m, InvoiceCreate.Vat(500000m, 12, true, true));
            Assert.Equal(60000m, InvoiceCreate.Vat(500000m, 12, true, false));
            Assert.Equal(0m, InvoiceCreate.Vat(500000m, 12, false, true));
            Assert.Equal(0m, InvoiceCreate.Vat(500000m, null, true, true));
            Assert.Equal(0m, InvoiceCreate.Vat(500000m, 0, true, false));
        }

        [Fact]
        public void BodySplitsGoodsAndServicesAndTotals()
        {
            var lines = new List<InvoiceCreate.Line>
            {
                new InvoiceCreate.Line { ItemKey = "s1", Service = true, Content = "Настройка сервера", Qty = 1, Price = 500000, Sum = 500000, VatRateKey = "r12", Vat = 60000 },
                new InvoiceCreate.Line { ItemKey = "g1", Service = false, UnitKey = "u1", Qty = 2, Price = 100, Sum = 200, Vat = 24 },
            };
            var b = InvoiceCreate.Body(new JObject { ["Организация_Key"] = "o" }, lines, true, false);
            Assert.Equal(560224m, (decimal)b["СуммаДокумента"]);
            Assert.False((bool)b["СуммаВключаетНДС"]);
            var srv = (JArray)b["Услуги"]; var goods = (JArray)b["Товары"];
            Assert.Single(srv); Assert.Single(goods);
            Assert.Equal("Настройка сервера", (string)srv[0]["Содержание"]);
            Assert.Equal("1", (string)srv[0]["LineNumber"]);
            Assert.Equal("u1", (string)goods[0]["ЕдиницаИзмерения_Key"]);
            Assert.Equal(InvoiceCreate.EmptyKey, (string)goods[0]["СтавкаНДС_Key"]);
            Assert.Null(b["Date"]);
            Assert.Null(b["Number"]);
            Assert.Null(b["Posted"]);
        }

        [Fact]
        public void DraftWorksOnceForSameOwnerOnly()
        {
            InvoiceCreate.ClearDrafts();
            var d = InvoiceCreate.Save("v1", "cloud:whatsapp:+7", new JObject(), "Покупатель 1", 100);
            Assert.Matches("^inv-[0-9a-f]{12}$", d.Id);
            Assert.Throws<AgentException>(() => InvoiceCreate.Take(d.Id, "cloud:whatsapp:+8"));
            Assert.Same(d, InvoiceCreate.Take(d.Id, "cloud:whatsapp:+7"));
            var again = Assert.Throws<AgentException>(() => InvoiceCreate.Take(d.Id, "cloud:whatsapp:+7"));
            Assert.Equal(Txt.S("InvDraftUsed"), again.Message);
            Assert.Throws<AgentException>(() => InvoiceCreate.Take("inv-000000000000", "cloud:whatsapp:+7"));
            Assert.Throws<AgentException>(() => InvoiceCreate.Take(null, "local"));
        }

        [Fact]
        public void DraftExpires()
        {
            InvoiceCreate.ClearDrafts();
            var now = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
            var was = InvoiceCreate.UtcNow;
            try
            {
                InvoiceCreate.UtcNow = () => now;
                var d = InvoiceCreate.Save("v1", "local", new JObject(), "Покупатель 1", 100);
                InvoiceCreate.UtcNow = () => now.AddMinutes(16);
                Assert.Throws<AgentException>(() => InvoiceCreate.Take(d.Id, "local"));
            }
            finally { InvoiceCreate.UtcNow = was; }
        }

        [Fact]
        public void OwnerFromCaller()
        {
            Assert.Equal("local", InvoiceCreate.OwnerOf(null));
            Assert.Equal("cloud:whatsapp:u1", InvoiceCreate.OwnerOf(new McpCaller { CloudChannel = "whatsapp", CloudUser = "u1", Client = "x" }));
            Assert.Equal("local:claude", InvoiceCreate.OwnerOf(new McpCaller { Client = "claude" }));
        }

        [Fact]
        public void EnabledOnlyByAccessWrite()
        {
            Assert.False(InvoiceCreate.Enabled(new BaseConfig()));
            Assert.False(InvoiceCreate.Enabled(new BaseConfig { Write = new List<string> { "Документ.Доверенность" } }));
            Assert.True(InvoiceCreate.Enabled(new BaseConfig { Write = new List<string> { InvoiceCreate.DocFullName } }));
            Assert.Contains(InvoiceCreate.DocFullName, Access.WriteWhitelist);
        }

        [Fact]
        public void PickPrefersSingleExactName()
        {
            var rows = new JArray(new JObject { ["Description"] = "Покупатель 1" }, new JObject { ["Description"] = "Покупатель 1 Сервис" });
            Assert.Equal("Покупатель 1", (string)InvoiceCreate.Pick(rows, " покупатель 1 ")["Description"]);
            Assert.Null(InvoiceCreate.Pick(rows, "Покупатель"));
            Assert.NotNull(InvoiceCreate.Pick(new JArray(new JObject { ["Description"] = "x" }), "y"));
        }

        [Fact]
        public void WordsForSimilar()
        {
            Assert.Equal(new[] { "настройка", "сервера" }, InvoiceCreate.Words("настройка сервера, ok"));
        }

        private static string J(string action, string number, string reff, string version, string user, DateTime t, string slug = "v1") =>
            new JObject
            {
                ["time"] = t.ToString("yyyy-MM-ddTHH:mm:sszzz"), ["base"] = slug, ["action"] = action,
                ["requested_by"] = new JObject { ["client"] = "x", ["cloud_channel"] = "whatsapp", ["cloud_user"] = user },
                ["created"] = new JObject { ["number"] = number, ["ref"] = reff, ["version"] = version, ["buyer"] = "Покупатель 1" },
            }.ToString(Newtonsoft.Json.Formatting.None);

        [Fact]
        public void OwnInvoiceFromJournal()
        {
            var t0 = new DateTime(2026, 11, 14, 10, 0, 0, DateTimeKind.Local);
            var now = t0.ToUniversalTime().AddHours(1);
            var lines = new List<string>
            {
                J(Audit.Create, "БК00-000007", "r7", "v1", "u1", t0),
                J(Audit.InvoiceUpdate, "БК00-000007", "r7", "v2", "u1", t0.AddMinutes(5)),
                J(Audit.Create, "БК00-000008", "r8", "w1", "u2", t0),
                "не JSON",
            };
            var own = InvoiceCreate.FindOwn(lines, "v1", "7", "cloud:whatsapp:u1", now);
            Assert.Equal("r7", own.Ref);
            Assert.Equal("v2", own.Version);
            // Чужой собеседник, чужая база, нет такого номера, прошло больше 24 часов, уже помечен
            Assert.Throws<AgentException>(() => InvoiceCreate.FindOwn(lines, "v1", "8", "cloud:whatsapp:u1", now));
            Assert.Throws<AgentException>(() => InvoiceCreate.FindOwn(lines, "v2", "7", "cloud:whatsapp:u1", now));
            Assert.Throws<AgentException>(() => InvoiceCreate.FindOwn(lines, "v1", "9", "cloud:whatsapp:u1", now));
            Assert.Throws<AgentException>(() => InvoiceCreate.FindOwn(lines, "v1", "7", "cloud:whatsapp:u1", now.AddHours(24)));
            lines.Add(J(Audit.InvoiceCancel, "БК00-000007", "r7", "v3", "u1", t0.AddMinutes(9)));
            Assert.Throws<AgentException>(() => InvoiceCreate.FindOwn(lines, "v1", "7", "cloud:whatsapp:u1", now));
        }

        [Fact]
        public void DraftKindMustMatchTool()
        {
            InvoiceCreate.ClearDrafts();
            var d = InvoiceCreate.Save("v1", "local", new JObject { ["DeletionMark"] = true }, "Покупатель 1", 100);
            d.Kind = InvoiceCreate.KindCancel;
            Assert.Throws<AgentException>(() => InvoiceCreate.Take(d.Id, "local"));
            Assert.Throws<AgentException>(() => InvoiceCreate.Take(d.Id, "local", InvoiceCreate.KindUpdate));
            Assert.Same(d, InvoiceCreate.Take(d.Id, "local", InvoiceCreate.KindCancel));
        }

        /// <summary>Запись в 1С во всём ядре — только в InvoiceCreate: POST нового счёта и PATCH своего; DELETE/MERGE/PUT нет.</summary>
        [Fact]
        public void OnlyInvoicePostInCode()
        {
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Bilem.Agent.Core"));
            if (!Directory.Exists(root)) return;
            var writes = new List<string>();
            foreach (var f in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
            {
                var name = Path.GetFileName(f);
                // Проверка «только чтение» намеренно пробует запись и ждёт отказа 1С; облачная заглушка и HTTP-клиенты облака — не OData 1С
                if (name == "Verifier.cs" || f.Contains(Path.DirectorySeparatorChar + "Cloud" + Path.DirectorySeparatorChar) || f.Contains(Path.DirectorySeparatorChar + "Updates" + Path.DirectorySeparatorChar)) continue;
                foreach (Match m in Regex.Matches(File.ReadAllText(f), "Send\\(\"(POST|PATCH|PUT|DELETE|MERGE)\""))
                    writes.Add(name + ":" + m.Groups[1].Value);
            }
            Assert.Equal(new[] { "InvoiceCreate.cs:POST", "InvoiceCreate.cs:PATCH" }, writes);
        }
    }
}
