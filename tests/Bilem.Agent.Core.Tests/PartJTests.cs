using System;
using System.Collections.Generic;
using System.Linq;
using Bilem.Agent.Core.Web;
using Xunit;

namespace Bilem.Agent.Core.Tests
{
    /// <summary>Часть J: выбор веб-сервера без вопросов, переход на свой Apache только по падению IIS, конфиг своего Apache.</summary>
    public class PartJTests
    {
        private static void Sim(string what, Action a)
        {
            Environment.SetEnvironmentVariable("BILEM_SIMULATE", what);
            try { a(); } finally { Environment.SetEnvironmentVariable("BILEM_SIMULATE", null); }
        }

        [Fact]
        public void Decide_AutoTable()
        {
            Assert.Equal("iis", WebChoice.Decide(new WebDetect { IisInstalled = true, ForeignApache = new List<string> { "Apache2.4" } }, "auto"));
            Assert.Equal("apache", WebChoice.Decide(new WebDetect { ForeignApache = new List<string> { "Apache2.4" } }, "auto"));
            Assert.Equal("apache", WebChoice.Decide(new WebDetect { OurApache = true }, null));
            Assert.Equal("iis", WebChoice.Decide(new WebDetect(), "auto"));             // ничего нет — включаем IIS
            Assert.Equal("apache", WebChoice.Decide(new WebDetect { IisInstalled = true }, "apache"));
            Assert.Equal("iis", WebChoice.Decide(new WebDetect { OurApache = true }, "IIS"));
        }

        [Fact]
        public void Fallback_OnlyOnIisCrash()
        {
            Assert.Equal("J6IisCrash", WebChoice.FallbackReason("iis", "auto", 0, null));          // обрыв / пустой ответ
            Assert.Equal("J6Iis503", WebChoice.FallbackReason("iis", "auto", 503, null));
            Assert.Equal("J6IisCrash", WebChoice.FallbackReason("iis", null, 200, "w3wp.exe 1000"));
            // пароль, права, лицензия — не повод
            Assert.Null(WebChoice.FallbackReason("iis", "auto", 401, null));
            Assert.Null(WebChoice.FallbackReason("iis", "auto", 403, null));
            Assert.Null(WebChoice.FallbackReason("iis", "auto", 500, null));
            // ручной режим и уже Apache — не переключаем
            Assert.Null(WebChoice.FallbackReason("iis", "iis", 0, "w3wp.exe 1000"));
            Assert.Null(WebChoice.FallbackReason("apache", "auto", 0, null));
        }

        [Fact]
        public void Simulate_IisCrash_AndPortBusy()
        {
            Sim("iis_crash", () =>
            {
                Assert.Equal("J6IisCrash", WebChoice.FallbackReason("iis", "auto", 200, null));
                Assert.Null(WebChoice.FallbackReason("iis", "iis", 200, null));
            });
            Sim("port_busy", () => Assert.NotEqual(18080, WebChoice.FreePort()));
            Sim("dism_fail,no_vcredist", () =>
            {
                Assert.True(WebSim.On("dism_fail"));
                Assert.True(WebSim.On("NO_VCREDIST"));
                Assert.False(WebSim.On("iis_crash"));
            });
        }

        [Fact]
        public void BaseConf_LocalhostOnly_LiteralRoot()
        {
            var c = ApachePublisher.BaseConf(32, 18085);
            Assert.Contains("Listen 127.0.0.1:18085", c);
            Assert.DoesNotContain("Define", c);                  // под виртуальной учёткой ServerRoot через ${…} не работает
            Assert.Contains("/apache/x86\"", c);
            Assert.Contains("Require all denied", c);
            Assert.DoesNotContain("\\", c.Replace("\r\n", "\n").Replace("\n", ""));   // только прямые слэши
            Assert.Equal(18085, ApachePublisher.ParsePort(c));
            Assert.Equal(32, ApachePublisher.ParseBits(c));
            Assert.Equal(64, ApachePublisher.ParseBits(ApachePublisher.BaseConf(64, 18080)));
            Assert.Equal(0, ApachePublisher.ParsePort("Listen 18080"));   // не на 127.0.0.1 — не наш
            Assert.Equal(0, ApachePublisher.ParsePort(""));
        }

        [Fact]
        public void Harden_LocalhostAndDedupModule()
        {
            var p = new Platform1C { Bits = 32, Bin = @"C:\Program Files (x86)\1cv8\8.3.27.2342\bin" };
            var mod = "LoadModule _1cws_module \"C:/Program Files (x86)/1cv8/8.3.27.2342/bin/wsap24.dll\"";
            var conf = mod + "\r\n<Directory \"x\">\r\n    Require all granted\r\n</Directory>\r\n" + mod + "\r\n";
            var h = ApachePublisher.Harden(conf, p);
            Assert.Contains("Require ip 127.0.0.1 ::1", h);
            Assert.DoesNotContain("Require all granted", h);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(h, "_1cws_module").Cast<System.Text.RegularExpressions.Match>());

            var other = conf + "LoadModule _1cws_module \"C:/Program Files (x86)/1cv8/8.3.24.1000/bin/wsap24.dll\"\r\n";
            Assert.Throws<AgentException>(() => ApachePublisher.Harden(other, p));   // две версии платформы в одном Apache
        }
    }
}
