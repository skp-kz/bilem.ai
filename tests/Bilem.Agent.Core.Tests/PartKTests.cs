using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Bilem.Agent.Core.Tests
{
    /// <summary>Часть K: какую 1С нашли и почему она не подходит — учебная, без COM, не 8.3, установка для пользователя.</summary>
    public class PartKTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "bilem-k-" + Guid.NewGuid().ToString("N"));

        public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

        private string Make(string rel, params string[] files)
        {
            var bin = Path.Combine(_root, rel, "bin");
            Directory.CreateDirectory(bin);
            foreach (var f in files) File.WriteAllText(Path.Combine(bin, f), "x");
            return bin;
        }

        private Platform.SearchRoot R(string rel, int bits, bool perUser = false, bool training = false) =>
            new Platform.SearchRoot { Path = Path.Combine(_root, rel), Bits = bits, PerUser = perUser, Training = training };

        private List<PlatformInstall> Survey(params Platform.SearchRoot[] roots) => Platform.Survey(roots, registry: false);

        [Fact]
        public void Full83WithCom_Usable()
        {
            Make(@"1cv8\8.3.25.1374", "1cv8.exe", "comcntr.dll", "wsisapi.dll");
            var s = Survey(R("1cv8", 64)).Single();
            Assert.True(s.Usable);
            Assert.Equal(64, s.Bits);
            Assert.True(s.Com && s.WebIsapi && !s.WebApache && !s.Training && !s.PerUser);
            Assert.Equal(new Version(8, 3, 25, 1374), s.ToPlatform().Version);
        }

        [Fact]
        public void Training_ByFolderAndByExe()
        {
            Make(@"1cv8t\8.3.25.1374", "1cv8t.exe", "comcntr.dll");     // папка учебной
            Make(@"other\8.3.24.1000", "1cv8t.exe");                      // в bin только 1cv8t.exe
            var s = Survey(R("1cv8t", 32, training: true), R("other", 32));
            Assert.Equal(2, s.Count);
            Assert.All(s, i => Assert.True(i.Training && !i.Usable));
            var msg = Platform.Explain(s);
            Assert.Contains("учебная версия", msg);
            Assert.Contains("8.3.25.1374", msg);
            Assert.Equal(Txt.S("HintPlatformFull83"), Platform.ExplainHint(s));
        }

        [Fact]
        public void Full83WithoutCom_Explained()
        {
            Make(@"1cv8\8.3.25.1374", "1cv8.exe");
            Make(@"1cv8t\8.3.27.1000", "1cv8t.exe");                     // учебная новее — но ближе полная без COM
            var s = Survey(R("1cv8", 64), R("1cv8t", 64, training: true));
            Assert.DoesNotContain(s, i => i.Usable);
            var msg = Platform.Explain(s);
            Assert.Contains("без COM-коннектора", msg);
            Assert.Contains("8.3.25.1374", msg);
            Assert.Equal(Txt.S("HintPlatformAddCom"), Platform.ExplainHint(s));
        }

        [Fact]
        public void Not83_Explained()
        {
            Make(@"1cv8\8.5.1.100", "1cv8.exe", "comcntr.dll");
            Make(@"1cv8\common", "1cv8.exe");                            // не версия — пропускаем
            var s = Survey(R("1cv8", 64));
            var i = s.Single();
            Assert.False(i.Is83);
            Assert.False(i.Usable);
            Assert.Contains("только с платформой 8.3", Platform.Explain(s));
        }

        [Fact]
        public void PerUser_UsableAndFound()
        {
            Make(@"Programs\1cv8\8.3.25.1374", "1cv8.exe", "comcntr.dll");
            var s = Survey(R(@"Programs\1cv8", 0, perUser: true)).Single();
            Assert.True(s.Usable && s.PerUser);
            Assert.True(s.ToPlatform().PerUser);
            Assert.Equal(32, s.Bits);                                     // exe не настоящий — разрядность по умолчанию
        }

        [Fact]
        public void Nothing_ListsWhereSearched()
        {
            var msg = Platform.Explain(new List<PlatformInstall>());
            Assert.Contains(Txt.S("NoPlatform"), msg);
            Assert.Contains("1cv8", msg);
            Assert.DoesNotContain(Environment.UserName + "\\", msg, StringComparison.OrdinalIgnoreCase);   // профили — без имени
        }

        [Fact]
        public void MaskProfile_HidesUserName()
        {
            Assert.Equal(@"%LOCALAPPDATA%\Programs\1cv8\8.3.25.1374", Platform.MaskProfile(@"C:\Users\someone\AppData\Local\Programs\1cv8\8.3.25.1374"));
            Assert.Equal(@"%USERPROFILE%\Downloads", Platform.MaskProfile(@"C:\Users\someone\Downloads"));
            Assert.Equal(@"C:\Program Files\1cv8", Platform.MaskProfile(@"C:\Program Files\1cv8"));
        }

        [Fact]
        public void PeBits_ReadsHeader()
        {
            var exe = Path.Combine(Environment.SystemDirectory, "notepad.exe");
            if (File.Exists(exe)) Assert.Equal(Environment.Is64BitProcess ? 64 : 32, Platform.PeBits(exe));
            Directory.CreateDirectory(_root);
            var junk = Path.Combine(_root, "junk.exe");
            File.WriteAllText(junk, "not a pe");
            Assert.Equal(0, Platform.PeBits(junk));
        }

        [Fact]
        public void OneCNames()
        {
            Assert.True(Platform.IsOneCName("1С:Предприятие 8 (8.3.25.1374)"));
            Assert.True(Platform.IsOneCName("1C:Enterprise 8 (x86-64) (8.3.25.1374)"));
            Assert.False(Platform.IsOneCName("Microsoft Visual C++ 2015-2022"));
        }
    }
}
