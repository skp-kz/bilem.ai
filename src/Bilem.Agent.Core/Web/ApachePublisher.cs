using System;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Bilem.Agent.Core.Web
{
    /// <summary>
    /// Свой Apache «BilemApache» (часть J4): программы — из MSI (Program Files\Bilem Agent\apache\x64|x86, по разрядности платформы 1С),
    /// свой httpd.conf, журналы и папки публикаций — C:\ProgramData\BilemApache. Служба под виртуальной учёткой NT SERVICE\BilemApache,
    /// автозапуск, слушает только 127.0.0.1 на свободном порту от 18080. Чужой Apache (XAMPP и т. п.) не трогаем.
    /// </summary>
    public sealed class ApachePublisher : IWebServerPublisher
    {
        public const string ServiceName = "BilemApache";
        public const string Account = "NT SERVICE\\" + ServiceName;
        public string Kind => WebServerKind.Apache;

        /// <summary>
        /// Данные своего Apache — рядом с папкой Bilem, а не внутри: C:\ProgramData\Bilem закрыта для всех, кроме администраторов и SYSTEM
        /// (там токен и пароли), а Apache при запуске просматривает каждую папку пути — под виртуальной учёткой падал с «Неверная функция».
        /// </summary>
        public static string DataDir => Path.Combine(Path.GetDirectoryName(Paths.Home.TrimEnd('\\')) ?? Paths.Home, "BilemApache");
        public static string ConfFile => Path.Combine(DataDir, "conf", "httpd.conf");
        public static string LogsDir => Path.Combine(DataDir, "logs");
        public static string PubDir(string publication) => Path.Combine(DataDir, "pub", publication);
        public static string HomeFor(int bits) => Path.Combine(Paths.AppDir, "apache", bits == 64 ? "x64" : "x86");
        private static string Httpd(int bits) => Path.Combine(HomeFor(bits), "bin", "httpd.exe");
        private static string Fwd(string p) => p.Replace('\\', '/');

        /// <summary>
        /// Порт и разрядность своего Apache берём из его httpd.conf, а не из config.json: конфиг агента сохраняют и настройка базы,
        /// и окно настроек со своей копией — порт в нём затирался нулём, и следующая база получала новый порт, на котором никто не слушает.
        /// </summary>
        public static int Port => ParsePort(ReadConf());
        public static int Bits => ParseBits(ReadConf());
        private static string ReadConf() { try { return File.Exists(ConfFile) ? File.ReadAllText(ConfFile) : ""; } catch { return ""; } }
        internal static int ParsePort(string conf)
        {
            var m = Regex.Match(conf ?? "", @"^\s*Listen\s+127\.0\.0\.1:(\d+)", RegexOptions.Multiline);
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }
        internal static int ParseBits(string conf)
        {
            var m = Regex.Match(conf ?? "", @"^\s*ServerRoot\s+""[^""]*/apache/(x64|x86)""", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return !m.Success ? 0 : m.Groups[1].Value.Equals("x64", StringComparison.OrdinalIgnoreCase) ? 64 : 32;
        }

        public static bool ServiceExists()
        {
            try { using (var sc = new ServiceController(ServiceName)) { var _ = sc.Status; return true; } } catch { return false; }
        }

        public void EnsureInstalled(Platform1C platform, bool enableIfMissing)
        {
            var bits = platform.Bits;
            if (Bits != 0 && Bits != bits && ServiceExists())
                throw new AgentException(Txt.F("ApacheBitsMismatch", Bits, bits), Txt.S("HintApacheBits"));
            if (!File.Exists(Httpd(bits))) throw new AgentException(Txt.F("ApacheMissing", HomeFor(bits)), Txt.S("HintReinstall"));
            if (!File.Exists(Path.Combine(platform.Bin, "wsap24.dll"))) throw new AgentException(Txt.F("ApacheNoModule", platform), Txt.S("HintWebinst"));
            EnsureVcRedist(bits);

            // Своя папка: Администраторы и SYSTEM — полный доступ; учётке службы ниже — чтение (и запись журналов)
            if (!Directory.Exists(DataDir)) { Directory.CreateDirectory(DataDir); Secrets.RestrictDirToAdmins(DataDir); }
            Directory.CreateDirectory(Path.GetDirectoryName(ConfFile));
            Directory.CreateDirectory(LogsDir);
            Directory.CreateDirectory(Path.Combine(DataDir, "pub"));
            if (Port == 0) File.WriteAllText(ConfFile, BaseConf(bits, WebChoice.FreePort()), new UTF8Encoding(false));
            var port = Port;

            if (!ServiceExists())
            {
                var r = Proc.Run(Httpd(bits), $"-k install -n \"{ServiceName}\" -f \"{ConfFile}\"");
                if (!ServiceExists()) throw new AgentException(Txt.F("ApacheServiceFailed", r.ExitCode, r.Combined), Txt.S("HintSendLog"));
                // Виртуальная учётка службы: ничего лишнего, только своё (J4); автозапуск
                // -E: ошибки до открытия error.log (не тот ServerRoot, нет прав) — в startup.log, иначе служба молча «Неверная функция»
                var bin = $"\"{Httpd(bits)}\" -k runservice -E \"{Path.Combine(LogsDir, "startup.log")}\"";
                Proc.Run(Path.Combine(Paths.NativeSystem32, "sc.exe"), $"config {ServiceName} obj= \"{Account}\" start= auto DisplayName= \"Bilem Apache\" binPath= \"{bin.Replace("\"", "\\\"")}\"");
                // Без SID службы права, выданные «NT SERVICE\BilemApache», не действуют
                Proc.Run(Path.Combine(Paths.NativeSystem32, "sc.exe"), $"sidtype {ServiceName} unrestricted");
                Proc.Run(Path.Combine(Paths.NativeSystem32, "sc.exe"), $"description {ServiceName} \"Bilem Agent: свой Apache для публикации баз 1С, когда модуль 1С не работает в IIS. Слушает только 127.0.0.1.\"");
                Proc.Run(Path.Combine(Paths.NativeSystem32, "sc.exe"), $"failure {ServiceName} reset= 86400 actions= restart/60000/restart/60000/restart/60000");
                Log.Ok(Txt.F("ApacheServiceInstalled", ServiceName, port, bits));
            }
            // Права учётки службы: чтение конфига и публикаций, запись журналов (папка Bilem закрыта для остальных)
            Grant(DataDir, "(OI)(CI)RX");
            Grant(LogsDir, "(OI)(CI)M");
        }

        private static void Grant(string path, string rights) =>
            Proc.Run(Path.Combine(Paths.NativeSystem32, "icacls.exe"), $"{Proc.Quote(path)} /grant \"{Account}:{rights}\" /Q");

        /// <summary>Минимальный конфиг: только 127.0.0.1, нужные модули, свои журналы. Публикации дописывает webinst.</summary>
        internal static string BaseConf(int bits, int port)
        {
            var root = Fwd(HomeFor(bits));
            var sb = new StringBuilder();
            sb.AppendLine("# Bilem Agent — свой Apache для публикации баз 1С (часть J). Файл пишет агент; правки вручную перезапишутся.");
            // Путь — буквально: «ServerRoot "${…}"» через Define под виртуальной учёткой службы не находит каталог
            sb.AppendLine($"ServerRoot \"{root}\"");
            sb.AppendLine($"Listen 127.0.0.1:{port}");
            sb.AppendLine($"ServerName 127.0.0.1:{port}");
            foreach (var m in new[] { "authz_core", "authz_host", "alias", "mime", "log_config", "dir" })
                sb.AppendLine($"LoadModule {m}_module modules/mod_{m}.so");
            sb.AppendLine("TypesConfig conf/mime.types");
            sb.AppendLine($"ErrorLog \"{Fwd(Path.Combine(LogsDir, "error.log"))}\"");
            sb.AppendLine("LogLevel warn");
            sb.AppendLine($"PidFile \"{Fwd(Path.Combine(LogsDir, "httpd.pid"))}\"");
            sb.AppendLine("ThreadsPerChild 64");
            sb.AppendLine("<Directory />");
            sb.AppendLine("    AllowOverride None");
            sb.AppendLine("    Require all denied");
            sb.AppendLine("</Directory>");
            return sb.ToString();
        }

        public void Publish(PublishSpec s)
        {
            var dir = PubDir(s.Publication);
            var vrd = Path.Combine(dir, "default.vrd");
            var backup = File.ReadAllText(ConfFile);
            var webinst = s.Platform.Webinst;
            var w = Proc.Run(webinst, $"-publish -apache24 -wsdir {s.Publication} -dir \"{dir}\" -connstr \"File=\"\"{s.BasePath}\"\";\" -confpath \"{ConfFile}\"");
            Log.Info(Txt.F("WebinstResult", w.ExitCode, w.Combined));
            if (!File.Exists(vrd)) throw new AgentException(Txt.S("WebinstNoVrd"), Txt.S("HintWebinst"));
            Iis.EnableOData(vrd);
            Log.Ok(Txt.S("VrdOData"));
            var fixedConf = Harden(File.ReadAllText(ConfFile), s.Platform);
            File.WriteAllText(ConfFile, fixedConf, new UTF8Encoding(false));
            // Права службы: чтение публикации, изменение папки базы (как у пула IIS)
            Grant(dir, "(OI)(CI)RX");
            Proc.Run(Path.Combine(Paths.NativeSystem32, "icacls.exe"), $"{Proc.Quote(s.BasePath)} /grant \"{Account}:(OI)(CI)M\" /T /Q");
            Log.Ok(Txt.F("ApacheFolderRights", s.BasePath, Account));
            Platform.GrantRead(s.Platform, Account);
            ApplyOrRollback(Bits == 0 ? s.Platform.Bits : Bits, backup);
            Log.Ok(Txt.F("ApachePublished", s.Publication, Port));
        }

        /// <summary>
        /// После webinst: публикации — только 127.0.0.1 / ::1 («Require all granted» → «Require ip …»), модуль 1С — одна строка LoadModule
        /// (Apache грузит модуль 1С одной версии; другая платформа у другой базы — понятная ошибка, а не тихая подмена).
        /// </summary>
        internal static string Harden(string conf, Platform1C platform)
        {
            conf = Regex.Replace(conf, @"(?im)^(\s*)Require\s+all\s+granted\s*$", "$1Require ip 127.0.0.1 ::1");
            var mods = Regex.Matches(conf, @"(?im)^\s*LoadModule\s+_1cws_module\s+""?([^""\r\n]+)""?\s*$").Cast<Match>().ToList();
            var paths = mods.Select(m => m.Groups[1].Value.Trim().Replace('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (paths.Count > 1 && platform != null)
            {
                var want = Fwd(Path.Combine(platform.Bin, "wsap24.dll"));
                if (paths.Any(p => !string.Equals(p, want, StringComparison.OrdinalIgnoreCase)))
                    throw new AgentException(Txt.F("ApacheModuleConflict", string.Join(", ", paths)), Txt.S("HintApacheBits"));
            }
            for (var i = mods.Count - 1; i > 0; i--) conf = conf.Remove(mods[i].Index, mods[i].Length);   // без дублей
            return conf;
        }

        /// <summary>httpd -t; ошибка — вернуть прежний конфиг и строку в журнал (J6); иначе перезапуск службы.</summary>
        private static void ApplyOrRollback(int bits, string backup)
        {
            var t = WebSim.On("httpd_t_fail") ? new ProcResult { ExitCode = 1, Err = "BILEM_SIMULATE=httpd_t_fail" } : Proc.Run(Httpd(bits), $"-t -f \"{ConfFile}\"");
            if (t.ExitCode != 0)
            {
                File.WriteAllText(ConfFile, backup, new UTF8Encoding(false));
                throw new AgentException(Txt.F("ApacheConfBad", t.Combined), Txt.S("HintSendLog"));
            }
            Restart();
        }

        public static void Restart()
        {
            try
            {
                using (var sc = new ServiceController(ServiceName))
                {
                    if (sc.Status != ServiceControllerStatus.Stopped)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                    }
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                }
            }
            catch (Exception e) when (e is InvalidOperationException || e is System.ServiceProcess.TimeoutException)
            {
                // Причина — в журнале Apache (ошибка конфига, модуль 1С) или «Отказано в доступе» у учётки службы
                string tail = null;
                foreach (var log in new[] { "error.log", "startup.log" })
                    try { var l = File.ReadAllLines(Path.Combine(LogsDir, log)); if (l.Length > 0) { tail = string.Join(" | ", l.Reverse().Take(3).Reverse()); break; } } catch { }
                throw new AgentException(Txt.F("ApacheStartFailed", (e.InnerException ?? e).Message, tail ?? "—"), Txt.S("HintSendLog"));
            }
        }

        /// <summary>Убрать публикацию из нашего конфига; последняя — служба, конфиг и папка тоже (J4).</summary>
        public void Unpublish(BaseConfig b)
        {
            if (!File.Exists(ConfFile)) return;
            var dir = Fwd(PubDir(b.Publication)).TrimEnd('/');
            var conf = File.ReadAllText(ConfFile);
            conf = Regex.Replace(conf, @"(?im)^\s*Alias\s+""?/" + Regex.Escape(b.Publication) + @"""?\s+.*\r?\n", "");
            conf = Regex.Replace(conf, @"(?is)<Directory\s+""?" + Regex.Escape(dir) + @"/?""?>.*?</Directory>\s*", "");
            File.WriteAllText(ConfFile, conf, new UTF8Encoding(false));
            try { Directory.Delete(PubDir(b.Publication), true); } catch { }
            Proc.Run(Path.Combine(Paths.NativeSystem32, "icacls.exe"), $"{Proc.Quote(b.Path)} /remove \"{Account}\" /T /Q");
            Log.Ok(Txt.F("FolderRightsRevoked", b.Path, Account));
            var others = AgentConfig.Load().Bases.Any(x => x.Slug != b.Slug && WebServerKind.Of(x) == WebServerKind.Apache);
            if (others) { Restart(); return; }
            RemoveAll();
        }

        /// <summary>Нет баз на Apache или удаление агента: служба BilemApache, конфиг и журналы.</summary>
        public static void RemoveAll()
        {
            if (ServiceExists())
            {
                try { using (var sc = new ServiceController(ServiceName)) if (sc.Status != ServiceControllerStatus.Stopped) { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)); } } catch { }
                Proc.Run(Path.Combine(Paths.NativeSystem32, "sc.exe"), $"delete {ServiceName}");
                Log.Ok(Txt.F("ApacheServiceRemoved", ServiceName));
            }
            try { if (Directory.Exists(DataDir)) Directory.Delete(DataDir, true); } catch (Exception e) { Log.Warn(e.Message); }
        }

        public string BaseUrl(PublishSpec s) => $"http://127.0.0.1:{Port}/{s.Publication}/odata/standard.odata";

        // ---------- Visual C++ 2015–2022 ----------

        public static bool VcRedistInstalled(int bits)
        {
            if (WebSim.On("no_vcredist")) return false;
            var key = bits == 64 ? @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64" : @"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\X86";
            if (bits != 64 && !Environment.Is64BitOperatingSystem) key = @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X86";
            try
            {
                using (var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(key))
                    return k != null && Convert.ToInt32(k.GetValue("Installed") ?? 0) == 1;
            }
            catch { return false; }
        }

        /// <summary>Нет VC++ — тихая установка из MSI (папка redist), без интернета (J6, J8).</summary>
        private static void EnsureVcRedist(int bits)
        {
            if (VcRedistInstalled(bits)) return;
            var exe = Path.Combine(Paths.AppDir, "redist", bits == 64 ? "vc_redist.x64.exe" : "vc_redist.x86.exe");
            if (!File.Exists(exe)) throw new AgentException(Txt.F("ApacheNoVcRedist", exe), Txt.S("HintReinstall"));
            Log.Info(Txt.S("ApacheVcInstalling"));
            var r = Proc.Run(exe, "/install /quiet /norestart", timeoutMs: 600000);
            // 0 — поставлено, 3010 — нужна перезагрузка, 1638 — уже есть новее
            if (r.ExitCode != 0 && r.ExitCode != 3010 && r.ExitCode != 1638) throw new AgentException(Txt.F("ApacheVcFailed", r.ExitCode), Txt.S("HintSendLog"));
            Log.Ok(Txt.S("ApacheVcInstalled"));
        }
    }
}
