using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Bilem.Agent.Core.Updates;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using Xunit;

namespace Bilem.Agent.Core.Tests
{
    /// <summary>v0.3, часть F: версии, подпись релиза Ed25519, проверка MSI, выбор релиза.</summary>
    public class UpdateTests
    {
        [Theory]
        [InlineData("0.3.0-beta.1", "0.3.0-beta.2")]
        [InlineData("0.3.0-beta.2", "0.3.0-beta.10")]
        [InlineData("0.3.0-beta.9", "0.3.0")]
        [InlineData("v0.3.0", "0.3.1")]
        [InlineData("0.9.9", "1.0.0-beta.1")]
        public void SemVer_Order(string older, string newer)
        {
            Assert.True(SemVer.Parse(older) < SemVer.Parse(newer));
            Assert.True(SemVer.Parse(newer) > SemVer.Parse(older));
        }

        [Fact]
        public void SemVer_ParseAndCurrent()
        {
            Assert.Equal("0.3.0-beta.1", SemVer.Parse("v0.3.0-beta.1").ToString());
            Assert.True(SemVer.Parse("v0.3.0-beta.1").IsPrerelease);
            Assert.False(SemVer.TryParse("latest", out _));
            Assert.NotNull(SemVer.Current);
            Assert.Equal(SemVer.Current.ToString(), Mcp.McpHost.Version);
        }

        private static (byte[] Pub, Ed25519PrivateKeyParameters Priv) Keys()
        {
            var g = new Ed25519KeyPairGenerator();
            g.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
            var kp = g.GenerateKeyPair();
            return (((Ed25519PublicKeyParameters)kp.Public).GetEncoded(), (Ed25519PrivateKeyParameters)kp.Private);
        }

        private static byte[] Sign(Ed25519PrivateKeyParameters k, byte[] data)
        {
            var s = new Ed25519Signer();
            s.Init(true, k);
            s.BlockUpdate(data, 0, data.Length);
            return s.GenerateSignature();
        }

        private static string Hex(byte[] data) { using (var h = SHA256.Create()) return string.Concat(h.ComputeHash(data).Select(b => b.ToString("x2"))); }

        /// <summary>
        /// v0.3.0-beta.2: на новой установке serve и команда url одновременно выпускали разные токены → 401.
        /// Теперь токен выпускается под замком с перечитыванием конфига — у всех один.
        /// </summary>
        [Fact]
        public void Token_ConcurrentFirstUse_SameToken()
        {
            var c = AgentConfig.Load();
            c.TokenProtected = null;
            c.Save();
            var copies = Enumerable.Range(0, 8).Select(_ => AgentConfig.Load()).ToList();
            var tokens = new System.Collections.Concurrent.ConcurrentBag<string>();
            System.Threading.Tasks.Parallel.ForEach(copies, cfg => tokens.Add(cfg.GetOrCreateToken()));
            Assert.Single(tokens.Distinct());
            Assert.Equal(tokens.First(), AgentConfig.Load().GetOrCreateToken());
        }

        [Fact]
        public void EmbeddedKey_Is32Bytes() => Assert.Equal(32, Convert.FromBase64String(ReleaseVerifier.PublicKeyBase64).Length);

