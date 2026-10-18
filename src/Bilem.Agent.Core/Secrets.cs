using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Bilem.Agent.Core
{
    /// <summary>
    /// Секреты агента (пароль bilem, токен MCP): DPAPI в области LocalMachine — чтобы потом прочитала служба,
    /// и права на файл только у Администраторов и SYSTEM (LocalMachine-DPAPI расшифрует любой процесс машины,
    /// поэтому защищает именно ACL файла).
    /// </summary>
    public static class Secrets
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Bilem.Agent/v1");

        public static string Protect(string plain)
        {
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.LocalMachine);
            return Convert.ToBase64String(bytes);
        }

        public static string Unprotect(string protectedBase64)
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), Entropy, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(bytes);
        }

        public static void WriteSecretFile(string path, string plain)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, Protect(plain), Encoding.ASCII);
            RestrictToAdmins(path);
        }

        public static string ReadSecretFile(string path) => Unprotect(File.ReadAllText(path, Encoding.ASCII).Trim());

        /// <summary>Права на файл — только Администраторы и SYSTEM, без наследования.</summary>
        public static void RestrictToAdmins(string path)
        {
            try
            {
                var sec = new FileSecurity();
                sec.SetAccessRuleProtection(true, false);
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
                File.SetAccessControl(path, sec);
            }
            catch (UnauthorizedAccessException)
            {
                // Без прав администратора (тесты) — оставляем как есть
            }
        }

        /// <summary>Права на папку — только Администраторы и SYSTEM (с наследованием на файлы внутри), без наследования сверху.</summary>
        public static void RestrictDirToAdmins(string path)
        {
            try
            {
                var sec = new DirectorySecurity();
                sec.SetAccessRuleProtection(true, false);
                const InheritanceFlags inh = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
                Directory.SetAccessControl(path, sec);
            }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>Случайный пароль/токен из безопасного алфавита (без похожих символов; безопасен для строки соединения).</summary>
        public static string RandomPassword(int length = 24)
        {
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
            var bytes = new byte[length];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var sb = new StringBuilder(length);
            foreach (var b in bytes) sb.Append(alphabet[b % alphabet.Length]);
            return sb.ToString();
        }

        /// <summary>Токен MCP: 32 случайных байта в base64url.</summary>
        public static string RandomToken()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>Сравнение за постоянное время.</summary>
        public static bool ConstantTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            var x = Encoding.UTF8.GetBytes(a);
            var y = Encoding.UTF8.GetBytes(b);
            var diff = x.Length ^ y.Length;
            for (int i = 0; i < Math.Min(x.Length, y.Length); i++) diff |= x[i] ^ y[i];
            return diff == 0;
        }
    }
}
