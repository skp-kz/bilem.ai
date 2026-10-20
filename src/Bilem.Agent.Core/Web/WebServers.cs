using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.ServiceProcess;
using Microsoft.Win32;

namespace Bilem.Agent.Core.Web
{
    /// <summary>Веб-сервер публикации базы (часть J): IIS или свой Apache «BilemApache».</summary>
    public static class WebServerKind
    {
        public const string Iis = "iis";
        public const string Apache = "apache";
        public static string Of(BaseConfig b) => string.Equals(b?.WebServer, Apache, StringComparison.OrdinalIgnoreCase) ? Apache : Iis;
    }

    /// <summary>Что опубликовать: база, имя публикации, платформа.</summary>
    public sealed class PublishSpec
    {
        public string Slug;
        public string Publication;   // bilem_<slug>
        public string Pool;          // Bilem_<slug> (IIS)
        public string BasePath;
        public Platform1C Platform;
    }

    /// <summary>
    /// Публикация базы на веб-сервере. Verifier и инструменты работают только через адрес OData (BaseUrl) и не знают, что под ним.
    /// </summary>
    public interface IWebServerPublisher
    {
        string Kind { get; }
        /// <summary>Поставить / включить веб-сервер, если его нет. Ошибка — AgentException (с причиной из таблицы J6).</summary>
        void EnsureInstalled(Platform1C platform, bool enableIfMissing);
        void Publish(PublishSpec spec);
        void Unpublish(BaseConfig b);
        string BaseUrl(PublishSpec spec);
    }

    /// <summary>Имитация сбоев для тестов и проверки веток отката: BILEM_SIMULATE=iis_crash,dism_fail,port_busy,no_vcredist,httpd_t_fail.</summary>
    public static class WebSim
    {
        public static bool On(string what)
        {
            var v = Environment.GetEnvironmentVariable("BILEM_SIMULATE") ?? "";
            return v.Split(',', ';').Any(x => string.Equals(x.Trim(), what, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Разведка машины до публикации (J2): одна строка в журнал и решение по таблице (J3).</summary>
    public sealed class WebDetect
    {
        public bool IisInstalled;
        public bool W3svcRunning;
        public bool IpSecurity;
        /// <summary>Чужой Apache: службы Apache* (кроме нашей) / httpd.exe в известных местах — не трогаем.</summary>
        public List<string> ForeignApache = new List<string>();
        public bool OurApache;
        public bool PlatformHasIsapi, PlatformHasApache24;
        public Platform1C Platform;

        public static WebDetect Run(Platform1C platform)
        {
            var d = new WebDetect { Platform = platform };
            d.IisInstalled = Iis.Installed && !WebSim.On("no_iis");
            d.IpSecurity = Iis.IpSecurityInstalled;
            try { using (var sc = new ServiceController("W3SVC")) d.W3svcRunning = sc.Status == ServiceControllerStatus.Running; } catch { }
            try
            {
                foreach (var s in ServiceController.GetServices())
                    using (s)
                    {
                        if (string.Equals(s.ServiceName, ApachePublisher.ServiceName, StringComparison.OrdinalIgnoreCase)) d.OurApache = true;
                        else if (s.ServiceName.StartsWith("Apache", StringComparison.OrdinalIgnoreCase)) d.ForeignApache.Add(s.ServiceName);
                    }
            }
            catch { }
            foreach (var p in new[] { @"C:\xampp\apache\bin\httpd.exe", @"C:\Apache24\bin\httpd.exe" })
                if (File.Exists(p)) d.ForeignApache.Add(p);
            if (platform != null)
            {
                d.PlatformHasIsapi = File.Exists(Path.Combine(platform.Bin, "wsisapi.dll"));
                d.PlatformHasApache24 = File.Exists(Path.Combine(platform.Bin, "wsap24.dll"));
            }
            return d;
        }

        public string Line() => Txt.F("WebDetectLine",
            IisInstalled ? Txt.S(W3svcRunning ? "WebDetectIisOn" : "WebDetectIisStopped") : Txt.S("WebDetectIisOff"),
            OurApache ? Txt.S("WebDetectOurApache") : ForeignApache.Count > 0 ? Txt.F("WebDetectForeignApache", string.Join(", ", ForeignApache)) : Txt.S("WebDetectApacheOff"),
            Platform == null ? "—" : $"{Platform.Version} x{(Platform.Bits == 64 ? "64" : "86")}");
    }

    /// <summary>Выбор веб-сервера — без вопросов админу (J3). Ручной режим — «Сервис» → «Веб-сервер».</summary>
    public static class WebChoice
    {
        public const string ModeAuto = "auto";

        /// <summary>Режим «Авто»: IIS есть → IIS; есть только Apache (чужой) → свой Apache; ничего → включить IIS (не вышло — Apache).</summary>
        public static string Decide(WebDetect d, string mode)
        {
            if (string.Equals(mode, WebServerKind.Iis, StringComparison.OrdinalIgnoreCase)) return WebServerKind.Iis;
            if (string.Equals(mode, WebServerKind.Apache, StringComparison.OrdinalIgnoreCase)) return WebServerKind.Apache;
            if (d.IisInstalled) return WebServerKind.Iis;
            if (d.ForeignApache.Count > 0 || d.OurApache) return WebServerKind.Apache;
            return WebServerKind.Iis;   // включим IIS; не включился — свой Apache (Onboarding)
        }

        /// <summary>
        /// Переключаться ли на Apache после проверки IIS (J5) — только по признаку падения IIS: событие 1000 по w3wp.exe,
        /// обрыв / пустой ответ на $metadata, 503 (пул остановлен). Неверный пароль, права, лицензия 1С — не повод.
        /// Возвращает строку таблицы J6 (ключ текста) или null.
        /// </summary>
        public static string FallbackReason(string kind, string mode, int metadataCode, string crash)
        {
            if (kind != WebServerKind.Iis || !string.Equals(mode ?? ModeAuto, ModeAuto, StringComparison.OrdinalIgnoreCase)) return null;
            if (WebSim.On("iis_crash") || crash != null) return "J6IisCrash";
            if (metadataCode == 503) return "J6Iis503";
            if (metadataCode == 0) return "J6IisCrash";
            return null;
        }

        /// <summary>Свободный порт на 127.0.0.1, начиная с from (J6: «порт занят» — следующий).</summary>
        public static int FreePort(int from = 18080)
        {
            for (var p = from; p < from + 200; p++)
            {
                if (p == from && WebSim.On("port_busy")) continue;
                try
                {
                    var l = new TcpListener(IPAddress.Loopback, p);
                    l.Start(); l.Stop();
                    return p;
                }
                catch (SocketException) { }
            }
            throw new AgentException(Txt.F("ApacheNoPort", from, from + 199));
        }
    }
}
