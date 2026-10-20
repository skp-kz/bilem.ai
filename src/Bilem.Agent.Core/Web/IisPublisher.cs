using System;
using System.IO;

namespace Bilem.Agent.Core.Web
{
    /// <summary>
    /// Публикация в IIS (как раньше): рядом с чужими сайтами — свой пул «Bilem_&lt;база&gt;» (профиль учётки, без .NET), своё приложение
    /// в Default Web Site, анонимный вход от имени пула, права пула на папку базы, доступ только с 127.0.0.1 / ::1.
    /// </summary>
    public sealed class IisPublisher : IWebServerPublisher
    {
        public string Kind => WebServerKind.Iis;

        public void EnsureInstalled(Platform1C platform, bool enableIfMissing)
        {
            if (!Iis.Installed)
            {
                if (!enableIfMissing) throw new AgentException(Txt.S("NoIis"), Txt.S("HintEnableIis"));
                var d = WebSim.On("dism_fail") ? new ProcResult { ExitCode = unchecked((int)0x800f081f), Out = "BILEM_SIMULATE=dism_fail" } : Iis.EnableFeatures(Iis.WebServerFeatures);
                if (d.ExitCode != 0 && d.ExitCode != 3010) throw new AgentException(Txt.F("DismFailed", d.ExitCode, d.Combined), Txt.S("HintIisManual")) { Short = "dism" };
                if (d.ExitCode == 3010) Log.Warn(Txt.S("RebootRequired"));
                if (!Iis.Installed) throw new AgentException(Txt.S("NoIis"), Txt.S("HintIisManual")) { Short = "dism" };
                Log.Ok(Txt.S("IisEnabled"));
            }
            else Log.Ok(Txt.S("IisInstalled"));
            if (!Iis.IpSecurityInstalled)
            {
                // Без «Ограничений по IP» публикация была бы открыта наружу — не публикуем
                if (!enableIfMissing) throw new AgentException(Txt.S("NoIpSecurity"), Txt.S("HintEnableIis"));
                var d = Iis.EnableFeatures("IIS-IPSecurity");
                if (d.ExitCode != 0 && d.ExitCode != 3010) throw new AgentException(Txt.F("DismFailed", d.ExitCode, d.Combined), Txt.S("HintIisManual")) { Short = "dism" };
                Log.Ok(Txt.S("IpSecurityEnabled"));
            }
        }

        public void Publish(PublishSpec s)
        {
            var dir = Path.Combine(Iis.WwwRoot, s.Publication);
            var vrd = Path.Combine(dir, "default.vrd");
            if (!Iis.AppExists(s.Publication) || !File.Exists(vrd))
            {
                var w = Iis.Publish(s.Platform, s.Publication, dir, s.BasePath);
                Log.Info(Txt.F("WebinstResult", w.ExitCode, w.Combined));
                if (!File.Exists(vrd)) throw new AgentException(Txt.S("WebinstNoVrd"), Txt.S("HintWebinst"));
            }
            else Log.Ok(Txt.F("PublicationExists", s.Publication));
            Iis.EnableOData(vrd); // пул сессий — всегда в нужном виде
            Log.Ok(Txt.S("VrdOData"));
            Iis.EnsurePool(s.Pool, s.Platform.Bits);
            if (!string.Equals(Iis.AppPoolOf(s.Publication), s.Pool, StringComparison.OrdinalIgnoreCase)) Iis.SetAppPool(s.Publication, s.Pool);
            Log.Ok(Txt.F("PoolReady", s.Pool, s.Platform.Bits));
            if (!Iis.AnonymousIsPool(s.Publication)) Iis.AnonymousAsPool(s.Publication);
            Log.Ok(Txt.F("AnonymousAsPool", s.Pool));
            Iis.GrantPool(s.BasePath, s.Pool);
            Log.Ok(Txt.F("FolderRights", s.BasePath, s.Pool));
            Platform.GrantRead(s.Platform, "IIS_IUSRS");   // 1С «для пользователя»: пулы читают модуль 1С из профиля
            if (!Iis.LocalhostOnly(s.Publication)) Iis.RestrictToLocalhost(s.Publication);
            if (!Iis.LocalhostOnly(s.Publication)) throw new AgentException(Txt.S("LocalhostOnlyFailed"), Txt.S("HintIisManual"));
            Log.Ok(Txt.S("LocalhostOnly"));
            Iis.Recycle(s.Pool);
        }

        /// <summary>Убрать свою публикацию: приложение, пул, каталог в wwwroot, права пула на папку базы. Чужое не трогаем.</summary>
        public void Unpublish(BaseConfig b)
        {
            if (!Iis.Installed) return;
            Iis.DeleteApp(b.Publication);
            Iis.DeletePool(b.Pool);
            var dir = Path.Combine(Iis.WwwRoot, b.Publication);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Iis.RevokePool(b.Path, b.Pool);
            Log.Ok(Txt.F("FolderRightsRevoked", b.Path, b.Pool));
        }

        public string BaseUrl(PublishSpec s) => $"http://127.0.0.1/{s.Publication}/odata/standard.odata";
    }
}
