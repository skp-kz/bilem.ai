using System;
using Bilem.Agent.Core;
using Xunit;

namespace Bilem.Agent.Core.Tests
{
    /// <summary>«Скопировать лог» / «Сохранить лог…» в окне «Настройка базы» (stand #75, 10.11).</summary>
    public class RunLogTests
    {
        [Fact]
        public void FileName_HasKindBaseAndDate()
        {
            Assert.Equal("bilem-setup-sentis101126-2026-11-10-1825.log", RunLog.FileName("setup", "sentis101126", new DateTime(2026, 11, 10, 18, 25, 0)));
            Assert.Equal("bilem-verify-2026-11-10-0905.log", RunLog.FileName("verify", null, new DateTime(2026, 11, 10, 9, 5, 0)));
        }

        [Fact]
        public void Mask_HidesPasswordsAndTokens()
        {
            var s = RunLog.Mask("Srvr=\"srv\";Ref=\"buh\";Usr=\"Администратор\";Pwd=\"Секрет;1\";\n"
                + "1cv8.exe DESIGNER /N\"bilem\" /PСекрет2 /DisableStartupDialogs\n"
                + "password: qwerty123\nhttp://localhost:8765/mcp/abcDEF123-_x\nAuthorization: Bearer eyJabc.def");
            Assert.DoesNotContain("Секрет", s);
            Assert.DoesNotContain("qwerty123", s);
            Assert.DoesNotContain("abcDEF123", s);
            Assert.DoesNotContain("eyJabc", s);
            Assert.Contains("Usr=\"Администратор\"", s);
            Assert.Contains("/DisableStartupDialogs", s);
        }

        [Fact]
        public void Build_ListsStepsAndLog()
        {
            var steps = new[]
            {
                new Step { Id = "admin", Title = "Права администратора", Status = StepStatus.Ok },
                new Step { Id = "user", Title = "Пользователь bilem", Status = StepStatus.Fail, Detail = "нет доступа" },
            };
            var s = RunLog.Build("Настройка базы", "sentis101126", new DateTime(2026, 11, 10, 18, 25, 0), steps, "Шаг 2 из 9 — ошибка", "Не получилось", "18:25:01 [FAIL] Pwd=123");
            Assert.Contains("База: sentis101126", s);
            Assert.Contains("2. Пользователь bilem", s);
            Assert.Contains("нет доступа", s);
            Assert.Contains("Шаг 2 из 9 — ошибка", s);
            Assert.DoesNotContain("Pwd=123", s);
        }
    }
}
