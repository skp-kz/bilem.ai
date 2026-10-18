using System;
using System.IO;
using System.Xml;

namespace Bilem.Agent.Core
{
    /// <summary>
    /// IIS: компоненты Windows, пулы, приложения, ограничения по IP, права на папку базы, публикация webinst.
    /// Все системные утилиты — 64-битные (через Sysnative, если процесс 32-битный: known-issues 6).
    /// </summary>
    public static class Iis
    {
        public const string Site = "Default Web Site";
        public static string Appcmd => Path.Combine(Paths.NativeSystem32, "inetsrv", "appcmd.exe");
        public static bool Installed => File.Exists(Appcmd);
        public static bool IpSecurityInstalled => File.Exists(Path.Combine(Paths.NativeSystem32, "inetsrv", "iprestr.dll"));
        public static string WwwRoot => Path.Combine(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)), "inetpub", "wwwroot");

        public static ProcResult Cmd(string args) => Proc.Run(Appcmd, args);

        /// <summary>Компоненты Windows через 64-битный dism (Install-WindowsFeature нет в 32-битном PowerShell; имена IIS-* общие для сервера и клиента).</summary>
        public static ProcResult EnableFeatures(params string[] names)
        {
            var args = "/online /enable-feature /all /norestart /quiet";
            foreach (var n in names) args += " /featurename:" + n;
            return Proc.Run(Path.Combine(Paths.NativeSystem32, "dism.exe"), args, timeoutMs: 1800000);
        }

        public static readonly string[] WebServerFeatures =
            { "IIS-WebServerRole", "IIS-WebServer", "IIS-ISAPIExtensions", "IIS-ISAPIFilter", "IIS-IPSecurity", "IIS-ManagementConsole" };

        // Только точное совпадение: «appcmd list app "Default Web Site/x"» для несуществующего x возвращает
        // корневое приложение сайта с кодом 0 (найдено на стенде) — поэтому ищем строку APP "Default Web Site/x".
        public static bool AppExists(string app) => AppLine(app) != null;

        private static string AppLine(string app)
        {
            var want = $"APP \"{Site}/{app}\"";
            foreach (var line in Cmd("list app").Out.Split('\n'))
                if (line.Trim().StartsWith(want + " ", StringComparison.OrdinalIgnoreCase)) return line.Trim();
            return null;
        }

        public static bool PoolExists(string pool)
        {
            var want = $"APPPOOL \"{pool}\"";
            foreach (var line in Cmd("list apppool").Out.Split('\n'))
                if (line.Trim().StartsWith(want + " ", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static string AppPoolOf(string app)
        {
            var line = AppLine(app);
            var m = line == null ? null : System.Text.RegularExpressions.Regex.Match(line, @"applicationPool:([^)]*)\)");
            return m != null && m.Success ? m.Groups[1].Value : null;
        }

        public static void EnsurePool(string pool, int bits)
        {
            if (!PoolExists(pool)) Check(Cmd($"add apppool /name:\"{pool}\""), "add apppool");
            Check(Cmd($"set apppool \"{pool}\" /enable32BitAppOnWin64:{(bits == 32 ? "true" : "false")}"), "set apppool");
        }

        public static void SetAppPool(string app, string pool) => Check(Cmd($"set app \"{Site}/{app}\" /applicationPool:\"{pool}\""), "set app");

        /// <summary>Анонимный вход от имени удостоверения пула (userName пустой), а не IUSR: файлы базы открывает только пул (known-issues 7).</summary>
        public static void AnonymousAsPool(string app) =>
            Check(Cmd($"set config \"{Site}/{app}\" -section:system.webServer/security/authentication/anonymousAuthentication /enabled:true /userName: /password: /commit:apphost"), "anonymousAuthentication");

        public static bool AnonymousIsPool(string app)
        {
            var r = Cmd($"list config \"{Site}/{app}\" -section:system.webServer/security/authentication/anonymousAuthentication");
            return r.Out.Contains("enabled=\"true\"") && r.Out.Contains("userName=\"\"");
        }

        /// <summary>Доступ только с 127.0.0.1 и ::1 (компонент «Ограничения по IP»).</summary>
        public static void RestrictToLocalhost(string app)
        {
            Cmd("unlock config -section:system.webServer/security/ipSecurity");
            Check(Cmd($"set config \"{Site}/{app}\" -section:system.webServer/security/ipSecurity /allowUnlisted:false /commit:apphost"), "ipSecurity allowUnlisted");
            var cur = Cmd($"list config \"{Site}/{app}\" -section:system.webServer/security/ipSecurity").Out;
            foreach (var ip in new[] { "127.0.0.1", "::1" })
            {
                if (cur.Contains($"ipAddress=\"{ip}\"")) continue;
                Check(Cmd($"set config \"{Site}/{app}\" -section:system.webServer/security/ipSecurity \"/+[ipAddress='{ip}',allowed='true']\" /commit:apphost"), "ipSecurity " + ip);
            }
        }

        public static bool LocalhostOnly(string app)
        {
            var cur = Cmd($"list config \"{Site}/{app}\" -section:system.webServer/security/ipSecurity").Out;
            return cur.Contains("allowUnlisted=\"false\"") && cur.Contains("ipAddress=\"127.0.0.1\"");
        }

        public static void Recycle(string pool) => Cmd($"recycle apppool /apppool.name:\"{pool}\"");
        public static void DeleteApp(string app) { if (AppExists(app)) Check(Cmd($"delete app \"{Site}/{app}\""), "delete app"); }
        public static void DeletePool(string pool) { if (PoolExists(pool)) Check(Cmd($"delete apppool /apppool.name:\"{pool}\""), "delete apppool"); }

        public static string PoolIdentity(string pool) => "IIS APPPOOL\\" + pool;

        /// <summary>Права на изменение папки базы — только пулу этой базы.</summary>
        public static void GrantPool(string folder, string pool) =>
            Check(Proc.Run(Path.Combine(Paths.NativeSystem32, "icacls.exe"), $"{Proc.Quote(folder)} /grant \"{PoolIdentity(pool)}:(OI)(CI)M\" /T /Q"), "icacls /grant");

        public static void RevokePool(string folder, string pool)
        {
            if (Directory.Exists(folder)) Proc.Run(Path.Combine(Paths.NativeSystem32, "icacls.exe"), $"{Proc.Quote(folder)} /remove \"{PoolIdentity(pool)}\" /T /Q");
        }

        /// <summary>Публикация webinst: каталог в wwwroot, приложение в Default Web Site, default.vrd и web.config.</summary>
        public static ProcResult Publish(Platform1C p, string app, string dir, string basePath)
        {
            // Формат из документации 1С: -connstr "File=""C:\base"";"
            var args = $"-publish -iis -wsdir {app} -dir \"{dir}\" -connstr \"File=\"\"{basePath}\"\";\"";
            return Proc.Run(p.Webinst, args);
        }

        /// <summary>default.vrd: стандартный интерфейс OData и пул сессий.</summary>
        public static void EnableOData(string vrdPath)
        {
            var x = new XmlDocument();
            x.Load(vrdPath);
            var point = x.DocumentElement;
            var od = point.SelectSingleNode("*[local-name()='standardOdata']") as XmlElement;
            if (od == null) { od = x.CreateElement("standardOdata", point.NamespaceURI); point.AppendChild(od); }
            od.SetAttribute("enable", "true");
            od.SetAttribute("reuseSessions", "autouse");
            od.SetAttribute("sessionMaxAge", "20");
            od.SetAttribute("poolSize", "5");
            od.SetAttribute("poolTimeout", "5");
            x.Save(vrdPath);
        }

        public static bool ODataEnabled(string vrdPath)
        {
            if (!File.Exists(vrdPath)) return false;
            var x = new XmlDocument();
            x.Load(vrdPath);
            var od = x.DocumentElement.SelectSingleNode("*[local-name()='standardOdata']") as XmlElement;
            return od != null && od.GetAttribute("enable") == "true";
        }

        private static void Check(ProcResult r, string what)
        {
            if (r.ExitCode != 0) throw new AgentException(Txt.F("IisCommandFailed", what, r.ExitCode, r.Combined), Txt.S("HintIisManual"));
        }
    }
}
