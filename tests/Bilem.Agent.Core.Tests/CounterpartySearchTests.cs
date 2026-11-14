using System.Linq;
using Bilem.Agent.Core.Mcp;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Bilem.Agent.Core.Tests
{
    /// <summary>#101: контрагент по тому, как его назвали голосом или текстом — без пробелов, форм собственности, в транслите.</summary>
    public class CounterpartySearchTests
    {
        private static readonly JObject[] Catalog =
        {
            Row("1", "Pro Tax & Accounting", "ТОО «Pro Tax & Accounting»"),
            Row("2", "IC Line", null),
            Row("3", "ТОО «Мост-Отряд»", null),
            Row("4", "Казахтелеком АО", null),
            Row("5", "ИП Ахметов", null),
            Row("6", "Протон Сервис", null),
        };

        private static JObject Row(string id, string name, string full) =>
            new JObject { ["Ref_Key"] = id, ["Description"] = name, ["НаименованиеПолное"] = full, ["ИдентификационныйКодЛичности"] = "" };

        private static string[] Ids(CounterpartySearch.Result r) => r.Rows.Select(x => (string)x["Ref_Key"]).ToArray();

        [Theory]
        [InlineData("protax")]
        [InlineData("Protax Company")]
        [InlineData("Prot")]
        [InlineData("Протакс")]
        [InlineData("ТОО Протакс")]
        [InlineData("pro-tax")]
        [InlineData("Pro Accounting")]
        public void ProTax(string q)
        {
            var r = CounterpartySearch.Match(Catalog, q, 10);
            Assert.Contains("1", Ids(r));
            Assert.False(r.Fuzzy);
        }

        [Fact]
        public void Prot_Cyrillic_FindsBoth()
        {
            var r = CounterpartySearch.Match(Catalog, "Прот", 10);
            Assert.Equal(new[] { "1", "6" }.OrderBy(x => x), Ids(r).OrderBy(x => x));
        }

        [Fact]
        public void DictatedLetters_IcLine_IsFuzzy()
        {
            var r = CounterpartySearch.Match(Catalog, "Ай Си Лайн", 10);
            Assert.Equal(new[] { "2" }, Ids(r));
            Assert.True(r.Fuzzy);
        }

        [Fact]
        public void Accounting_Cyrillic()
        {
            var r = CounterpartySearch.Match(Catalog, "Аккаунтинг", 10);
            Assert.Equal(new[] { "1" }, Ids(r));
        }

        [Fact]
        public void LegalForm_Alone_Stays()
        {
            // «ТОО» без другого слова — не выбрасываем, ищем как есть
            Assert.Equal(new[] { "too" }, CounterpartySearch.Compacts("ТОО"));
            Assert.Equal(new[] { "mostotryad" }, CounterpartySearch.Compacts("ТОО «Мост-Отряд»"));
        }

        [Fact]
        public void Latin_ForCyrillicName()
        {
            Assert.Contains("4", Ids(CounterpartySearch.Match(Catalog, "Kazakhtelecom", 10)));
            Assert.Contains("5", Ids(CounterpartySearch.Match(Catalog, "Akhmetov IP", 10)));
        }

        [Fact]
        public void NothingFound_GivesSimilar()
        {
            var r = CounterpartySearch.Match(Catalog, "Протаксис Консалтинг", 10);
            Assert.Empty(r.Rows);
            Assert.InRange(r.Similar.Count, 1, CounterpartySearch.SimilarTop);
            Assert.Equal("1", (string)r.Similar[0]["Ref_Key"]);
        }

        [Theory]
        [InlineData("Зюзя")]
        [InlineData("Зюзя Компани")]
        public void Garbage_NoSimilar(string q)
        {
            var r = CounterpartySearch.Match(Catalog.Append(Row("7", "Сертификационный центр Компас", null)), q, 10);
            Assert.Empty(r.Rows);
            Assert.Empty(r.Similar);
        }
    }
}
