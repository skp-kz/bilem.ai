using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Bilem.Agent.Core.Mcp
{
    /// <summary>MCP-сервер агента: базы из конфига, токен из конфига (DPAPI), HTTP на 127.0.0.1:порт.</summary>
    public sealed class McpHost : IDisposable
    {
        private McpHttpServer _http;
        private List<BaseRuntime> _bases = new List<BaseRuntime>();
        private string _token;

        public bool Running => _http?.Running == true;
        public int Port { get; private set; }
        public int BaseCount => _bases.Count;

        /// <summary>Версия агента SemVer («0.3.0-beta.1») — та же, что в теге релиза и в MSI.</summary>
        private string _basesLogged;

        public static string Version => Updates.SemVer.Current.ToString();

        /// <summary>Локальный MCP: HTTP на 127.0.0.1:порт (бесплатный режим).</summary>
        public void Start() => Start(true);

        /// <summary>
        /// withHttp = false — только набор инструментов, без порта: режим Bilem Cloud (часть L), вызовы приходят по каналу облака
        /// (Protocol.Handle под McpCaller с CloudBase). Локальный порт при этом не слушается.
        /// </summary>
        public void Start(bool withHttp)
        {
            var cfg = AgentConfig.Load();
            Port = cfg.Port;
            _token = cfg.GetOrCreateToken();
            Reload(cfg);
            var proto = new McpProtocol(Tools.Create(Current, cfg.MaxRows), Version);
            // Почта — в списке инструментов, только когда SMTP-клиент включён (тумблер на вкладке «SMTP» / в трее)
            proto.Visible = t => t.Name != "send_document" || _smtpOn;
            _proto = proto;
            if (withHttp)
            {
                _http = new McpHttpServer(cfg.Port, () => _token, proto);
                _http.Start();
            }
            _core = true;
            _signature = Signature();
            // config.json изменился (окно, командная строка) — перечитать; сменился набор инструментов — уведомить клиентов
            _watch = new System.Threading.Timer(_ => Watch(), null, 2000, 2000);
        }

        private DateTime _cfgStamp;

        /// <summary>Базы для вызова инструмента: config.json изменился (окно, командная строка) — перечитываем, «Доступ» действует сразу.</summary>
        private IReadOnlyList<BaseRuntime> Current()
        {
            try { if (System.IO.File.GetLastWriteTimeUtc(Paths.ConfigFile) != _cfgStamp) Reload(); }
            catch (Exception e) { Log.Warn(e.Message); }
            // Вызов из облака — только его база, что бы ни было в аргументах (правило протокола: проверяет агент)
            var only = McpCaller.Current?.CloudBase;
            if (only != null) return _bases.Where(b => string.Equals(b.Config.Slug, only, StringComparison.OrdinalIgnoreCase)).ToList();
            return _bases;
        }

        /// <summary>Набор инструментов (и для канала облака).</summary>
        public McpProtocol Protocol => _proto;
        /// <summary>Инструменты подняты (с портом или без) — для режима Bilem Cloud.</summary>
        public bool CoreRunning => _core;
        /// <summary>Сменился набор инструментов или «Доступ» — канал облака пересылает список баз.</summary>
        public event Action ListChanged;
        private volatile bool _core;

        /// <summary>Перечитать базы и токен (после добавления/удаления базы или смены токена).</summary>
        public void Reload(AgentConfig cfg = null)
        {
            try { _cfgStamp = System.IO.File.GetLastWriteTimeUtc(Paths.ConfigFile); } catch { }
            cfg = cfg ?? AgentConfig.Load();
            var list = new List<BaseRuntime>();
            foreach (var b in cfg.Bases)
            {
                try { list.Add(BaseRuntime.FromConfig(b)); }
                catch (Exception e) { Log.Warn(Txt.F("McpBaseSkipped", b.Slug, e.Message)); }
            }
            _bases = list;
            _smtpOn = cfg.Smtp?.Enabled == true;
            _accessStamp = string.Join(";", cfg.Bases.Select(b => b.Slug + ":" + string.Join(",", b.ReadEffective.OrderBy(x => x)) + "/" + string.Join(",", (b.Write ?? new List<string>()).OrderBy(x => x))));
            if (!string.IsNullOrEmpty(cfg.TokenProtected)) _token = cfg.GetOrCreateToken();
            // Тот же набор баз — без повтора строки (повторы запуска MCP при занятом порте писали её каждые 2 с)
            var basesLine = Txt.F("McpBases", _bases.Count, string.Join(", ", _bases.Select(x => x.Config.Slug)));
            if (basesLine != _basesLogged) { Log.Info(basesLine); _basesLogged = basesLine; }
        }

        private McpProtocol _proto;
        private System.Threading.Timer _watch;
        private string _signature;
        private volatile bool _smtpOn;
        private string _accessStamp = "";

        /// <summary>Отпечаток: видимые инструменты + «Доступ» (чтение/запись по базам) — сменился → notifications/tools/list_changed.</summary>
        private string Signature() => (_proto?.ToolsSignature() ?? "") + "|" + _accessStamp;

        private void Watch()
        {
            try
            {
                // Конфиг мог перечитать и вызов инструмента (Current) — поэтому отпечаток сравниваем всегда
                if (System.IO.File.GetLastWriteTimeUtc(Paths.ConfigFile) != _cfgStamp) Reload();
                var sig = Signature();
                if (sig == _signature) return;
                _signature = sig;
                _http?.Notify(McpProtocol.ListChangedNotification);
                if (_http != null) Log.Info(Txt.F("McpListChanged", _http.StreamCount));
                ListChanged?.Invoke();
            }
            catch (Exception e) { Log.Warn(e.Message); }
        }

        public void Stop() { _watch?.Dispose(); _watch = null; _http?.Stop(); _http = null; _core = false; }
        public void Dispose() => Stop();
    }
}
