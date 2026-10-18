using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Win32;

namespace Bilem.Agent.Core
{
    public enum CheckStatus { Ok, Warn, Fail, Info }

    public sealed class CheckResult
    {
        public string Group { get; set; }
        public string Name { get; set; }
        public CheckStatus Status { get; set; }
        public string Detail { get; set; }
        public string Hint { get; set; }
    }

    /// <summary>
    /// «Диагностика компонентов»: быстрый самотест всего стека — чек-лист зелёных и красных пунктов с подсказкой, что делать.
    /// Ничего не меняет. В бесплатной версии это и есть техподдержка: разработчик видит, чего не хватает.
    /// </summary>
    public static class Diagnostics
    {
        /// <summary>Минимальное значение Release в реестре для .NET Framework 4.8.</summary>
        public const int Net48Release = 528040;

        public static List<CheckResult> Run(bool mcpRunningHere = false)
        {
            var r = new List<CheckResult>();
            void Add(string g, string n, CheckStatus s, string d, string h = null) => r.Add(new CheckResult { Group = g, Name = n, Status = s, Detail = d, Hint = h });
            string Safe(Func<string> f) { try { return f(); } catch (Exception e) { return e.Message; } }

            // --- Система
            var gSys = Txt.S("DiagGroupSystem");
            Add(gSys, Txt.S("DiagAdmin"), Onboarding.IsAdmin() ? CheckStatus.Ok : CheckStatus.Fail,
                Onboarding.IsAdmin() ? Txt.S("Yes") : Txt.S("No"), Txt.S("HintRunAsAdmin"));
            Add(gSys, Txt.S("DiagWindows"), Environment.Is64BitOperatingSystem ? CheckStatus.Ok : CheckStatus.Warn,
                $"{Environment.OSVersion.Version}, {(Environment.Is64BitOperatingSystem ? 64 : 32)}-бит", Txt.S("HintWindows32"));
            var rel = NetRelease();
            Add(gSys, Txt.S("DiagNet"), rel >= Net48Release ? CheckStatus.Ok : CheckStatus.Fail,
                rel > 0 ? Txt.F("DiagNetRelease", NetName(rel), rel) : Txt.S("DiagNetMissing"), Txt.S("HintNet48"));

            // --- 1С
            var g1c = Txt.S("DiagGroup1C");
            var found = Platform.Find();
            if (found.Count == 0) Add(g1c, Txt.S("DiagPlatform"), CheckStatus.Fail, Txt.S("NoPlatform"), Txt.S("HintInstallPlatform"));
            foreach (var p in found.OrderByDescending(x => x.Version).ThenByDescending(x => x.Bits))
            {
                Add(g1c, Txt.F("DiagPlatformItem", p), CheckStatus.Ok, p.Bin);
                var com = Safe(() => Platform.ComProblem(p));
                Add(g1c, Txt.F("DiagCom", p), com.Length == 0 ? CheckStatus.Ok : CheckStatus.Warn, com.Length == 0 ? Txt.S("ComOk") : com, Txt.S("HintComAuto"));
                var web = File.Exists(Path.Combine(p.Bin, "wsisapi.dll")) && File.Exists(p.Webinst);
                Add(g1c, Txt.F("DiagWebModules", p), web ? CheckStatus.Ok : CheckStatus.Fail, web ? "wsisapi.dll, webinst.exe" : Txt.S("DiagWebModulesMissing"), Txt.S("HintWebinst"));
                var helper = Platform.ComHelper(p);
                Add(g1c, Txt.F("DiagHelper", Path.GetFileName(helper)), File.Exists(helper) ? CheckStatus.Ok : CheckStatus.Fail, File.Exists(helper) ? helper : Txt.S("DiagNotFound"), Txt.S("HintReinstall"));
            }

            // --- IIS
            var gIis = Txt.S("DiagGroupIis");
            Add(gIis, Txt.S("DiagIis"), Iis.Installed ? CheckStatus.Ok : CheckStatus.Warn, Iis.Installed ? Txt.S("IisInstalled") : Txt.S("NoIis"), Txt.S("HintEnableIis"));
            var isapi = File.Exists(Path.Combine(Paths.NativeSystem32, "inetsrv", "isapi.dll"));
            Add(gIis, Txt.S("DiagIsapi"), isapi ? CheckStatus.Ok : CheckStatus.Warn, isapi ? Txt.S("Yes") : Txt.S("No"), Txt.S("HintEnableIis"));
            Add(gIis, Txt.S("DiagIpSecurity"), Iis.IpSecurityInstalled ? CheckStatus.Ok : CheckStatus.Warn, Iis.IpSecurityInstalled ? Txt.S("Yes") : Txt.S("No"), Txt.S("HintEnableIis"));

            // --- Агент и MCP
            var gAgent = Txt.S("DiagGroupAgent");
            AgentConfig cfg = null;
            try { cfg = AgentConfig.Load(); Add(gAgent, Txt.S("DiagConfig"), CheckStatus.Ok, Paths.ConfigFile); }
            catch (Exception e) { Add(gAgent, Txt.S("DiagConfig"), CheckStatus.Fail, e.Message, Txt.S("HintConfigBroken")); }
            if (cfg != null)
            {
                string token = null;
                try { token = string.IsNullOrEmpty(cfg.TokenProtected) ? null : Secrets.Unprotect(cfg.TokenProtected); } catch { }
                Add(gAgent, Txt.S("DiagToken"), token != null ? CheckStatus.Ok : CheckStatus.Info, token != null ? Txt.S("DiagTokenOk") : Txt.S("DiagTokenNone"));
                var listening = PortListening(cfg.Port);
                Add(gAgent, Txt.F("DiagMcpPort", cfg.Port),
                    listening ? CheckStatus.Ok : (mcpRunningHere ? CheckStatus.Fail : CheckStatus.Info),
                    listening ? Txt.S("DiagMcpListening") : Txt.S("DiagMcpNotListening"), Txt.S("HintMcpStart"));

                // --- Базы
                if (cfg.Bases.Count == 0) Add(Txt.S("DiagGroupBases"), Txt.S("DiagBases"), CheckStatus.Info, Txt.S("NoBases"), Txt.S("HintSetupFirst"));
                foreach (var b in cfg.Bases) CheckBase(b, r);
            }

            // --- ИИ-клиенты (справочно)
            var gClients = Txt.S("DiagGroupClients");
            var claude = Connectors.FindOnPath("claude.cmd") ?? Connectors.FindOnPath("claude.exe");
            Add(gClients, "Claude Code", claude != null ? CheckStatus.Ok : CheckStatus.Info, claude ?? Txt.S("DiagNotFound"), Txt.S("ConnectClaudeManual"));
            var npx = Connectors.FindOnPath("npx.cmd");
            Add(gClients, Txt.S("DiagNode"), npx != null ? CheckStatus.Ok : CheckStatus.Info, npx ?? Txt.S("DiagNotFound"), Txt.S("ConnectNoNode"));
            return r;
        }

