using System;
using Xunit;

namespace Bilem.Agent.Core.Tests
{
    /// <summary>Пароль настроек: хеш PBKDF2 с солью, проверка, пауза после 5 неверных попыток, снятие.</summary>
    public class SettingsLockTests
    {
        [Fact]
        public void Hash_SaltedAndVerifiable()
        {
            var a = SettingsLock.Hash("1234");
            var b = SettingsLock.Hash("1234");
            Assert.NotEqual(a, b);                       // соль разная
            Assert.StartsWith("pbkdf2-sha256$", a);
            Assert.DoesNotContain("1234", a);
            Assert.True(SettingsLock.Matches(a, "1234"));
            Assert.False(SettingsLock.Matches(a, "12345"));
            Assert.False(SettingsLock.Matches(a, ""));
            Assert.False(SettingsLock.Matches("мусор", "1234"));
        }

        [Fact]
        public void Unlock_WrongFiveTimes_Pauses_ThenClear()
        {
            try
            {
                SettingsLock.Set("pass1");
                Assert.True(SettingsLock.Enabled);
                Assert.Throws<AgentException>(() => SettingsLock.Set("12"));   // короче 4
                Assert.True(SettingsLock.TryUnlock("pass1", "тест"));
                for (var i = 0; i < SettingsLock.MaxFails; i++) Assert.False(SettingsLock.TryUnlock("нет", "тест"));
                Assert.NotNull(SettingsLock.LockedFor());
                Assert.False(SettingsLock.TryUnlock("pass1", "тест"));          // пауза — даже верный не принимается
                Assert.DoesNotContain("pass1", System.IO.File.ReadAllText(Paths.ConfigFile));
            }
            finally
            {
                SettingsLock.Clear("тест");
            }
            Assert.False(SettingsLock.Enabled);
            Assert.Null(SettingsLock.LockedFor());                               // снятие сбрасывает и паузу
            Assert.True(SettingsLock.TryUnlock("что угодно", "тест"));          // пароля нет — вход свободный
        }
    }
}
