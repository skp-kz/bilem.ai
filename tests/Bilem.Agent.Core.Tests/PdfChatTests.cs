using Bilem.Agent.Core;
using Bilem.Agent.Core.Mcp;
using Xunit;

namespace Bilem.Agent.Core.Tests
{
    /// <summary>PDF в чат (решение 0018): файл до 5 МБ — ресурсом, больше — текстом, чтобы ответ не потерялся целиком.</summary>
    public class PdfChatTests
    {
        [Fact]
        public void BigPdf_IsTextNotBlob()
        {
            var big = new byte[Tools.MaxFileBytes + 1];
            var c = Tools.PdfContent(@"C:\Bilem\docs\Акт сверки.pdf", big);
            Assert.Single(c);
            Assert.Equal("text", (string)c[0]["type"]);
            Assert.Equal(Txt.F("ToolFileTooBig", "Акт сверки.pdf", 6), (string)c[0]["text"]);
        }

        [Fact]
        public void SmallPdf_IsResourceBlob()
        {
            var c = Tools.PdfContent(@"C:\Bilem\docs\Счёт 12.pdf", new byte[] { 1, 2, 3 });
            Assert.Equal("resource", (string)c[0]["type"]);
            Assert.Equal("application/pdf", (string)c[0]["resource"]["mimeType"]);
            Assert.Equal("Счёт 12.pdf", (string)c[0]["resource"]["name"]);
            Assert.Equal("AQID", (string)c[0]["resource"]["blob"]);
        }
    }
}
