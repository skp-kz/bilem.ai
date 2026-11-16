using System.Collections.Generic;
using Bilem.Agent.Core;
using Bilem.Agent.Core.Mcp;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Bilem.Agent.Core.Tests
{
    /// <summary>#100: новый контрагент по черновику — БИН, вид, «такой же уже есть», одноразовый черновик, роль по умолчанию.</summary>
    public class CounterpartyCreateTests
    {
        [Theory]
        [InlineData("123456789012", "123456789012")]
        [InlineData(" 1234 5678 9012 ", "123456789012")]
        [InlineData("1234-5678-9012", "123456789012")]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void BinNormalized(string input, string want) => Assert.Equal(want, CounterpartyCreate.Bin(input));

        [Theory]
        [InlineData("12345678901")]
        [InlineData("1234567890123")]
        [InlineData("12345678901A")]
        public void BinMustBe12Digits(string input)
        {
            var e = Assert.Throws<AgentException>(() => CounterpartyCreate.Bin(input));
            Assert.StartsWith("БИН/ИИН — это 12 цифр", e.Message);
        }

        [Theory]
        [InlineData(null, "Ай-Си Групп ТОО", "ЮрЛицо")]
        [InlineData(null, "ИП Иванов", "ФизЛицо")]
        [InlineData(null, "Иванов ИП", "ФизЛицо")]
        [InlineData(null, "Типография", "ЮрЛицо")]
        [InlineData("ФизЛицо", "Ай-Си Групп ТОО", "ФизЛицо")]
        [InlineData("ЮрЛицо", "ИП Иванов", "ЮрЛицо")]
        [InlineData("физ", "x", "ФизЛицо")]
        public void KindByInputOrName(string kind, string name, string want) => Assert.Equal(want, CounterpartyCreate.Kind(kind, name));

        [Fact]
        public void KindRejectsUnknown() => Assert.Throws<AgentException>(() => CounterpartyCreate.Kind("банк", "x"));

        [Theory]
        [InlineData("Ай-Си Групп", true)]
        [InlineData("ТОО «Ай Си Групп»", true)]
        [InlineData("айси групп", true)]
        [InlineData("Ай-Си", false)]
        [InlineData("Ай-Си Групп Плюс", false)]
        public void SameNameIsWholeKey(string query, bool same)
        {
            var row = new JObject { ["Description"] = "Ай-Си Групп ТОО", ["НаименованиеПолное"] = "Товарищество «Ай-Си Групп»" };
            Assert.Equal(same, CounterpartyCreate.SameName(row, query));
        }

        [Fact]
        public void DraftKindIsSeparateFromInvoice()
        {
            InvoiceCreate.ClearDrafts();
            var d = InvoiceCreate.Save("v1", "local", new JObject { ["Description"] = "Новая" }, "Новая", 0m, "cp-");
            d.Kind = InvoiceCreate.KindCounterparty;
            Assert.Matches("^cp-[0-9a-f]{12}$", d.Id);
            // Черновик контрагента не создаёт счёт, и наоборот
            Assert.Throws<AgentException>(() => InvoiceCreate.Take(d.Id, "local"));
            var foreign = Assert.Throws<AgentException>(() => InvoiceCreate.Take(d.Id, "cloud:whatsapp:+7", InvoiceCreate.KindCounterparty));
            Assert.Equal(Txt.S("CpDraftForeign"), foreign.Message);
            Assert.Same(d, InvoiceCreate.Take(d.Id, "local", InvoiceCreate.KindCounterparty));
            var again = Assert.Throws<AgentException>(() => InvoiceCreate.Take(d.Id, "local", InvoiceCreate.KindCounterparty));
            Assert.Equal(Txt.S("InvDraftUsed"), again.Message);
            Assert.Equal(Txt.S("CpHintNewDraft"), again.Hint);
        }

        [Fact]
        public void EnabledOnlyByAccessWriteAndDefault()
        {
            Assert.False(CounterpartyCreate.Enabled(new BaseConfig()));
            Assert.False(CounterpartyCreate.Enabled(new BaseConfig { Write = new List<string> { InvoiceCreate.DocFullName } }));
            Assert.True(CounterpartyCreate.Enabled(new BaseConfig { Write = new List<string> { CounterpartyCreate.FullName } }));
            Assert.Contains(CounterpartyCreate.FullName, Access.WriteWhitelist);
            // Роль на контрагентов — в наборе по умолчанию: новая настройка и «Обновить права Bilem»
            Assert.Contains(CounterpartyCreate.FullName, Access.DefaultWrite);
        }

        [Fact]
        public void ToolsListed()
        {
            var tools = Tools.Create(() => new List<BaseRuntime>(), 100);
            Assert.Contains(tools, t => t.Name == CounterpartyCreate.DraftTool);
            Assert.Contains(tools, t => t.Name == CounterpartyCreate.CreateTool);
            Assert.True(CounterpartyCreate.IsTool("draft_counterparty"));
            Assert.False(CounterpartyCreate.IsTool("draft_invoice"));
        }
    }
}
