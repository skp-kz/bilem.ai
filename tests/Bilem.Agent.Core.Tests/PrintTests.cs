using System;
using System.Text;
using Bilem.Agent.Core.Print;
using Xunit;

namespace Bilem.Agent.Core.Tests
{
    /// <summary>v0.2, часть 2: печатные формы.</summary>
    public class PrintTests
    {
        [Theory]
        [InlineData(50000, "Пятьдесят тысяч тенге 00 тиын")]
        [InlineData(33020, "Тридцать три тысячи двадцать тенге 00 тиын")]
        [InlineData(1, "Один тенге 00 тиын")]
        [InlineData(2001.5, "Две тысячи один тенге 50 тиын")]
        [InlineData(1211000.07, "Один миллион двести одиннадцать тысяч тенге 07 тиын")]
        [InlineData(0, "Ноль тенге 00 тиын")]
        public void AmountInWords(double sum, string expected) => Assert.Equal(expected, Words.Tenge((decimal)sum));

        [Fact]
        public void DateAndMoney()
        {
            Assert.Equal("13 апреля 2022 г.", Words.Date(new DateTime(2022, 4, 13)));
            Assert.Equal("50 000,00", Words.Money(50000m));
            Assert.Equal("2,5", Words.Qty(2.5m));
        }

        [Theory]
        [InlineData("00-000004", "4")]
        [InlineData("ЛК00-000123", "123")]
        [InlineData("0000000010", "10")]
        [InlineData("ДЕМО-А", "ДЕМО-А")]
        public void PrintNumber(string number, string expected) => Assert.Equal(expected, InvoiceSource.PrintNumber(number));

        [Fact]
        public void FileName_ShortBuyer_NoBadChars()
        {
            var inv = new InvoiceData { PrintNumber = "4", Date = new DateTime(2022, 4, 13), BuyerName = "Товарищество с ограниченной ответственностью \"Пример/Клиент”" };
            Assert.Equal("Счёт №4 от 13.04.2022 — ТОО Пример Клиент (demo).pdf", PrintService.FileName(inv, "demo"));
        }

        [Fact]
        public void Period_Parse()
        {
            Assert.Equal((new DateTime(2022, 1, 1), new DateTime(2022, 12, 31, 23, 59, 59)), Unwrap(InvoiceSource.ParsePeriod("2022")));
            Assert.Equal((new DateTime(2022, 4, 1), new DateTime(2022, 4, 30, 23, 59, 59)), Unwrap(InvoiceSource.ParsePeriod("2022-04")));
            Assert.Equal((new DateTime(2022, 1, 1), new DateTime(2022, 6, 30, 23, 59, 59)), Unwrap(InvoiceSource.ParsePeriod("2022-01-01..2022-06-30")));
            Assert.Equal((null, null), InvoiceSource.ParsePeriod(""));
            Assert.Throws<AgentException>(() => InvoiceSource.ParsePeriod("прошлый год"));
        }

        private static (DateTime, DateTime) Unwrap((DateTime? From, DateTime? To) p) => (p.From.Value, p.To.Value);

        [Fact]
        public void IssuedBy_DefaultAndLegacy()
        {
            Assert.Equal("Бухгалтер", new PrintSettings().IssuedByEffective);
            Assert.Equal("Директор Иванов И.И.", new PrintSettings { SignerTitle = "Директор", SignerName = "Иванов И.И." }.IssuedByEffective);
            Assert.Equal("Петров П.П.", new PrintSettings { IssuedBy = "Петров П.П.", SignerTitle = "Директор" }.IssuedByEffective);
        }

        /// <summary>PDF собирается без шрифтов Windows: встроенный PT Sans с казахскими буквами.</summary>
        [Fact]
        public void Pdf_RendersWithKazakhLetters()
        {
            var inv = new InvoiceData
            {
                Number = "00-000001", PrintNumber = "1", Date = new DateTime(2026, 10, 18), SupplierName = "ТОО «Тест әғқңөұүһі»", SupplierBin = "000000000000",
                Kbe = "17", Knp = "710", Iik = "KZ000000000000000000", BankName = "АО Банк", Bik = "TESTKZKA",
                BuyerName = "ТОО «Пример Клиент»", BuyerBin = "000000000000", Contract = "Без договора",
            };
            inv.Lines.Add(new InvoiceLine { N = 1, Code = "00000000001", Name = "Установка и настройка облачного сервера", Qty = 1, Unit = "услуга", Price = 50000, Sum = 50000 });
            var pdf = InvoicePdf.Render(inv, new PrintSettings(), null, null, null);
            Assert.True(pdf.Length > 1000);
            Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
        }
    }
}
