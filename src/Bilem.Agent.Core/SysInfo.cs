using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Bilem.Agent.Core
{
    /// <summary>
    /// «Собрать системную информацию» — краткая сводка для техподдержки (в буфер обмена, в файл, в отчёт для поддержки).
    /// Без паролей, токенов, путей (в том числе к профилям пользователей) и данных из 1С.
    /// </summary>
    public static class SysInfo
    {
        public static string Collect()
        {
            var sb = new StringBuilder();
            sb.AppendLine(Txt.F("SysInfoHeader", Mcp.McpHost.Version, DateTime.Now));
            sb.AppendLine();
            sb.AppendLine(Txt.F("SysInfoWindows", Environment.OSVersion.Version, Environment.Is64BitOperatingSystem ? 64 : 32));
            var rel = Diagnostics.NetRelease();
            sb.AppendLine(Txt.F("SysInfoNet", rel > 0 ? Diagnostics.NetName(rel) : Txt.S("DiagNetMissing"), rel));
            var survey = Platform.Survey();
            var found = survey.Where(s => s.Usable).Select(s => s.ToPlatform()).ToList();
            if (found.Count == 0) sb.AppendLine(Txt.F("SysInfoPlatform", Platform.Explain(survey)));
            // Все установки 1С, годные и нет (часть K): версия, разрядность, полная/учебная, COM, веб-модули, путь без имени пользователя
            foreach (var s in survey) sb.AppendLine(Txt.F("SysInfoPlatformSurvey", s.Line()));
            foreach (var p in found.OrderByDescending(x => x.Version).ThenByDescending(x => x.Bits))
            {
                string com;
                try { com = Platform.ComProblem(p); } catch (Exception e) { com = e.Message; }
                sb.AppendLine(Txt.F("SysInfoPlatformItem", p, com.Length == 0 ? Txt.S("SysInfoOk") : com,
                    System.IO.File.Exists(System.IO.Path.Combine(p.Bin, "wsisapi.dll")) ? Txt.S("Yes") : Txt.S("No")));
            }
            var isapi = System.IO.File.Exists(System.IO.Path.Combine(Paths.NativeSystem32, "inetsrv", "isapi.dll"));
            sb.AppendLine(Txt.F("SysInfoIis", Iis.Installed ? Txt.S("Yes") : Txt.S("No"), isapi ? Txt.S("Yes") : Txt.S("No"), Iis.IpSecurityInstalled ? Txt.S("Yes") : Txt.S("No")));

            AgentConfig cfg;
            try { cfg = AgentConfig.Load(); }
            catch (Exception e) { sb.AppendLine(Txt.F("SysInfoConfigBroken", e.Message)); return sb.ToString(); }
            sb.AppendLine(Txt.F("SysInfoMcp", cfg.Port, PortListening(cfg.Port) ? Txt.S("DiagMcpListening") : Txt.S("DiagMcpNotListening"), cfg.MaxRows));
            sb.AppendLine();
            sb.AppendLine(Txt.F("SysInfoBases", cfg.Bases.Count));
            foreach (var b in cfg.Bases)
            {
                sb.AppendLine(Txt.F("SysInfoBase", b.Slug, Txt.S("SysInfoKindFile"), b.DisplayName, b.Configuration, b.Version,
                    b.HasBsp ? Txt.S("Yes") : Txt.S("No"), b.PlatformVersion, b.PlatformBits));
                sb.AppendLine(Txt.F("SysInfoBaseOData", b.ODataObjects.Count, string.Join(", ", b.Roles)));
                sb.AppendLine(Txt.F("SysInfoBaseCheck",
                    b.VerifiedAt.HasValue ? b.VerifiedAt.Value.ToString("dd.MM.yyyy HH:mm") : "—",
                    b.LastCheckOk == true ? Txt.S("SysInfoOk") : b.LastCheckOk == false ? Txt.S("SysInfoFailed") : "—",
                    b.WriteDeniedAt.HasValue ? b.WriteDeniedAt.Value.ToString("dd.MM.yyyy HH:mm") : "—"));
            }
            return sb.ToString();
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
    }
}
