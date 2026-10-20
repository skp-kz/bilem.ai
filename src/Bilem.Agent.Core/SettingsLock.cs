using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using Newtonsoft.Json;

namespace Bilem.Agent.Core
{
    /// <summary>
    /// Пароль настроек (решение Славы 20.10): дополнительный короткий пароль на окно «Настройки» и на «Скопировать адрес MCP» —
    /// чтобы на сервере с несколькими администраторами (аутсорсеры) никто лишний не лазил. Хранится не сам пароль, а PBKDF2-SHA256
    /// с солью; после 5 неверных попыток — пауза минуту. Забыли — сброс из командной строки администратора (с записью в журнал).
    /// </summary>
    public static class SettingsLock
    {
        public const int MinLength = 4;
        public const int MaxFails = 5;
        public static readonly TimeSpan Pause = TimeSpan.FromMinutes(1);
        private const int Iterations = 100000;

        public static bool Enabled => !string.IsNullOrEmpty(AgentConfig.Load().SettingsPassword);

        /// <summary>«pbkdf2-sha256$итерации$соль$хеш» (base64).</summary>
        internal static string Hash(string password, byte[] salt = null, int iterations = Iterations)
        {
            salt = salt ?? RandomSalt();
            using (var kdf = new Rfc2898DeriveBytes(password ?? "", salt, iterations, HashAlgorithmName.SHA256))
                return $"pbkdf2-sha256${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(kdf.GetBytes(32))}";
        }

        internal static bool Matches(string stored, string password)
        {
            var p = (stored ?? "").Split('$');
            if (p.Length != 4 || p[0] != "pbkdf2-sha256" || !int.TryParse(p[1], out var it)) return false;
            byte[] salt, want;
            try { salt = Convert.FromBase64String(p[2]); want = Convert.FromBase64String(p[3]); } catch { return false; }
            using (var kdf = new Rfc2898DeriveBytes(password ?? "", salt, it, HashAlgorithmName.SHA256))
            {
                var got = kdf.GetBytes(want.Length);
                var diff = 0;
                for (var i = 0; i < want.Length; i++) diff |= got[i] ^ want[i];   // сравнение за постоянное время
                return diff == 0;
            }
        }

        private static byte[] RandomSalt()
        {
            var s = new byte[16];
            using (var r = RandomNumberGenerator.Create()) r.GetBytes(s);
            return s;
        }

        public static string WhoAmI()
        {
            try { using (var id = WindowsIdentity.GetCurrent()) return id.Name; } catch { return Environment.UserName; }
        }

        // ---------- Неверные попытки: файл состояния (пишут только администраторы — окно настроек и так с UAC) ----------

        private sealed class Fails { public int Count; public DateTime? Until; }
        private static string FailsFile => Path.Combine(Paths.StateDir, "settings-lock.json");

        private static Fails LoadFails()
        {
            try { return File.Exists(FailsFile) ? JsonConvert.DeserializeObject<Fails>(File.ReadAllText(FailsFile)) ?? new Fails() : new Fails(); }
            catch { return new Fails(); }
        }

        private static void SaveFails(Fails f)
        {
            try { Directory.CreateDirectory(Paths.StateDir); File.WriteAllText(FailsFile, JsonConvert.SerializeObject(f)); } catch { }
        }

        /// <summary>Сколько ещё ждать после 5 неверных попыток; null — можно вводить.</summary>
        public static TimeSpan? LockedFor()
        {
            var f = LoadFails();
            return f.Until.HasValue && f.Until.Value > DateTime.Now ? f.Until.Value - DateTime.Now : (TimeSpan?)null;
        }

        /// <summary>Проверить пароль на входе: верно — сброс счётчика и строка «вошёл в настройки»; неверно — счётчик, после 5 — пауза.</summary>
        public static bool TryUnlock(string password, string what)
        {
            if (LockedFor() != null) return false;
            var stored = AgentConfig.Load().SettingsPassword;
            if (string.IsNullOrEmpty(stored)) return true;
            var f = LoadFails();
            if (Matches(stored, password))
            {
                SaveFails(new Fails());
                Log.Info(Txt.F("LockUnlocked", what, WhoAmI()));
                return true;
            }
            f.Count++;
            if (f.Count >= MaxFails) { f.Until = DateTime.Now + Pause; f.Count = 0; Log.Warn(Txt.F("LockPaused", WhoAmI(), MaxFails)); }
            else Log.Warn(Txt.F("LockWrong", what, WhoAmI()));
            SaveFails(f);
            return false;
        }

        public static void Set(string password)
        {
            if ((password ?? "").Length < MinLength) throw new AgentException(Txt.F("LockTooShort", MinLength));
            var cfg = AgentConfig.Load();
            var had = !string.IsNullOrEmpty(cfg.SettingsPassword);
            cfg.SettingsPassword = Hash(password);
            cfg.Save();
            Log.Ok(Txt.F(had ? "LockChanged" : "LockSet", WhoAmI()));
        }

        public static void Clear(string how)
        {
            var cfg = AgentConfig.Load();
            if (string.IsNullOrEmpty(cfg.SettingsPassword)) return;
            cfg.SettingsPassword = null;
            cfg.Save();
            SaveFails(new Fails());
            Log.Warn(Txt.F("LockCleared", how, WhoAmI()));
        }
    }
}
