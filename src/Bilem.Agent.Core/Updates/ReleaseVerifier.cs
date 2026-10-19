using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Bilem.Agent.Core.Updates
{
    /// <summary>
    /// Проверка релиза перед установкой: подпись Ed25519 файла SHA256SUMS (открытый ключ зашит в агент, закрытый — только секрет
    /// сборки на GitHub) → хеш MSI из SHA256SUMS → если MSI подписан Authenticode — подпись действительна и издатель «SKP.kz LLC».
    /// </summary>
    public static class ReleaseVerifier
    {
        /// <summary>Открытый ключ подписи релизов Bilem Agent (Ed25519, 32 байта).</summary>
        public const string PublicKeyBase64 = "iztFn5WiOBn1yuFVOqaz5aiFV8jP+y2+ll+aOfEPmII=";
        public const string Publisher = "SKP.kz LLC";
        /// <summary>Только для тестов: свой открытый ключ вместо зашитого.</summary>
        internal static byte[] KeyForTests { get; set; }

        public static bool VerifySignature(byte[] data, byte[] signature, byte[] publicKey = null)
        {
            if (data == null || signature == null || signature.Length != 64) return false;
            try
            {
                var v = new Ed25519Signer();
                v.Init(false, new Ed25519PublicKeyParameters(publicKey ?? KeyForTests ?? Convert.FromBase64String(PublicKeyBase64), 0));
                v.BlockUpdate(data, 0, data.Length);
                return v.VerifySignature(signature);
            }
            catch { return false; }
        }

        /// <summary>SHA256SUMS: строки «хеш  имя» (или «хеш *имя») → имя файла → хеш (нижний регистр).</summary>
        public static Dictionary<string, string> ParseSums(string text)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in (text ?? "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length < 66) continue;
                var hash = line.Substring(0, 64);
                if (!System.Text.RegularExpressions.Regex.IsMatch(hash, "^[0-9a-fA-F]{64}$")) continue;
                var name = line.Substring(64).Trim().TrimStart('*').Trim();
                if (name.Length > 0) d[name] = hash.ToLowerInvariant();
            }
            return d;
        }

        public static string Sha256File(string path)
        {
            using (var s = File.OpenRead(path))
            using (var h = SHA256.Create())
            {
                var b = h.ComputeHash(s);
                var sb = new StringBuilder(64);
                foreach (var x in b) sb.Append(x.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// Полная проверка скачанного: подпись SHA256SUMS, хеш файла, Authenticode (если есть). null — всё хорошо, иначе текст отказа.
        /// </summary>
        public static string Check(string file, byte[] sums, byte[] sig, byte[] publicKey = null)
        {
            if (!VerifySignature(sums, sig, publicKey)) return Txt.S("UpdBadSignature");
            var list = ParseSums(Encoding.UTF8.GetString(sums));
            if (!list.TryGetValue(Path.GetFileName(file), out var want)) return Txt.F("UpdNotInSums", Path.GetFileName(file));
            if (!string.Equals(Sha256File(file), want, StringComparison.OrdinalIgnoreCase)) return Txt.S("UpdBadHash");
            var a = Authenticode(file);
            if (a.Signed && !a.Valid) return Txt.F("UpdBadAuthenticode", a.Error);
            if (a.Signed && !IsOurPublisher(a.Subject)) return Txt.F("UpdWrongPublisher", a.Subject);
            return null;
        }

        /// <summary>
        /// Издатель подписи — наш: CN сертификата равен «SKP.kz LLC» без учёта регистра (в сертификате Microsoft —
        /// «CN=SKP.KZ LLC, O=SKP.KZ LLC, …»). Точное имя, а не вхождение подстроки: «CN=Not SKP.KZ LLC» — чужой.
        /// </summary>
        public static bool IsOurPublisher(string subject) =>
            string.Equals(SubjectPart(subject, "CN"), Publisher, StringComparison.OrdinalIgnoreCase);

        /// <summary>Значение поля DN («CN=…, O=…»); кавычки вокруг значения снимаются. Нет поля — null.</summary>
        internal static string SubjectPart(string subject, string key)
        {
            if (string.IsNullOrEmpty(subject)) return null;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(subject, @"(?:^|,)\s*([A-Za-z.]+)=(""(?:[^""]|"""")*""|[^,]*)"))
            {
                if (!string.Equals(m.Groups[1].Value, key, StringComparison.OrdinalIgnoreCase)) continue;
                var v = m.Groups[2].Value.Trim();
                if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"') v = v.Substring(1, v.Length - 2).Replace("\"\"", "\"");
                return v;
            }
            return null;
        }

        /// <summary>Подпись Authenticode файла: есть ли, действительна ли (WinVerifyTrust), кто издатель.</summary>
        public static (bool Signed, bool Valid, string Subject, string Error) Authenticode(string file)
        {
            string subject;
            try { subject = X509Certificate.CreateFromSignedFile(file).Subject; }
            catch { return (false, false, null, null); }
            var rc = WinVerify(file);
            return (true, rc == 0, subject, rc == 0 ? null : "0x" + rc.ToString("X8"));
        }

        // ---------- WinVerifyTrust ----------

        private static readonly Guid ActionGenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            public uint cbStruct; public string pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustData
        {
            public uint cbStruct; public IntPtr pPolicyCallbackData; public IntPtr pSIPClientData; public uint dwUIChoice; public uint fdwRevocationChecks;
            public uint dwUnionChoice; public IntPtr pFile; public uint dwStateAction; public IntPtr hWVTStateData; public IntPtr pwszURLReference;
            public uint dwProvFlags; public uint dwUIContext; public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        private static extern uint WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WinTrustData data);

        private static uint WinVerify(string file)
        {
            var fi = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo)), pcwszFilePath = file };
            var pFile = Marshal.AllocHGlobal(Marshal.SizeOf(fi));
            try
            {
                Marshal.StructureToPtr(fi, pFile, false);
                // UI нет, отзыв — по всей цепочке, проверка одного файла
                var d = new WinTrustData { cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustData)), dwUIChoice = 2, fdwRevocationChecks = 1, dwUnionChoice = 1, pFile = pFile };
                return WinVerifyTrust(IntPtr.Zero, ActionGenericVerifyV2, ref d);
            }
            finally
            {
                Marshal.DestroyStructure(pFile, typeof(WinTrustFileInfo));
                Marshal.FreeHGlobal(pFile);
            }
        }
    }
}