        private static void CheckBase(BaseConfig b, List<CheckResult> r)
        {
            var g = Txt.F("DiagGroupBase", b.Slug);
            void Add(string n, CheckStatus s, string d, string h = null) => r.Add(new CheckResult { Group = g, Name = n, Status = s, Detail = d, Hint = h });
            var hasFile = File.Exists(Path.Combine(b.Path ?? "", "1Cv8.1CD"));
            Add(Txt.S("DiagBaseFolder"), hasFile ? CheckStatus.Ok : CheckStatus.Fail, b.Path, Txt.S("HintBaseMoved"));
            string pwd = null;
            try { pwd = Secrets.ReadSecretFile(b.SecretFile); Add(Txt.S("DiagSecret"), CheckStatus.Ok, Txt.S("DiagSecretOk")); }
            catch (Exception e) { Add(Txt.S("DiagSecret"), CheckStatus.Fail, e.Message, Txt.S("HintResetPassword")); }
            if (Iis.Installed)
            {
                var app = Iis.AppExists(b.Publication);
                Add(Txt.S("DiagPublication"), app ? CheckStatus.Ok : CheckStatus.Fail, b.Publication, Txt.S("Hint404"));
                var pool = Iis.PoolExists(b.Pool);
                Add(Txt.S("DiagPool"), pool ? CheckStatus.Ok : CheckStatus.Fail, b.Pool, Txt.S("HintRerunSetup"));
                if (app)
                {
                    Add(Txt.S("DiagAnonymousPool"), Iis.AnonymousIsPool(b.Publication) ? CheckStatus.Ok : CheckStatus.Fail, "", Txt.S("HintRerunSetup"));
                    Add(Txt.S("DiagLocalhostOnly"), Iis.LocalhostOnly(b.Publication) ? CheckStatus.Ok : CheckStatus.Fail, "127.0.0.1 / ::1", Txt.S("HintRerunSetup"));
                }
            }
            if (pwd != null)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var m = new ODataClient(b.OData, b.User, pwd).Send("GET", b.OData + "/$metadata", timeoutMs: 30000);
                Add(Txt.S("DiagOData"), m.Code == 200 ? CheckStatus.Ok : CheckStatus.Fail,
                    Txt.F("DiagODataCode", m.Code, sw.ElapsedMilliseconds), Verifier.HintForCode(m.Code));
            }
            Add(Txt.S("DiagWriteDenied"), b.WriteDeniedAt.HasValue ? CheckStatus.Ok : CheckStatus.Warn,
                b.WriteDeniedAt.HasValue ? Txt.F("DiagWriteDeniedAt", b.WriteDeniedAt.Value) : Txt.S("UiWriteNotChecked"), Txt.S("HintVerify"));
        }

