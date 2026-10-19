using System;
using Xunit;

namespace Bilem.Agent.Core.Tests
{
    /// <summary>beta.5: системная регистрация COM-коннектора — запомнить прежнюю и не перетереть её своей; падения из журнала событий.</summary>
    public class Beta5Tests
    {
        private static Platform1C P(string ver) => new Platform1C { Version = new Version(ver), Bits = 32, Bin = $@"C:\Program Files (x86)\1cv8\{ver}\bin" };

        [Fact]
        public void RememberCom_SecondTime_KeepsFirstPrevious()
        {
            var cfg = new AgentConfig();
            Platform.RememberCom(cfg, P("8.3.24.1342"));
            var first = cfg.ComBefore["32"].Previous;
            // Повторная настройка с другой платформой: «прежней» не становится наша же регистрация
            cfg.ComBefore["32"].Previous = @"C:\Program Files (x86)\1cv8\8.3.15.1869\bin\comcntr.dll";
            Platform.RememberCom(cfg, P("8.3.27.2342"));
            Assert.Equal(@"C:\Program Files (x86)\1cv8\8.3.15.1869\bin\comcntr.dll", cfg.ComBefore["32"].Previous);
            Assert.EndsWith(@"8.3.27.2342\bin\comcntr.dll", cfg.ComBefore["32"].Ours);
            Assert.NotNull(first);
        }

        [Fact]
        public void RestoreCom_NothingRemembered_DoesNothing()
        {
            var cfg = new AgentConfig();
            Assert.Null(Platform.RestoreCom(cfg, 32));
            Assert.Null(Platform.RestoreCom(cfg, 64));
        }

        [Fact]
        public void RestoreCom_RegistrationChangedByOthers_LeavesIt()
        {
            // Сейчас зарегистрирован не «наш» файл — значит, после нас регистрацию сменили: не трогаем, запись забываем
            var cfg = new AgentConfig();
            cfg.ComBefore["32"] = new ComRegistration { Previous = "", Ours = @"C:\nowhere\comcntr.dll", At = DateTime.Now };
            var msg = Platform.RestoreCom(cfg, 32);
            Assert.NotNull(msg);
            Assert.False(cfg.ComBefore.ContainsKey("32"));
        }

        [Fact]
        public void ComBefore_SurvivesJson()
        {
            var cfg = new AgentConfig();
            cfg.ComBefore["64"] = new ComRegistration { Previous = @"C:\a\comcntr.dll", Ours = @"C:\b\comcntr.dll", At = new DateTime(2026, 10, 19) };
            var back = Newtonsoft.Json.JsonConvert.DeserializeObject<AgentConfig>(Newtonsoft.Json.JsonConvert.SerializeObject(cfg));
            Assert.Equal(@"C:\a\comcntr.dll", back.ComBefore["64"].Previous);
        }

        [Theory]
        [InlineData("CN=SKP.KZ LLC, O=SKP.KZ LLC, L=Plano, S=Texas, C=US", true)]   // сертификат Microsoft (F7)
        [InlineData("CN=SKP.kz LLC, O=SKP.kz LLC", true)]
        [InlineData("CN=\"SKP.KZ LLC\", O=X", true)]
        [InlineData("CN=Not SKP.KZ LLC, O=SKP.KZ LLC", false)]   // вхождение подстроки — не наш
        [InlineData("CN=Evil, O=SKP.KZ LLC", false)]             // O совпадает, CN — нет
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Publisher_ExactCnIgnoringCase(string subject, bool ours) =>
            Assert.Equal(ours, Bilem.Agent.Core.Updates.ReleaseVerifier.IsOurPublisher(subject));

        [Fact]
        public void Crashes_ReadsLogWithoutThrowing()
        {
            // На стенде падений w3wp нет — пусто, но без исключений; выдуманный процесс — тоже пусто
            Assert.Empty(Crashes.Since(DateTime.Now.AddMinutes(-5), "no-such-process.exe"));
            Assert.Null(Crashes.IisExplain(DateTime.Now));
        }
    }
}
