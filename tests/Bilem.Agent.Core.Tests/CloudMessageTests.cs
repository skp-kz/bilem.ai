using Bilem.Agent.Core;
using Bilem.Agent.Core.Cloud;
using Xunit;

namespace Bilem.Agent.Core.Tests
{
    /// <summary>Ошибки облака — словами (stand #75, замечание по beta.4): не «нет такого адреса», а что случилось.</summary>
    public class CloudMessageTests
    {
        [Fact]
        public void NotFound_IsOldServer_5xx_IsDown()
        {
            // Старое облако (до v1.1) на незнакомый адрес отвечает 404 not_found «нет такого адреса»
            Assert.Equal(Txt.S("CloudOldServer"), CloudApi.Message("not_found", "Нет такого адреса", 404));
            Assert.Equal(Txt.S("CloudOldServer"), CloudApi.Message("http_404", null, 404));
            // Заявка неизвестна — свой текст, не «старый сервер»
            Assert.Equal(Txt.S("CloudRequestNotFound"), CloudApi.Message("request_not_found", "заявка неизвестна", 404));
            Assert.Equal(Txt.F("CloudDown", 502), CloudApi.Message("http_502", "<html>Bad Gateway</html>", 502));
            Assert.Equal(Txt.F("CloudDown", 500), CloudApi.Message("internal", "stack trace", 500));
            // Прочее 4xx — текст облака как раньше
            Assert.Equal("лимит", CloudApi.Message("limit", "лимит", 402));
        }
    }
}