        public static int NetRelease()
        {
            try
            {
                using (var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                    return k?.GetValue("Release") is int v ? v : 0;
            }
            catch { return 0; }
        }

        /// <summary>Версия .NET Framework 4.x по значению Release (таблица Microsoft).</summary>
        public static string NetName(int release)
        {
            if (release >= 533320) return "4.8.1";
            if (release >= 528040) return "4.8";
            if (release >= 461808) return "4.7.2";
            if (release >= 461308) return "4.7.1";
            if (release >= 460798) return "4.7";
            if (release >= 394802) return "4.6.2";
            if (release >= 394254) return "4.6.1";
            if (release >= 393295) return "4.6";
            if (release >= 379893) return "4.5.2";
            if (release >= 378675) return "4.5.1";
            return "4.5";
        }

        private static bool PortListening(int port)
        {
            try
            {
                using (var c = new TcpClient())
                {
                    var ar = c.BeginConnect(IPAddress.Loopback, port, null, null);
                    return ar.AsyncWaitHandle.WaitOne(1000) && c.Connected;
                }
            }
            catch { return false; }
        }

        /// <summary>Текстовый отчёт — для «Скопировать» и отчёта для поддержки. Без паролей и токенов.</summary>
        public static string Report(IEnumerable<CheckResult> results)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Bilem Agent {Mcp.McpHost.Version} — {Txt.S("UiDiagTitle")} {DateTime.Now:dd.MM.yyyy HH:mm}");
            string group = null;
            foreach (var c in results)
            {
                if (c.Group != group) { sb.AppendLine(); sb.AppendLine("## " + c.Group); group = c.Group; }
                var mark = c.Status == CheckStatus.Ok ? "[OK]  " : c.Status == CheckStatus.Fail ? "[FAIL]" : c.Status == CheckStatus.Warn ? "[WARN]" : "[INFO]";
                sb.AppendLine($"{mark} {c.Name}: {Connectors.Mask(c.Detail)}");
                if (c.Status == CheckStatus.Fail || c.Status == CheckStatus.Warn) if (!string.IsNullOrEmpty(c.Hint)) sb.AppendLine("       → " + c.Hint);
            }
            var fails = results.Count(x => x.Status == CheckStatus.Fail);
            var warns = results.Count(x => x.Status == CheckStatus.Warn);
            sb.AppendLine();
            sb.AppendLine(Txt.F("DiagSummary", results.Count(x => x.Status == CheckStatus.Ok), warns, fails));
            return sb.ToString();
        }
    }
}