        /// <summary>Подписанный релиз проходит; испорченный байт MSI, чужая подпись, подменённый SHA256SUMS — отказ.</summary>
        [Fact]
        public void Check_SignatureHashAndTamper()
        {
            var (pub, priv) = Keys();
            var dir = Path.Combine(Path.GetTempPath(), "bilem-upd-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(dir);
            var msi = Path.Combine(dir, "BilemAgent-0.3.0-beta.2.msi");
            var content = Encoding.ASCII.GetBytes("not a real msi, just bytes for the hash");
            File.WriteAllBytes(msi, content);
            var sums = Encoding.UTF8.GetBytes(Hex(content) + "  BilemAgent-0.3.0-beta.2.msi\n");
            var sig = Sign(priv, sums);

            Assert.Null(ReleaseVerifier.Check(msi, sums, sig, pub));
            // Испорченный байт MSI
            var bad = (byte[])content.Clone(); bad[5] ^= 1;
            File.WriteAllBytes(msi, bad);
            Assert.Equal(Txt.S("UpdBadHash"), ReleaseVerifier.Check(msi, sums, sig, pub));
            File.WriteAllBytes(msi, content);
            // Подпись другим ключом
            var (_, other) = Keys();
            Assert.Equal(Txt.S("UpdBadSignature"), ReleaseVerifier.Check(msi, sums, Sign(other, sums), pub));
            // Подменили SHA256SUMS под испорченный файл, подпись старая
            var sums2 = Encoding.UTF8.GetBytes(Hex(bad) + "  BilemAgent-0.3.0-beta.2.msi\n");
            Assert.Equal(Txt.S("UpdBadSignature"), ReleaseVerifier.Check(msi, sums2, sig, pub));
            // Зашитый ключ не подходит к тестовой подписи
            Assert.Equal(Txt.S("UpdBadSignature"), ReleaseVerifier.Check(msi, sums, sig));
            Directory.Delete(dir, true);
        }

        [Fact]
        public void ParseSums_Formats()
        {
            var h = new string('a', 64);
            var d = ReleaseVerifier.ParseSums(h + "  a.msi\r\n" + h.ToUpperInvariant() + " *b.msi\nмусор\n");
            Assert.Equal(h, d["a.msi"]);
            Assert.Equal(h, d["b.msi"]);
            Assert.Equal(2, d.Count);
        }

        private static string Release(string tag, bool pre, bool assets = true) =>
            "{\"tag_name\":\"" + tag + "\",\"prerelease\":" + (pre ? "true" : "false") + ",\"draft\":false,\"body\":\"заметки\",\"html_url\":\"https://x/" + tag + "\",\"assets\":" +
            (assets ? "[{\"name\":\"BilemAgent-" + tag.TrimStart('v') + ".msi\",\"browser_download_url\":\"https://d/" + tag + ".msi\"},{\"name\":\"SHA256SUMS\",\"browser_download_url\":\"https://d/" + tag + ".sums\"},{\"name\":\"SHA256SUMS.sig\",\"browser_download_url\":\"https://d/" + tag + ".sig\"}]" : "[]") + "}";

        [Fact]
        public void Latest_BetaChannelAndAssets()
        {
            var json = "[" + string.Join(",", Release("v0.3.0-beta.2", true), Release("v0.3.0-beta.1", true), Release("v0.2.0", false), Release("v0.4.0", false, assets: false), "{\"tag_name\":\"latest\",\"assets\":[]}") + "]";
            var all = Updater.Parse(json);
            Assert.Equal("0.2.0", Updater.Latest(all, beta: false).Version.ToString());       // без галочки — только выпуски
            Assert.Equal("0.3.0-beta.2", Updater.Latest(all, beta: true).Version.ToString());  // с галочкой — бета; релиз без MSI не предлагаем
        }

        /// <summary>Проверка с GitHub через подменённый HTTP: ETag (304 — из кэша), новее текущей — предлагаем.</summary>
        [Fact]
        public void Releases_ETagCache()
        {
            var old = Updater.Http;
            var calls = new List<string>();
            try
            {
                var body = Encoding.UTF8.GetBytes("[" + Release("v99.0.0", false) + "]");
                Updater.Http = (url, etag) => { calls.Add(etag ?? "-"); return etag == "\"e1\"" ? (304, new byte[0], "\"e1\"") : (200, body, "\"e1\""); };
                Assert.Equal("99.0.0", Updater.CheckNewer(false).Version.ToString());
                Assert.Equal("99.0.0", Updater.CheckNewer(false).Version.ToString());
                Assert.Equal(new[] { "-", "\"e1\"" }, calls);
                Assert.NotNull(Updater.LastCheck);
            }
            finally { Updater.Http = old; }
        }

        /// <summary>Скачивание: подписанный релиз сохраняется; подменённый MSI — отказ, файлы удалены, ничего не сохранено.</summary>
        [Fact]
        public void Download_RejectsTampered()
        {
            var (pub, priv) = Keys();
            var old = Updater.Http;
            ReleaseVerifier.KeyForTests = pub;
            try
            {
                var msi = Encoding.ASCII.GetBytes("msi bytes 1");
                var sums = Encoding.UTF8.GetBytes(Hex(msi) + "  BilemAgent-88.0.0.msi\n");
                var sig = Sign(priv, sums);
                var served = msi;
                Updater.Http = (url, etag) => url.EndsWith(".sums") ? (200, sums, null) : url.EndsWith(".sig") ? (200, sig, null) : (200, served, null);
                var r = Updater.Parse("[" + Release("v88.0.0", false) + "]")[0];
                var path = Updater.Download(r);
                Assert.True(File.Exists(path));
                Assert.Contains(Updater.Stored(), s => s.Version.ToString() == "88.0.0");
                Assert.Null(Updater.Recheck(path));

                served = (byte[])msi.Clone(); served[0] ^= 1;
                var r2 = Updater.Parse("[" + Release("v88.0.1", false) + "]")[0];
                var sums2 = Encoding.UTF8.GetBytes(Hex(msi) + "  BilemAgent-88.0.1.msi\n");
                var sig2 = Sign(priv, sums2);
                Updater.Http = (url, etag) => url.EndsWith(".sums") ? (200, sums2, null) : url.EndsWith(".sig") ? (200, sig2, null) : (200, served, null);
                Assert.Throws<AgentException>(() => Updater.Download(r2));
                Assert.DoesNotContain(Updater.Stored(), s => s.Version.ToString() == "88.0.1");
                Assert.False(Directory.Exists(Path.Combine(Updater.Dir, "88.0.1")));
            }
            finally { Updater.Http = old; ReleaseVerifier.KeyForTests = null; }
        }
    }
}
