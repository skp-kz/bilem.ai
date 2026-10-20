using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Bilem.Agent.Core.Mcp;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent.Core.Service
{
    /// <summary>
    /// То, что работает без окна: MCP на 127.0.0.1, фоновые проверки баз, проверка обновлений (только проверка — ставит человек),
    /// канал состояния для окна в трее. Живёт в службе BilemAgent (и в «bilem-agent serve» для отладки).
    /// </summary>
    public sealed class AgentServer : IDisposable
    {
        public const string ServiceName = "BilemAgent";

        private readonly McpHost _host = new McpHost();
        private readonly List<LogEntry> _tail = new List<LogEntry>();
        private readonly Dictionary<string, string> _baseErrors = new Dictionary<string, string>();
        private Timer _mcpRetry, _health, _updates, _mode;
        private Cloud.CloudChannel _cloud;
        /// <summary>Сейчас режим Bilem Cloud: локальный порт закрыт, вызовы — по каналу облака (часть L).</summary>
        private bool _cloudMode;
        private StatusPipe _pipe;
        private string _mcpError;
        private DateTime _started;
        private DateTime? _lastHealth;
        private string _updateAvailable;
        private int _retriesLeft;

        /// <summary>Сколько ждать порт MCP при старте (после обновления порт может ещё держать прежний процесс).</summary>
        public static readonly TimeSpan PortPatience = TimeSpan.FromSeconds(60);

        /// <summary>Канал состояния для окна (тесты на машине с работающей службой — без него, имя канала одно на машину).</summary>
        public bool WithPipe { get; set; } = true;

        public void Start()
        {
            _started = DateTime.Now;
            Paths.Ensure();
            Secrets.RestrictDirToAdmins(Paths.Home);
            Log.Written += Remember;
            Log.Info(Txt.F("SvcStarted", McpHost.Version));
            // Было обновление кнопкой — «Обновлено с X на Y» в журнал и аудит (окно в трее без прав этого не может)
            try { Updates.Updater.CompleteIfPending(); } catch (Exception e) { Log.Warn(e.Message); }
            if (WithPipe)
            {
                _pipe = new StatusPipe(Status) { RestartMcp = RestartMcp };
                _pipe.Start();
            }
            _retriesLeft = (int)(PortPatience.TotalSeconds / 2);
            _cloud = new Cloud.CloudChannel(_host);
            ApplyMode();
            _mcpRetry = new Timer(_ => TryStartMcp(), null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
            // Галочка Bilem Cloud на вкладке MCP — переключаем режим без перезапуска службы
            _mode = new Timer(_ => ApplyMode(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
            _health = new Timer(_ => CheckBases(), null, TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(5));
            _updates = new Timer(_ => CheckUpdates(), null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(1));
        }

        private readonly object _sync = new object();

        private void TryStartMcp()
        {
            lock (_sync)
            {
                if (_host.Running || _cloudMode) { _mcpRetry?.Change(Timeout.Infinite, Timeout.Infinite); return; }
                try
                {
                    _host.Start();
                    _mcpError = null;
                    _mcpRetry?.Change(Timeout.Infinite, Timeout.Infinite);
                }
                catch (Exception e)
                {
                    _mcpError = (e as AgentException)?.Short ?? e.Message;
                    // Первые попытки — молча; ошибка в журнале — только если порт так и не освободился
                    if (--_retriesLeft <= 0)
                    {
                        Log.Fail(e.Message);
                        _mcpRetry?.Change(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));   // дальше — раз в минуту, без шума
                        _retriesLeft = int.MaxValue;
                    }
                }
            }
        }

        /// <summary>Перезапуск MCP (команда из канала от администратора).</summary>
        /// <summary>
        /// Либо бесплатно, либо Bilem Cloud (часть L): включили облако — локальный MCP выключен (порт не слушается), служба держит
        /// канал; выключили — канал закрыт, локальный MCP снова на 127.0.0.1. Одновременно — никогда.
        /// </summary>
        internal void ApplyMode()
        {
            bool want;
            try { want = AgentConfig.Load().Cloud?.Enabled == true; } catch { return; }
            lock (_sync)
            {
                if (want == _cloudMode) return;
                if (want)
                {
                    _mcpRetry?.Change(Timeout.Infinite, Timeout.Infinite);
                    _host.Stop();
                    _cloudMode = true;
                    try { _host.Start(false); } catch (Exception e) { Log.Fail(e.Message); }
                    _cloud.Start();
                    Log.Ok(Txt.S("CloudModeOn"));
                }
                else
                {
                    _cloud.Stop();
                    _host.Stop();
                    _cloudMode = false;
                    Log.Ok(Txt.S("CloudModeOff"));
                    _retriesLeft = (int)(PortPatience.TotalSeconds / 2);
                    _mcpRetry?.Change(TimeSpan.Zero, TimeSpan.FromSeconds(2));
                }
            }
        }

        public bool CloudMode => _cloudMode;
        internal Cloud.CloudChannel Cloud => _cloud;
        internal Mcp.McpHost Host => _host;

        public void RestartMcp()
        {
            lock (_sync)
            {
                if (_cloudMode)
                {
                    // В режиме Bilem Cloud «Перезапустить MCP» — переподключить канал
                    _cloud.Stop();
                    _cloud.Start();
                    return;
                }
                if (_host.Running) _host.Stop();
                Log.Info(Txt.S("McpRestarting"));
                _retriesLeft = (int)(PortPatience.TotalSeconds / 2);
                _mcpRetry.Change(TimeSpan.Zero, TimeSpan.FromSeconds(2));
            }
        }

        private void CheckBases()
        {
            try
            {
                var cfg = AgentConfig.Load();
                foreach (var b in cfg.Bases)
                {
                    string err;
                    try { err = Health.QuickCheck(b); } catch (Exception e) { err = e.Message; }
                    lock (_baseErrors) _baseErrors[b.Slug] = err;
                }
                lock (_baseErrors)
                    foreach (var gone in _baseErrors.Keys.Where(k => cfg.Find(k) == null).ToList()) _baseErrors.Remove(gone);
                _lastHealth = DateTime.Now;
            }
            catch (Exception e) { Log.Warn(e.Message); }
        }

        private void CheckUpdates()
        {
            try
            {
                var last = Updates.Updater.LastCheck;
                if (last != null && DateTime.Now - last.Value < TimeSpan.FromDays(1)) return;
                var r = Updates.Updater.CheckNewer(AgentConfig.Load().UpdateBeta);
                _updateAvailable = r?.Version.ToString();
                if (r != null) Log.Info(Txt.F("UpdAvailableLog", r.Version));
            }
            catch (Exception e) { Log.Info(Txt.F("UpdCheckFailedQuiet", e.Message)); }
        }

        private void Remember(LogEntry e)
        {
            lock (_tail)
            {
                _tail.Add(e);
                if (_tail.Count > 300) _tail.RemoveRange(0, _tail.Count - 300);
            }
        }

        /// <summary>
        /// Состояние для окна в трее (канал читают и обычные пользователи): MCP, проверки баз, обновления, журнал агента.
        /// Без токена и паролей; адрес MCP — без токена. Журнал агента и так без секретов и данных 1С.
        /// </summary>
        public JObject Status()
        {
            AgentConfig cfg = null;
            try { cfg = AgentConfig.Load(); } catch { }
            JArray bases;
            lock (_baseErrors)
                bases = new JArray((cfg?.Bases ?? new List<BaseConfig>()).Select(b => new JObject
                {
                    ["slug"] = b.Slug, ["name"] = b.DisplayName, ["configuration"] = b.Configuration, ["version"] = b.Version,
                    ["verified"] = b.VerifiedAt.HasValue ? (JToken)b.VerifiedAt.Value.ToString("yyyy-MM-dd HH:mm") : null,
                    ["last_check_ok"] = b.LastCheckOk,
                    ["error"] = _baseErrors.TryGetValue(b.Slug, out var e) ? e : null,
                }));
            JArray log;
            lock (_tail) log = new JArray(_tail.Select(x => new JObject { ["time"] = x.Time, ["level"] = x.Level.ToString(), ["text"] = x.Text }));
            return new JObject
            {
                ["version"] = McpHost.Version,
                ["started"] = _started,
                ["mcp"] = new JObject
                {
                    ["running"] = _host.Running, ["port"] = cfg?.Port ?? 8796, ["error"] = _host.Running ? null : _mcpError,
                    ["starting"] = !_host.Running && !_cloudMode && _retriesLeft != int.MaxValue,
                },
                ["mode"] = _cloudMode ? "cloud" : "local",
                ["cloud"] = SafeCloudStatus(),
                ["bases"] = bases,
                ["last_health"] = _lastHealth,
                ["update_available"] = _updateAvailable,
                ["log"] = log,
            };
        }

        private JObject SafeCloudStatus()
        {
            try { return _cloud?.Status(); } catch (Exception e) { return new JObject { ["error"] = e.Message }; }
        }

        public void Dispose()
        {
            _mode?.Dispose();
            _mcpRetry?.Dispose(); _health?.Dispose(); _updates?.Dispose();
            try { _cloud?.Stop(); } catch { }
            _pipe?.Dispose();
            try { _host.Dispose(); } catch { }
            Log.Written -= Remember;
            Log.Info(Txt.S("SvcStopped"));
        }
    }
}
