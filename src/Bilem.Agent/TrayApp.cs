using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Bilem.Agent.Core;
using Bilem.Agent.Core.Service;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent
{
    /// <summary>
    /// Окно в трее (часть I): без прав администратора, у каждого вошедшего пользователя своё. Показывает состояние службы BilemAgent
    /// и MCP, проверки баз, журнал службы. Всё, что меняет систему, — кнопки со щитом (UAC): «Настройки…»,
    /// «Запустить службу». Закрыть окно — в трей; выход из трея не останавливает MCP (он в службе).
    /// </summary>
    internal sealed class TrayApp : BilemForm
    {
        private readonly NotifyIcon _tray = new NotifyIcon { Text = "Bilem Agent" };
        private readonly Label _service = new Label { AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
        private readonly Label _mcp = new Label { AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
        private readonly Label _update = new Label { AutoSize = true, Margin = new Padding(0, 2, 0, 2), Visible = false };
        private readonly ListView _bases = new ListView { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.Nonclickable };
        private readonly ListView _journal = new ListView { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.Nonclickable };
        private readonly Button _settings = new Button { Text = Txt.S("UiTraySettings"), AutoSize = true, FlatStyle = FlatStyle.System };
        private readonly Button _startSvc = new Button { Text = Txt.S("UiTrayStartService"), AutoSize = true, FlatStyle = FlatStyle.System, Visible = false };
        private readonly System.Windows.Forms.Timer _poll = new System.Windows.Forms.Timer { Interval = 2000 };
        private readonly bool _showOnStart;
        private bool _exiting;
        private EventWaitHandle _showEvent;
        private AgentHealth? _shownHealth;
        private string _shownTip;
        private DateTime _lastLog = DateTime.MinValue;

        private static readonly Dictionary<AgentHealth, Icon> Icons = new Dictionary<AgentHealth, Icon>
        {
            [AgentHealth.Green] = LoadIcon("tray-green.ico", SystemInformation.SmallIconSize),
            [AgentHealth.Yellow] = LoadIcon("tray-yellow.ico", SystemInformation.SmallIconSize),
            [AgentHealth.Red] = LoadIcon("tray-red.ico", SystemInformation.SmallIconSize),
            [AgentHealth.Grey] = LoadIcon("tray-grey.ico", SystemInformation.SmallIconSize),
        };

        public TrayApp(bool show)
        {
            _showOnStart = show;
            Text = Txt.S("UiTitleTray");
            Size = new Size(Px(760), Px(520));
            MinimumSize = new Size(Px(560), Px(380));
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;

            var head = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, Padding = new Padding(10, 8, 10, 4), WrapContents = false };
            head.Controls.AddRange(new Control[] { _service, _mcp, _update });

            _bases.Columns.Add(Txt.S("UiColSlug"), Px(90));
            _bases.Columns.Add(Txt.S("UiColConfig"), Px(220));
            _bases.Columns.Add(Txt.S("UiColVersion"), Px(70));
            _bases.Columns.Add(Txt.S("UiColStatus"), Px(320));
            _journal.Columns.Add(Txt.S("UiColTime"), Px(120));
            _journal.Columns.Add(Txt.S("UiColType"), Px(60));
            _journal.Columns.Add(Txt.S("UiColEvent"), Px(520));
            var basesBox = new GroupBox { Text = Txt.S("UiTabBases"), Dock = DockStyle.Top, Height = Px(130), Padding = new Padding(6) };
            basesBox.Controls.Add(_bases);
            var journalBox = new GroupBox { Text = Txt.S("UiTrayJournal"), Dock = DockStyle.Fill, Padding = new Padding(6) };
            journalBox.Controls.Add(_journal);

            // Нижняя панель (Слава, 23.10): слева «Настройки…» (и «Запустить службу», если она остановлена), справа — версия.
            // «Скопировать адрес MCP» и «Перезапустить MCP» убраны: локальный MCP на серверах клиентов выключен, работает облако.
            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            buttons.Controls.AddRange(new Control[] { _settings, _startSvc });
            foreach (var b in new[] { _settings, _startSvc }) Shield(b);
            // Полная версия, с бета-суффиксом: «0.3.1» — стабильная, «0.3.1-beta.1» — бета
            var version = new Label { Text = Txt.F("UiVersion", Bilem.Agent.Core.Mcp.McpHost.Version), AutoSize = true, Anchor = AnchorStyles.Right | AnchorStyles.Bottom, ForeColor = SystemColors.GrayText, Margin = new Padding(8, 0, 2, 6) };
            var bar = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, RowCount = 1, Padding = new Padding(8) };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.Controls.Add(buttons, 0, 0);
            bar.Controls.Add(version, 1, 0);
            _settings.Click += (s, e) => OpenSettings(null);
            _startSvc.Click += (s, e) => Admin("start-service", null);
            var note = new Label { Text = Txt.S("UiTrayNote"), Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(10, 0, 10, 4), ForeColor = SystemColors.GrayText };

            Controls.Add(journalBox);
            Controls.Add(basesBox);
            Controls.Add(head);
            Controls.Add(note);
            Controls.Add(bar);

            var menu = new ContextMenuStrip();
            menu.Items.Add(Txt.S("UiTrayOpen"), null, (s, e) => ShowWindow());
            menu.Items.Add(Txt.S("UiTraySettings"), null, (s, e) => OpenSettings(null));
            // «Скопировать адрес MCP» и «Перезапустить MCP» убраны и из трея (Слава, 23.10) — как с нижней панели окна
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Txt.S("UiTrayExitTray"), null, (s, e) => { _exiting = true; Close(); });
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (s, e) => ShowWindow();
            _tray.Icon = Icons[AgentHealth.Yellow];
            _tray.Visible = true;

            Resize += (s, e) => { if (WindowState == FormWindowState.Minimized) Hide(); };
            FormClosing += (s, e) =>
            {
                if (!_exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
                _poll.Stop();
                _tray.Visible = false;
            };
            _poll.Tick += (s, e) => Poll();
            Load += (s, e) => FillLastColumn(_journal);
        }

        private bool _started;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (_started) return;
            _started = true;
            // Окно может так и не показаться (автозапуск в трей) — опрос службы запускаем при создании окна, а не по Load
            ListenForShow();
            Poll();
            _poll.Start();
        }

        protected override void SetVisibleCore(bool value)
        {
            // Автозапуск при входе (--tray) — сразу в трей, без мелькания окна
            if (!_showOnStart && !IsHandleCreated) { CreateHandle(); value = false; }
            base.SetVisibleCore(value);
        }

        private void ShowWindow()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        private void ListenForShow()
        {
            try
            {
                _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.TrayShowEventName);
                new Thread(() =>
                {
                    while (true)
                    {
                        _showEvent.WaitOne();
                        try { BeginInvoke((Action)ShowWindow); } catch { return; }
                    }
                }) { IsBackground = true }.Start();
            }
            catch { }
        }

        private void OpenSettings(string tab) => Program.Elevate("--settings" + (tab == null ? "" : " " + tab));

        /// <summary>Действие администратора — отдельным процессом с UAC; «Нет» в окне UAC — ничего не происходит.</summary>
        private void Admin(string action, string done)
        {
            Task.Run(() => Program.Elevate("--admin " + action, wait: true)).ContinueWith(t =>
            {
                if (IsDisposed) return;
                BeginInvoke((Action)(() =>
                {
                    if (t.Result && done != null) _tray.ShowBalloonTip(3000, "Bilem Agent", done, ToolTipIcon.Info);
                    Poll();
                }));
            });
        }

        private void Poll()
        {
            Task.Run(() => (Status: ServiceInfo.Status, Reply: StatusPipe.Ask())).ContinueWith(t =>
            {
                if (IsDisposed || !IsHandleCreated || t.Status != TaskStatus.RanToCompletion) return;
                try { BeginInvoke((Action)(() => Render(t.Result.Status, t.Result.Reply))); } catch { }
            });
        }

        /// <summary>Состояние — словами (решение Славы: в окне только чёрный текст); цвет — только у значка в трее.</summary>
        private void Render(ServiceControllerStatus? svc, JObject st)
        {
            var running = svc == ServiceControllerStatus.Running;
            _service.Text = Txt.S(svc == null ? "UiTraySvcMissing" : running ? (st == null ? "UiTraySvcNoAnswer" : "UiTraySvcRunning")
                : svc == ServiceControllerStatus.StartPending ? "UiTraySvcStarting" : "UiTraySvcStopped");
            _startSvc.Visible = svc != null && !running && svc != ServiceControllerStatus.StartPending;
            var m = st?["mcp"];
            var port = (int?)m?["port"] ?? 8796;
            var bases = (st?["bases"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            var mcpRunning = (bool?)m?["running"] == true;
            var starting = (bool?)m?["starting"] == true;
            var mcpError = (string)m?["error"];
            _mcp.Text = st == null ? Txt.S("UiTrayMcpUnknown")
                : mcpRunning ? Txt.F("UiTrayMcpRunning", port, bases.Count)
                : starting ? Txt.F("UiStateStartingSvc", port)
                : Txt.F("UiStateStartFailed", mcpError ?? "—");
            var upd = (string)st?["update_available"];
            _update.Visible = upd != null;
            if (upd != null) _update.Text = Txt.F("UiTrayUpdate", upd);
            var cloud = (string)st?["mode"] == "cloud";
            // Bilem Cloud: локальный MCP выключен; строка — состояние канала
            if (cloud) _mcp.Text = MainForm.CloudStatusLine(st, bases.Count);

            _bases.BeginUpdate();
            _bases.Items.Clear();
            foreach (var b in bases)
            {
                var it = new ListViewItem((string)b["slug"]);
                it.SubItems.Add((string)b["name"] ?? (string)b["configuration"]);
                it.SubItems.Add((string)b["version"]);
                var err = (string)b["error"];
                it.SubItems.Add(err != null ? Txt.F("UiTrayBaseError", err)
                    : b["verified"]?.Type == JTokenType.String ? Txt.F("UiTrayBaseOk", (string)b["verified"]) : Txt.S("UiStatusNotChecked"));
                _bases.Items.Add(it);
            }
            _bases.EndUpdate();

            // Журнал службы — только новые строки
            foreach (var e in (st?["log"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var time = (DateTime)e["time"];
                if (time <= _lastLog) continue;
                _lastLog = time;
                var it = new ListViewItem(time.ToString("dd.MM HH:mm:ss"));
                it.SubItems.Add((string)e["level"]);
                it.SubItems.Add((string)e["text"]);
                _journal.Items.Insert(0, it);
            }
            while (_journal.Items.Count > 500) _journal.Items.RemoveAt(_journal.Items.Count - 1);

            // Значок: цвет — по тем же правилам, что раньше (Core.Health)
            var cst = (string)st?["cloud"]?["state"];
            var cloudOk = cloud && cst == "Working";
            var cloudErr = !cloud || cloudOk || cst == "Connecting" || cst == "NoKey" ? null : CloudPanel.StateText(st["cloud"] as JObject);
            var h = Health.Evaluate(new HealthInput
            {
                // В режиме облака «MCP работает» = канал на связи; нет связи / ключ отозван — красный
                McpRunning = cloud ? cloudOk : mcpRunning,
                McpError = !running ? Txt.S("UiTraySvcStoppedShort") : st == null ? Txt.S("UiTraySvcNoAnswerShort") : cloud ? cloudErr : (mcpRunning || starting ? null : mcpError),
                BaseCount = bases.Count,
                BaseErrors = bases.ToDictionary(b => (string)b["slug"], b => (string)b["error"]),
                BusySince = (cloud ? !cloudOk && cloudErr == null : starting) ? DateTime.Now : (DateTime?)null,
                Now = DateTime.Now,
            });
            if (_shownHealth != h.Color) { _tray.Icon = Icons[h.Color]; _shownHealth = h.Color; }
            if (_shownTip != h.Tooltip) { _tray.Text = h.Tooltip; _shownTip = h.Tooltip; }
        }

        /// <summary>Щит UAC на кнопке (BCM_SETSHIELD) — действие попросит права администратора.</summary>
        private static void Shield(Button b)
        {
            b.HandleCreated += (s, e) => SendMessage(b.Handle, 0x160C, IntPtr.Zero, new IntPtr(1));
        }

        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    }
}
