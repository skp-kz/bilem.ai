using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Bilem.Agent.Core;
using Bilem.Agent.Core.Mcp;

namespace Bilem.Agent
{
    /// <summary>Главное окно (вариант A, классические контролы Windows). MCP работает, пока открыто окно (сворачивается в трей).</summary>
    internal sealed class MainForm : BilemForm
    {
        private const string SiteUrl = "https://bilem.ai";
        private const string GitHubUrl = "https://github.com/skp-kz/bilem.ai";
        private const string WaitlistUrl = "https://bilem.ai";
        private const string IssuesUrl = "https://github.com/skp-kz/bilem.ai/issues/new";

        private readonly McpHost _host = new McpHost();
        private readonly NotifyIcon _tray = new NotifyIcon { Text = "Bilem Agent" };
        private readonly ToolStripStatusLabel _stState = new ToolStripStatusLabel();
        private readonly ToolStripStatusLabel _stVersion = new ToolStripStatusLabel { Alignment = ToolStripItemAlignment.Right };
        private readonly Button _mcpToggle = new Button { AutoSize = true };
        private bool _exiting;

        // Состояние для цвета трея (правила — Core.Health)
        private string _mcpError;
        private bool _mcpStoppedByUser;
        private int _busy;
        private DateTime? _busySince;
        private readonly System.Collections.Generic.Dictionary<string, string> _baseErrors = new System.Collections.Generic.Dictionary<string, string>();
        private AgentHealth? _shownHealth;
        private string _shownTip;
        private readonly Timer _healthTimer = new Timer { Interval = 1000 };
        private readonly Timer _recheckTimer = new Timer { Interval = 5 * 60 * 1000 };
        private System.Threading.EventWaitHandle _showEvent;

        private static readonly System.Collections.Generic.Dictionary<AgentHealth, Icon> TrayIcons = new System.Collections.Generic.Dictionary<AgentHealth, Icon>
        {
            [AgentHealth.Green] = LoadIcon("tray-green.ico", SystemInformation.SmallIconSize),
            [AgentHealth.Yellow] = LoadIcon("tray-yellow.ico", SystemInformation.SmallIconSize),
            [AgentHealth.Red] = LoadIcon("tray-red.ico", SystemInformation.SmallIconSize),
            [AgentHealth.Grey] = LoadIcon("tray-grey.ico", SystemInformation.SmallIconSize),
        };

        // Базы 1С
        private readonly ListView _bases = new ListView { View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, Dock = DockStyle.Fill };
        // Подключение
        private readonly TextBox _url = new TextBox { ReadOnly = true, Dock = DockStyle.Fill };
        private readonly CheckBox _showToken = new CheckBox { Text = Txt.S("UiShowToken"), AutoSize = true };
        // Доступ к данным
        private readonly ComboBox _accessBase = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        private readonly Label _accessWrite = new Label { AutoSize = true, Padding = new Padding(0, 4, 0, 4) };
        private readonly AccessTree _accessTree = new AccessTree { Dock = DockStyle.Fill };
        private readonly Label _accessSummary = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Padding = new Padding(0, 4, 0, 2) };
        private readonly Button _accessApply = new Button { Text = Txt.S("UiAccessApply"), AutoSize = true, Enabled = false };
        private readonly Button _accessCancel = new Button { Text = Txt.S("UiAccessCancel"), AutoSize = true, Enabled = false };
        private readonly ToolTip _tips = new ToolTip();
        private PrintPanel _print;
        private SmtpPanel _smtp;
        // Журнал
        private readonly ListView _journal = new ListView { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill };

        /// <summary>
        /// Окно настроек при установленной службе (часть I): MCP работает в службе BilemAgent — окно его не поднимает, значка в трее
        /// не ставит (трей — отдельный процесс без прав администратора), состояние берёт у службы по каналу.
        /// </summary>
        private readonly bool _svc;
        private Newtonsoft.Json.Linq.JObject _svcStatus;

        public MainForm(bool viaService = false, string tab = null)
        {
            _svc = viaService;
            Text = Txt.S(_svc ? "UiTitleSettings" : "UiTitle");
            // Компактно по умолчанию (логические пиксели — масштабируются под экран); растягивать можно
            Size = WindowPlacement.DefaultSize;
            StartPosition = FormStartPosition.CenterScreen;
            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(BasesTab());
            tabs.TabPages.Add(ConnectTab());
            tabs.TabPages.Add(AccessTab());
            _print = new PrintPanel(() => this) { Dock = DockStyle.Fill };
            var printTab = new TabPage(Txt.S("UiTabPrint"));
            printTab.Controls.Add(_print);
            tabs.TabPages.Add(printTab);
            _smtp = new SmtpPanel(() => this) { Dock = DockStyle.Fill };
            var smtpTab = new TabPage(Txt.S("UiTabSmtp"));
            smtpTab.Controls.Add(_smtp);
            tabs.TabPages.Add(smtpTab);
            tabs.TabPages.Add(JournalTab());
            tabs.TabPages.Add(ServiceTab());
            tabs.TabPages.Add(AboutTab());
            tabs.SelectedIndexChanged += (s, e) => { if (tabs.SelectedIndex == 2) RefreshAccess(); };
            // «Настройки…» из трея открывают нужную вкладку: bases, mcp, access, print, smtp, journal, service, about
            var tabIndex = Array.IndexOf(new[] { "bases", "mcp", "access", "print", "smtp", "journal", "service", "about" }, (tab ?? "").ToLowerInvariant());
            if (tabIndex > 0) tabs.SelectedIndex = tabIndex;

            // Строка состояния: слева — MCP, порт, базы; справа — версия сборки
            var status = new StatusStrip();
            _stVersion.Text = Txt.F("UiVersion", McpHost.Version);
            status.Items.Add(_stState);
            status.Items.Add(new ToolStripStatusLabel { Spring = true }); // в StatusStrip «вправо» — только через растяжку
            status.Items.Add(_stVersion);
            Controls.Add(tabs);
            Controls.Add(status);

            // Трей: цветной значок состояния; крестик — в трей, MCP продолжает работать; выход — только из меню трея
            var menu = new ContextMenuStrip();
            menu.Items.Add(Txt.S("UiTrayOpen"), null, (s, e) => ShowFromTray());
            menu.Items.Add(Txt.S("UiTrayCopyUrl"), null, (s, e) => { try { Clipboard.SetText(FullUrl()); } catch { } });
            menu.Items.Add(Txt.S("UiTrayResetWindow"), null, (s, e) => { WindowPlacement.Reset(this); ShowFromTray(); });
            // MCP из трея: «Остановить / Запустить» (как кнопка на вкладке «MCP») и «Перезапустить»
            menu.Items.Add(new ToolStripSeparator());
            var trayMcp = new ToolStripMenuItem();
            trayMcp.Click += (s, e) => ToggleMcp();
            var trayRestart = new ToolStripMenuItem(Txt.S("UiTrayMcpRestart"));
            trayRestart.Click += (s, e) => RestartMcp();
            menu.Opening += (s, e) =>
            {
                trayMcp.Text = Txt.S(_host.Running ? "UiTrayMcpStop" : "UiTrayMcpStart");
                trayRestart.Enabled = _host.Running;
            };
            menu.Items.Add(trayMcp);
            menu.Items.Add(trayRestart);
            // Тумблер «SMTP-клиент включён» — только на вкладке «SMTP» (решение Славы 19.10: из трея убран)
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Txt.S("UiTrayExit"), null, (s, e) => { _exiting = true; Close(); });
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (s, e) => ShowFromTray();
            _tray.Icon = TrayIcons[AgentHealth.Yellow];
            _tray.Visible = !_svc;
            Resize += (s, e) => { if (!_svc && WindowState == FormWindowState.Minimized) HideToTray(); };
            FormClosing += (s, e) =>
            {
                if (!_svc && !_exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HideToTray(); return; }
                _healthTimer.Stop();
                _recheckTimer.Stop();
                _host.Stop();
                _tray.Visible = false;
            };
            _healthTimer.Tick += (s, e) => { if (_svc && DateTime.Now.Second % 2 == 0) PollService(); UpdateHealth(); };
            _recheckTimer.Tick += (s, e) => { QuickCheckBases(); AutoCheckUpdates(); };

            Log.Written += en => { if (IsHandleCreated) BeginInvoke((Action)(() => AddJournal(en.Time, en.Level.ToString(), en.Text))); };
            Log.McpWritten += line => { if (IsHandleCreated) BeginInvoke((Action)(() => AddJournal(DateTime.Now, "MCP", line.Substring(line.IndexOf('\t') + 1).Replace('\t', ' ')))); };
            // Запоминаем только то, что пользователь изменил сам: перетаскивание, размер, развернуть/свернуть
            ResizeEnd += (s, e) => WindowPlacement.Save(this);
            var lastState = WindowState;
            Resize += (s, e) =>
            {
                if (WindowState == lastState || WindowState == FormWindowState.Minimized) return;
                lastState = WindowState;
                WindowPlacement.Save(this);
            };
            Load += (s, e) =>
            {
                MinimumSize = new Size(Px(WindowPlacement.MinSize.Width), Px(WindowPlacement.MinSize.Height));
                WindowPlacement.TryRestore(this);
                lastState = WindowState;
                ListenForSecondLaunch();
                Shortcuts.EnsureExist();
                if (_svc)
                {
                    // MCP, проверки баз и «Обновлено с X на Y» — в службе; окно только показывает её состояние
                    PollService();
                    RefreshAll();
                    _healthTimer.Start();
                    AutoCheckUpdates();
                    return;
                }
                // Было обновление — «Обновлено с X на Y» в журнал и аудит
                Bilem.Agent.Core.Updates.Updater.CompleteIfPending();
                StartMcpPatiently();
                RefreshAll();
                _healthTimer.Start();
                _recheckTimer.Start();
                QuickCheckBases();
                AutoCheckUpdates();
            };
        }

        // ---------------- Вкладки ----------------

        private TabPage BasesTab()
        {
            var p = new TabPage(Txt.S("UiTabBases"));
            _bases.Columns.Add(Txt.S("UiColSlug"), Px(50));
            _bases.Columns.Add(Txt.S("UiColConfig"), Px(170));
            _bases.Columns.Add(Txt.S("UiColVersion"), Px(60));
            _bases.Columns.Add(Txt.S("UiColFolder"), Px(100));
            _bases.Columns.Add(Txt.S("UiColStatus"), Px(140));
            FillLastColumn(_bases);
            var add = new Button { Text = Txt.S("UiAdd"), AutoSize = true };
            var verify = new Button { Text = Txt.S("UiVerify"), AutoSize = true };
            var remove = new Button { Text = Txt.S("UiRemove"), AutoSize = true };
            add.Click += (s, e) => AddBase();
            verify.Click += (s, e) => VerifyBase();
            remove.Click += (s, e) => RemoveBase();
            _bases.DoubleClick += (s, e) => VerifyBase();
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6) };
            bar.Controls.AddRange(new Control[] { add, verify, remove });
            p.Controls.Add(_bases);
            p.Controls.Add(bar);
            return p;
        }

        private TabPage ConnectTab()
        {
            var p = new TabPage(Txt.S("UiTabConnect"));
            // Либо бесплатно, либо Bilem Cloud (часть L): блок 1 — локальный MCP, блок 2 — облако; включено облако — блок 1 серый
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10), AutoScroll = true };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _localBox = new GroupBox { Text = Txt.S("UiLocalMcpTitle"), Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10) };
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
            _localBox.Controls.Add(t);
            root.Controls.Add(_localBox, 0, 0);
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var copy = new Button { Text = Txt.S("UiCopy"), AutoSize = true };
            copy.Click += (s, e) => { Clipboard.SetText(FullUrl()); Info(Txt.S("UiCopied")); };
            _showToken.CheckedChanged += (s, e) => RefreshUrl();
            int r = 0;
            t.Controls.Add(new Label { Text = Txt.S("UiMcpAddress"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, r);
            t.Controls.Add(_url, 1, r);
            t.Controls.Add(copy, 2, r); r++;
            t.Controls.Add(_showToken, 1, r); r++;
            var disclaimer = new Label { Text = Txt.S("UiTokenDisclaimer"), AutoSize = true, ForeColor = Color.DarkGoldenrod, Padding = new Padding(0, 4, 0, 8) };
            Wrap(disclaimer, t, 30);
            t.Controls.Add(disclaimer, 0, r); t.SetColumnSpan(disclaimer, 3); r++;

            // Кнопки — в две строки: подключение к клиентам; токен и MCP
            var clients = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            foreach (var (key, client) in new[] { ("UiConnectClaudeCode", "claude-code"), ("UiConnectClaudeDesktop", "claude-desktop"), ("UiConnectCursor", "cursor") })
            {
                var b = new Button { Text = Txt.S(key), AutoSize = true };
                b.Click += (s, e) => ConnectClient(client);
                clients.Controls.Add(b);
            }
            t.Controls.Add(clients, 0, r); t.SetColumnSpan(clients, 3); r++;
            var tokenRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            var rotate = new Button { Text = Txt.S("UiRotateToken"), AutoSize = true };
            rotate.Click += (s, e) => RotateToken();
            tokenRow.Controls.Add(rotate);
            _mcpToggle.Click += (s, e) => ToggleMcp();
            tokenRow.Controls.Add(_mcpToggle);
            t.Controls.Add(tokenRow, 0, r); t.SetColumnSpan(tokenRow, 3); r++;
            _localOff.Visible = false;
            t.Controls.Add(_localOff, 0, r); t.SetColumnSpan(_localOff, 3); r++;
            var docs = new LinkLabel { Text = Txt.S("UiLocalDocs"), AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
            docs.LinkClicked += (s, e) => Open("https://docs.bilem.ai/connect");
            t.Controls.Add(docs, 0, r); t.SetColumnSpan(docs, 3); r++;

            var cloudBox = new GroupBox { Text = Txt.S("UiCloudTitle"), Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10), Margin = new Padding(3, 10, 3, 3) };
            _cloud = new CloudPanel(() => this, _svc) { Location = new Point(10, 22) };
            cloudBox.Resize += (s, e) => _cloud.FitWidth(cloudBox.ClientSize.Width - Px(24));
            _cloud.ModeChanged += () => { ApplyCloudMode(); PollService(); };
            cloudBox.Controls.Add(_cloud);
            root.Controls.Add(cloudBox, 0, 1);
            r = 0;
            t = root;

            // «Данные на …»: выписка старше N рабочих дней — предупреждение в ответах с цифрами
            var staleRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 0) };
            staleRow.Controls.Add(new Label { Text = Txt.S("UiStaleDays"), AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
            var stale = new NumericUpDown { Minimum = 1, Maximum = 30, Width = 50, Value = Math.Max(1, Math.Min(30, AgentConfig.Load().StaleWorkdays)) };
            stale.ValueChanged += (s, e) => { var c = AgentConfig.Load(); c.StaleWorkdays = (int)stale.Value; c.Save(); Bilem.Agent.Core.Mcp.Tools.StaleWorkdays = c.StaleWorkdays; };
            staleRow.Controls.Add(stale);
            staleRow.Controls.Add(new Label { Text = Txt.S("UiStaleDaysNote"), AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(4, 6, 0, 0) });
            t.Controls.Add(staleRow, 0, 2);
            p.Controls.Add(root);
            return p;
        }

        private GroupBox _localBox;
        private CloudPanel _cloud;
        private readonly Label _localOff = new Label { Text = Txt.S("UiLocalOffByCloud"), AutoSize = true, Margin = new Padding(0, 6, 0, 0) };

        /// <summary>Включён Bilem Cloud — блок локального MCP серый, кнопки недоступны (порт закрыт службой).</summary>
        private void ApplyCloudMode()
        {
            var on = _cloud?.CloudOn == true;
            if (_localBox == null) return;
            foreach (Control c in _localBox.Controls[0].Controls) if (c != _localOff) c.Enabled = !on;
            _localOff.Visible = on;
        }

        /// <summary>«Доступ»: дерево разделов 1С с галочками «Чтение / Запись», «Применить» (слой MCP — сразу, слой 1С — с паролем администратора).</summary>
        private TabPage AccessTab()
        {
            var p = new TabPage(Txt.S("UiTabAccess"));
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10, 8, 10, 6) };
            for (int i = 0; i < 6; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles[2] = new RowStyle(SizeType.Percent, 100);

            var top = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0), WrapContents = false };
            top.Controls.Add(new Label { Text = Txt.S("UiAccessBase"), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 4, 0) });
            top.Controls.Add(_accessBase);
            var reload = new LinkLabel { Text = Txt.S("UiAccessReload"), AutoSize = true, Margin = new Padding(10, 6, 0, 0) };
            reload.LinkClicked += (s, e) => ReloadCatalog();
            top.Controls.Add(reload);
            _accessBase.SelectedIndexChanged += (s, e) => ShowAccess();
            // Строка-обещание (решение 0013) — та же, что на «О программе»
            var promise = new Label { Text = Txt.S("UiPromise"), AutoSize = true, ForeColor = SystemColors.GrayText, Padding = new Padding(0, 4, 0, 0) };
            Wrap(promise, t, 30);
            var header = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
            header.Controls.Add(top);
            header.Controls.Add(promise);
            t.Controls.Add(header, 0, 0);
            Wrap(_accessWrite, t, 30);
            t.Controls.Add(_accessWrite, 0, 1);
            _accessTree.Changed += UpdateAccessSummary;
            _accessTree.LockedClicked += () => ShowCloudPeople(true);
            t.Controls.Add(_accessTree, 0, 2);
            Wrap(_accessSummary, t, 30);
            t.Controls.Add(_accessSummary, 0, 3);

            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            _accessApply.Click += (s, e) => ApplyAccess();
            _accessCancel.Click += (s, e) => ShowAccess();
            buttons.Controls.Add(_accessApply);
            buttons.Controls.Add(_accessCancel);
            t.Controls.Add(buttons, 0, 4);

            // «Доступ по людям · Облако» — на своём месте, серым; по щелчку — что это даст и лист ожидания
            var cloud = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
            var head = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            var people = new LinkLabel { Text = Txt.S("UiCloudPeople"), AutoSize = true, LinkColor = SystemColors.GrayText, ActiveLinkColor = SystemColors.GrayText, Margin = new Padding(0, 3, 0, 0) };
            var badge = CloudBadge.Create(this);
            head.Controls.Add(people);
            head.Controls.Add(badge);
            _cloudPeopleText = new Label { Text = Txt.S("UiCloudPeopleText"), AutoSize = true, ForeColor = SystemColors.GrayText, Visible = false, Margin = new Padding(12, 2, 0, 0) };
            Wrap(_cloudPeopleText, t, 50);
            _cloudPeopleLink = new LinkLabel { Text = Txt.S("UiWaitlist"), AutoSize = true, Visible = false, Margin = new Padding(12, 2, 0, 0) };
            _cloudPeopleLink.LinkClicked += (s, e) => Open(WaitlistUrl);
            people.LinkClicked += (s, e) => ShowCloudPeople(!_cloudPeopleText.Visible);
            badge.Click += (s, e) => ShowCloudPeople(!_cloudPeopleText.Visible);
            cloud.Controls.Add(head);
            cloud.Controls.Add(_cloudPeopleText);
            cloud.Controls.Add(_cloudPeopleLink);
            t.Controls.Add(cloud, 0, 5);
            p.Controls.Add(t);
            return p;
        }

        private Label _cloudPeopleText;
        private LinkLabel _cloudPeopleLink;

        private void ShowCloudPeople(bool show)
        {
            _cloudPeopleText.Visible = show;
            _cloudPeopleLink.Visible = show;
        }

        private TabPage JournalTab()
        {
            var p = new TabPage(Txt.S("UiTabJournal"));
            _journal.Columns.Add(Txt.S("UiColTime"), Px(120));
            _journal.Columns.Add(Txt.S("UiColKind"), Px(60));
            _journal.Columns.Add(Txt.S("UiColText"), Px(320));
            FillLastColumn(_journal);
            var openLogs = new Button { Text = Txt.S("UiOpenLogs"), AutoSize = true };
            openLogs.Click += (s, e) => { Directory.CreateDirectory(Paths.LogsDir); Process.Start("explorer.exe", Paths.LogsDir); };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6) };
            bar.Controls.Add(openLogs);
            p.Controls.Add(_journal);
            p.Controls.Add(bar);
            return p;
        }

        /// <summary>«Сервис»: проверка и поддержка (диагностика, системная информация, отчёт, GitHub Issues) и заготовка обновлений.</summary>
        private TabPage ServiceTab()
        {
            var p = new TabPage(Txt.S("UiTabService"));
            // Прокрутка — у вкладки; root не прикреплён (Dock): AutoScroll прикреплённые элементы не учитывает
            p.AutoScroll = true;
            var root = new FlowLayoutPanel { AutoSize = true, Location = new Point(0, 0), FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12) };
            // Узкое окно: пояснение — под кнопкой, ширина — по окну
            // минус полоса вертикальной прокрутки — чтобы не появлялась горизонтальная
            int Inner() => Math.Max(Px(300), p.ClientSize.Width - Px(40) - SystemInformation.VerticalScrollBarWidth);
            var notes = new List<Label>();
            var boxes = new List<GroupBox>();
            p.Resize += (s, e) => { foreach (var n in notes) n.MaximumSize = new Size(Inner() - Px(24), 0); foreach (var g in boxes) g.MinimumSize = new Size(Inner(), 0); root.MaximumSize = new Size(Inner() + Px(30), 0); };

            // --- Проверка и поддержка
            var support = new GroupBox { Text = Txt.S("UiServiceSupport"), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10), Margin = new Padding(3, 12, 3, 3) };
            boxes.Add(support);
            var t = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
            Button B(string key) => new Button { Text = Txt.S(key), AutoSize = true, MinimumSize = new Size(Px(240), 0), Margin = new Padding(0, 6, 0, 0) };
            Label Note(string text) { var l = new Label { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.Left, MaximumSize = new Size(Px(480), 0), Margin = new Padding(0, 2, 0, 4) }; notes.Add(l); return l; }
            var row = 0;
            void Add(Control c) => t.Controls.Add(c, 0, row++);

            var diag = B("UiDiagButton");
            diag.Click += (s, e) => { using (var f = new DiagForm(McpRunning)) f.ShowDialog(this); };
            Add(diag); Add(Note(Txt.S("UiServiceDiagNote")));

            var sysinfo = B("UiSysInfo");
            sysinfo.Click += (s, e) => CopySysInfo();
            var saveSys = new LinkLabel { Text = Txt.S("UiSysInfoSave"), AutoSize = true, Anchor = AnchorStyles.Left };
            saveSys.LinkClicked += (s, e) => SaveSysInfo();
            var sysRight = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
            sysRight.Controls.Add(Note(Txt.S("UiSysInfoNote")));
            sysRight.Controls.Add(saveSys);
            Add(sysinfo); Add(sysRight);

            var report = B("UiSupportReport");
            report.Click += (s, e) =>
            {
                try
                {
                    var zip = SupportReport.Create();
                    Info(Txt.F("UiSupportReportDone", zip));
                    Process.Start("explorer.exe", "/select,\"" + zip + "\"");
                }
                catch (Exception ex) { Warn(ex.Message); }
            };
            Add(report); Add(Note(Txt.S("UiSupportReportNote")));

            var issue = B("UiReportIssue");
            issue.Click += (s, e) => Open(IssuesUrl);
            Add(issue); Add(Note(Txt.S("UiReportIssueNote")));

            // Веб-сервер для новых баз (часть J): «Авто» — по таблице без вопросов; IIS / Apache — вручную, для поддержки
            var webRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 10, 0, 0) };
            webRow.Controls.Add(new Label { Text = Txt.S("UiWebMode"), AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
            var webMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Px(160) };
            var modes = new[] { Bilem.Agent.Core.Web.WebChoice.ModeAuto, Bilem.Agent.Core.Web.WebServerKind.Iis, Bilem.Agent.Core.Web.WebServerKind.Apache };
            webMode.Items.AddRange(new object[] { Txt.S("UiWebModeAuto"), "IIS", Txt.S("WebNameApache") });
            webMode.SelectedIndex = Math.Max(0, Array.IndexOf(modes, (AgentConfig.Load().WebServerMode ?? modes[0]).ToLowerInvariant()));
            webMode.SelectedIndexChanged += (s, e) => { var c = AgentConfig.Load(); c.WebServerMode = webMode.SelectedIndex == 0 ? null : modes[webMode.SelectedIndex]; c.Save(); };
            webRow.Controls.Add(webMode);
            Add(webRow); Add(Note(Txt.S("UiWebModeNote")));

            var paid = new Label { Text = Txt.S("UiPaidSupportSoon"), AutoSize = true, ForeColor = SystemColors.GrayText, Font = new Font(Font.FontFamily, Font.Size * 0.9f), Margin = new Padding(0, 8, 0, 0) };
            notes.Add(paid); Add(paid);
            support.Controls.Add(t);
            root.Controls.Add(support);

            // --- Обновления: GitHub Releases, подпись Ed25519, «Обновить» / «Откатить»
            var upd = new GroupBox { Text = Txt.S("UiServiceUpdates"), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10) };
            boxes.Add(upd);
            var u = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
            var ur = 0;
            void UAdd(Control c) => u.Controls.Add(c, 0, ur++);
            UAdd(new Label { Text = Txt.F("UiInstalledVersion", McpHost.Version), AutoSize = true, Margin = new Padding(0, 3, 0, 4) });
            _updBeta = new CheckBox { Text = Txt.S("UiUpdBeta"), AutoSize = true, Margin = new Padding(0, 2, 0, 2), Checked = AgentConfig.Load().UpdateBeta };
            _updBeta.CheckedChanged += (s, e) => { var c = AgentConfig.Load(); c.UpdateBeta = _updBeta.Checked; c.Save(); _updAvailable = null; ShowUpdateState(); };
            UAdd(_updBeta);
            var check = B("UiCheckUpdates");
            check.Click += async (s, e) => await CheckUpdates(false);
            UAdd(check);
            _updStatus = Note("");
            UAdd(_updStatus);
            _updProgress = new ProgressBar { Width = Px(460), Height = Px(14), Visible = false, Margin = new Padding(0, 2, 0, 4), Maximum = 1000 };
            UAdd(_updProgress);
            _updNotes = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Width = Px(460), Height = Px(110), Visible = false, Margin = new Padding(0, 2, 0, 4) };
            UAdd(_updNotes);
            _updButton = B("UiUpdInstall");
            _updButton.Visible = false;
            _updButton.Click += async (s, e) => await InstallUpdate();
            UAdd(_updButton);
            _updRollback = new LinkLabel { AutoSize = true, Visible = false, Margin = new Padding(0, 6, 0, 0) };
            _updRollback.LinkClicked += (s, e) => Rollback();
            UAdd(_updRollback);
            UAdd(Note(Txt.S("UiUpdNote")));
            upd.Controls.Add(u);
            root.Controls.Add(upd);
            // Главная задача «Сервиса» — обновления: группа первой (пачка 7)
            root.Controls.SetChildIndex(upd, 0);
            ShowUpdateState();

            // --- Защита настроек: сразу под «Обновлениями» (решение Славы 20.10)
            var lockBox = new GroupBox { Text = Txt.S("UiLockGroup"), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10) };
            boxes.Add(lockBox);
            var lt = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
            var lr = 0;
            void LAdd(Control c) => lt.Controls.Add(c, 0, lr++);
            var lockState = new Label { AutoSize = true, Margin = new Padding(0, 3, 0, 4) };
            var lockSet = B("UiLockSet");
            var lockClear = new LinkLabel { Text = Txt.S("UiLockClear"), AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
            void ShowLock()
            {
                var on = SettingsLock.Enabled;
                lockState.Text = Txt.S(on ? "UiLockOn" : "UiLockOff");
                lockSet.Text = Txt.S(on ? "UiLockChange" : "UiLockSet");
                lockClear.Visible = on;
            }
            lockSet.Click += (s, e) => { using (var f = new SetLockForm(SettingsLock.Enabled)) if (f.ShowDialog(this) == DialogResult.OK) Info(Txt.S("UiLockSaved")); ShowLock(); };
            lockClear.LinkClicked += (s, e) =>
            {
                if (!SettingsGate.Pass(Txt.S("UiLockWhatClear"))) return;
                SettingsLock.Clear(Txt.S("UiLockHowWindow"));
                ShowLock();
            };
            LAdd(lockState); LAdd(lockSet); LAdd(lockClear); LAdd(Note(Txt.F("UiLockNote", "bilem-agent reset-settings-password")));
            lockBox.Controls.Add(lt);
            root.Controls.Add(lockBox);
            root.Controls.SetChildIndex(lockBox, 1);
            ShowLock();

            p.Controls.Add(root);
            return p;
        }

        // ---------------- Обновления ----------------

        /// <summary>Закрыть агент совсем (не в трей) — перед установкой обновления.</summary>
        private void ExitApp() { _exiting = true; Close(); }

        /// <summary>Автопроверка раз в сутки (таймер — раз в 5 минут смотрит, пора ли).</summary>
        private void AutoCheckUpdates()
        {
            var last = Bilem.Agent.Core.Updates.Updater.LastCheck;
            if (last == null || DateTime.Now - last.Value > TimeSpan.FromDays(1)) _ = CheckUpdates(true);
        }

        private CheckBox _updBeta;
        private Label _updStatus;
        private ProgressBar _updProgress;
        private TextBox _updNotes;
        private Button _updButton;
        private LinkLabel _updRollback;
        private Bilem.Agent.Core.Updates.ReleaseInfo _updAvailable;
        private List<Bilem.Agent.Core.Updates.ReleaseInfo> _updReleases;
        private bool _updBusy;

        private void ShowUpdateState(string status = null)
        {
            if (_updStatus == null) return;
            var last = Bilem.Agent.Core.Updates.Updater.LastCheck;
            _updStatus.Text = status ?? (_updAvailable != null ? Txt.F("UiUpdAvailable", _updAvailable.Version)
                : last == null ? Txt.S("UiUpdNeverChecked") : Txt.F("UiUpdUpToDate", last.Value));
            _updNotes.Visible = _updAvailable != null;
            _updNotes.Text = _updAvailable == null ? "" : (_updAvailable.Notes ?? "").Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
            _updButton.Visible = _updAvailable != null;
            if (_updAvailable != null) _updButton.Text = Txt.F("UiUpdInstallTo", _updAvailable.Version);
            var cur = Bilem.Agent.Core.Updates.SemVer.Current;
            var prev = Bilem.Agent.Core.Updates.Updater.Stored().Where(x => !x.Version.Equals(cur)).Select(x => x.Version).FirstOrDefault(v => v < cur);
            _updRollback.Visible = prev != null;
            if (prev != null) { _updRollback.Text = Txt.F("UiUpdRollbackTo", prev); _updRollback.Tag = prev; }
        }

        /// <summary>Проверка обновлений: кнопкой — с ответом в окне; автопроверка раз в сутки — тихо (нет сети — строка в журнале).</summary>
        private async Task CheckUpdates(bool quiet)
        {
            if (_updBusy) return;
            _updBusy = true;
            try
            {
                if (!quiet) ShowUpdateState(Txt.S("UiUpdChecking"));
                var beta = AgentConfig.Load().UpdateBeta;
                var (newer, all) = await Task.Run(() =>
                {
                    var list = Bilem.Agent.Core.Updates.Updater.Releases();
                    var l = Bilem.Agent.Core.Updates.Updater.Latest(list, beta);
                    return (l != null && l.Version > Bilem.Agent.Core.Updates.SemVer.Current ? l : null, list);
                });
                _updReleases = all;
                var was = _updAvailable?.Version;
                _updAvailable = newer;
                ShowUpdateState();
                if (newer != null && (was == null || !was.Equals(newer.Version)))
                {
                    Log.Info(Txt.F("UpdAvailableLog", newer.Version));
                    if (quiet) _tray.ShowBalloonTip(8000, "Bilem Agent", Txt.F("UiUpdAvailable", newer.Version), ToolTipIcon.Info);
                }
            }
            catch (Exception ex)
            {
                if (quiet) Log.Info(Txt.F("UpdCheckFailedQuiet", ex.Message));
                else { Log.Warn(ex.Message); ShowUpdateState(ex.Message); }
            }
            finally { _updBusy = false; }
        }

        private async Task InstallUpdate()
        {
            var r = _updAvailable;
            if (r == null || _updBusy) return;
            _updBusy = true;
            _updButton.Enabled = false;
            try
            {
                ShowUpdateState(Txt.F("UiUpdDownloading", r.Version));
                _updProgress.Value = 0;
                _updProgress.Style = ProgressBarStyle.Marquee;
                _updProgress.Visible = true;
                var cur = Bilem.Agent.Core.Updates.SemVer.Current;
                // Полоса и «получено из» — с потока скачивания через окно (не чаще 5 раз в секунду)
                var downloaded = false;
                void Progress(long got, long total) => BeginInvoke((Action)(() =>
                {
                    if (downloaded) return;
                    if (total > 0)
                    {
                        _updProgress.Style = ProgressBarStyle.Continuous;
                        _updProgress.Value = (int)Math.Min(1000, got * 1000 / total);
                        _updStatus.Text = Txt.F("UiUpdDownloadingProgress", r.Version, got / 1048576.0, total / 1048576.0);
                    }
                    else _updStatus.Text = Txt.F("UiUpdDownloadingBytes", r.Version, got / 1048576.0);
                }));
                var msi = await Task.Run(() =>
                {
                    // MSI текущей версии — для «Откатить», если его ещё нет
                    Bilem.Agent.Core.Updates.Updater.EnsureRollback(cur, _updReleases ?? Bilem.Agent.Core.Updates.Updater.Releases());
                    return Bilem.Agent.Core.Updates.Updater.Download(r, Progress);
                });
                downloaded = true;
                _updProgress.Style = ProgressBarStyle.Continuous;
                _updProgress.Value = 1000;
                ShowUpdateState(Txt.F("UiUpdInstalling", r.Version));
                _updInstalling = r.Version.ToString();
                UpdateHealth();
                Application.DoEvents();
                Bilem.Agent.Core.Updates.Updater.Install(msi, cur, r.Version, Application.ExecutablePath);
                ExitApp();
            }
            catch (Exception ex) { _updInstalling = null; _updProgress.Visible = false; Warn(ex.Message); ShowUpdateState(ex.Message); UpdateHealth(); }
            finally { _updBusy = false; _updButton.Enabled = true; }
        }

        private void Rollback()
        {
            if (!(_updRollback.Tag is Bilem.Agent.Core.Updates.SemVer prev)) return;
            if (MessageBox.Show(this, Txt.F("UiUpdRollbackConfirm", prev), Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            try
            {
                var msi = Bilem.Agent.Core.Updates.Updater.Stored().First(x => x.Version.Equals(prev)).Msi;
                Bilem.Agent.Core.Updates.Updater.Install(msi, Bilem.Agent.Core.Updates.SemVer.Current, prev, Application.ExecutablePath, rollback: true);
                ExitApp();
            }
            catch (Exception ex) { Warn(ex.Message); }
        }

        private void CopySysInfo()
        {
            try
            {
                var text = SysInfo.Collect();
                Clipboard.SetText(text);
                Info(Txt.S("UiSysInfoCopied"));
            }
            catch (Exception ex) { Warn(ex.Message); }
        }

        private void SaveSysInfo()
        {
            using (var d = new SaveFileDialog { FileName = $"bilem-sysinfo-{DateTime.Now:yyyyMMdd-HHmm}.txt", Filter = Txt.S("UiTextFilter"), InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                File.WriteAllText(d.FileName, SysInfo.Collect(), new System.Text.UTF8Encoding(false));
            }
        }

        private TabPage AboutTab()
        {
            var p = new TabPage(Txt.S("UiTabAbout"));
            var f = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(16), WrapContents = false };
            f.Controls.Add(new Label { Text = "Bilem Agent " + McpHost.Version, AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 14f) });
            var aboutText = new Label { Text = Txt.S("UiAboutText"), AutoSize = true, Padding = new Padding(0, 8, 0, 8) };
            Wrap(aboutText, f, 40);
            f.Controls.Add(aboutText);
            foreach (var (text, url) in new[] { ("bilem.ai", SiteUrl), (Txt.S("UiAboutGitHub"), GitHubUrl), (Txt.S("UiWaitlist"), WaitlistUrl) })
            {
                var l = new LinkLabel { Text = text, AutoSize = true, Padding = new Padding(0, 2, 0, 2) };
                l.LinkClicked += (s, e) => Open(url);
                f.Controls.Add(l);
            }
            f.Controls.Add(new Label { Text = Txt.F("UiAboutData", Paths.Home), AutoSize = true, ForeColor = SystemColors.GrayText, Padding = new Padding(0, 12, 0, 0) });
            p.Controls.Add(f);
            return p;
        }

        // ---------------- Действия ----------------

        private void StartMcp()
        {
            _mcpError = null;
            _mcpStoppedByUser = false;
            try { _host.Start(); }
            catch (AgentException ex) { _mcpError = ex.Short ?? ex.Message; Log.Fail(ex.Message); if (!string.IsNullOrEmpty(ex.Hint)) Log.Fail(Txt.S("WhatToDo") + " " + ex.Hint); }
            catch (Exception ex) { _mcpError = ex.Message; Log.Fail(ex.Message); }
            UpdateHealth();
        }

        /// <summary>
        /// Запуск MCP при старте агента — с терпением: после «Обновить» новый агент стартует, пока прежний процесс ещё
        /// держит порт 8796 (сервер 19.10). Первые попытки — молча, раз в 2 с до 30 с (в трее — жёлтый «подключение»);
        /// в журнал ошибка попадает, только если порт так и не освободился. Дальше — кнопка «Запустить».
        /// </summary>
        private void StartMcpPatiently()
        {
            _mcpStoppedByUser = false;
            _mcpError = null;
            if (TryStartMcp(out _)) { UpdateHealth(); return; }
            _mcpStartingSince = DateTime.Now;
            _busySince = _busySince ?? DateTime.Now;
            _busy++;
            UpdateHealth();
            var left = (int)(McpPatience.TotalSeconds / 2);
            var t = new Timer { Interval = 2000 };
            t.Tick += (s, e) =>
            {
                if (_mcpStoppedByUser || _host.Running) { Done(); return; }
                if (TryStartMcp(out var err)) { Done(); return; }
                if (--left > 0) { UpdateHealth(); return; }
                Done();
                StartMcp();   // последняя попытка — уже с ошибкой в журнале и в трее
            };
            void Done()
            {
                t.Stop(); t.Dispose();
                _mcpStartingSince = null;
                if (--_busy == 0) _busySince = null;
                UpdateHealth();
                RefreshAll();
            }
            t.Start();
        }

        /// <summary>Сколько ждать порт при запуске агента, прежде чем показать «Ошибка запуска MCP».</summary>
        private static readonly TimeSpan McpPatience = TimeSpan.FromSeconds(30);
        private DateTime? _mcpStartingSince;
        /// <summary>Идёт установка обновления — версия (для строки состояния).</summary>
        private string _updInstalling;

        private bool TryStartMcp(out Exception error)
        {
            error = null;
            try { _host.Start(); return true; }
            catch (Exception ex) { error = ex; return false; }
        }

        /// <summary>Состояние службы по каналу — в фоне, без задержки окна.</summary>
        private void PollService()
        {
            System.Threading.Tasks.Task.Run(() => Bilem.Agent.Core.Service.StatusPipe.Ask())
                .ContinueWith(t => { if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)(() => { _svcStatus = t.Result; UpdateHealth(); })); });
        }

        private bool McpRunning => _svc ? (bool?)_svcStatus?["mcp"]?["running"] == true : _host.Running;

        /// <summary>Перезапуск MCP (из трея): остановить и сразу запустить; клиенты переподключатся сами.</summary>
        private void RestartMcp()
        {
            if (_svc)
            {
                var r = Bilem.Agent.Core.Service.StatusPipe.Ask("restart-mcp", 5000);
                if (r == null) Warn(Txt.S("UiSvcNotRunning"));
                else if (r["error"] != null) Warn((string)r["error"]);
                PollService();
                return;
            }
            if (_host.Running) _host.Stop();
            Log.Info(Txt.S("McpRestarting"));
            StartMcp();
            RefreshAll();
        }

        private void ToggleMcp()
        {
            // Со службой MCP не останавливают из окна — только перезапуск (остановить службу — в «Службах» Windows)
            if (_svc) { RestartMcp(); return; }
            if (_host.Running) { _host.Stop(); _mcpStoppedByUser = true; }
            else StartMcp();
            RefreshAll();
        }

        /// <summary>Жёлтый в трее на время настройки, проверки, удаления (дольше 60 с — красный «долго нет ответа»).</summary>
        private void Busy(Action a)
        {
            if (_busy++ == 0) _busySince = DateTime.Now;
            UpdateHealth();
            try { a(); }
            finally { if (--_busy == 0) _busySince = null; UpdateHealth(); }
        }

        private void AddBase()
        {
            using (var f = new AddBaseForm())
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                var o = f.Options;
                Busy(() => { using (var run = new RunForm(Txt.F("UiSetupTitle", o.Slug), ob => ob.Setup(o))) run.ShowDialog(this); });
                o.AdminPassword = null;
            }
            AfterChange();
        }

        private BaseConfig Selected() =>
            _bases.SelectedItems.Count == 1 ? AgentConfig.Load().Find((string)_bases.SelectedItems[0].Tag) : null;

        private void VerifyBase()
        {
            var b = Selected();
            if (b == null) { Info(Txt.S("UiSelectBase")); return; }
            Busy(() => { using (var run = new RunForm(Txt.F("UiVerifyTitle", b.Slug), ob => ob.Verify(b.Slug))) run.ShowDialog(this); });
            AfterChange();
        }

        private void RemoveBase()
        {
            var b = Selected();
            if (b == null) { Info(Txt.S("UiSelectBase")); return; }
            var ans = MessageBox.Show(this, Txt.F("UiRemoveConfirm", b.Slug, b.Path), Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button3);
            if (ans == DialogResult.Cancel) return;
            var drop = ans == DialogResult.Yes;
            string user = null, pwd = null;
            if (drop)
            {
                using (var c = new CredForm(Txt.S("UiRemoveTitle"), Txt.S("UiRemoveCredHint")))
                {
                    if (c.ShowDialog(this) != DialogResult.OK) return;
                    user = c.User; pwd = c.Password;
                }
            }
            Busy(() => { using (var run = new RunForm(Txt.F("UiRemoveRunTitle", b.Slug), ob => ob.Remove(b.Slug, drop, user, pwd))) run.ShowDialog(this); });
            AfterChange();
        }

        private void ConnectClient(string client)
        {
            var ok = Connectors.Connect(client, FullUrl());
            if (ok) Info(Txt.S("UiConnectDone_" + client.Replace("-", "")));
            else Warn(Txt.S("UiConnectFailed"));
        }

        private void RotateToken()
        {
            if (MessageBox.Show(this, Txt.S("UiRotateConfirm"), Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            AgentConfig.Load().RotateToken();
            _host.Reload();
            RefreshUrl();
            Info(Txt.S("UiRotateDone"));
        }

        private void AfterChange()
        {
            // Служба сама перечитывает config.json (раз в 2 с) и проверяет базы
            if (_svc) { RefreshAll(); return; }
            _host.Reload();
            RefreshAll();
            QuickCheckBases();
        }

        // ---------------- Трей: цвет состояния ----------------

        /// <summary>Короткая проверка всех баз в фоне (пароль bilem + $metadata) — при запуске, раз в 5 минут и после изменений.</summary>
        private void QuickCheckBases()
        {
            var bases = AgentConfig.Load().Bases.ToList();
            if (_busy++ == 0) _busySince = DateTime.Now;
            UpdateHealth();
            System.Threading.Tasks.Task.Run(() => bases.Select(b => (b.Slug, Err: Health.QuickCheck(b))).ToList())
                .ContinueWith(t => BeginInvoke((Action)(() =>
                {
                    lock (_baseErrors)
                    {
                        _baseErrors.Clear();
                        if (t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
                            foreach (var (slug, err) in t.Result) _baseErrors[slug] = err;
                    }
                    if (--_busy == 0) _busySince = null;
                    UpdateHealth();
                })));
        }

        private void UpdateHealth()
        {
            if (IsDisposed) return;
            var cfg = AgentConfig.Load();
            if (_svc)
            {
                // Строка состояния — по службе: работает / запускается / ошибка / служба не отвечает
                var m = _svcStatus?["mcp"];
                _stState.ForeColor = SystemColors.ControlText;
                _cloud?.UpdateStatus(_svcStatus);
                if (_updInstalling != null) _stState.Text = Txt.F("UiStateUpdating", _updInstalling);
                else if (_svcStatus == null) _stState.Text = Txt.S("UiSvcNotRunning");
                else if ((string)_svcStatus["mode"] == "cloud") _stState.Text = CloudStatusLine(_svcStatus, cfg.Bases.Count);
                else if ((bool?)m?["running"] == true) _stState.Text = Txt.F("UiStateRunningSvc", cfg.Port, cfg.Bases.Count);
                else if ((bool?)m?["starting"] == true) _stState.Text = Txt.F("UiStateStartingSvc", cfg.Port);
                else _stState.Text = Txt.F("UiStateStartFailed", (string)m?["error"] ?? "—");
                _mcpToggle.Text = Txt.S("UiTrayMcpRestart");
                return;
            }
            HealthState h;
            lock (_baseErrors)
            {
                h = Health.Evaluate(new HealthInput
                {
                    McpRunning = _host.Running,
                    McpError = _mcpError,
                    McpStoppedByUser = _mcpStoppedByUser,
                    BaseCount = cfg.Bases.Count,
                    BaseErrors = new System.Collections.Generic.Dictionary<string, string>(_baseErrors),
                    BusySince = _busySince,
                    Now = DateTime.Now,
                });
            }
            // Значок меняется только при смене состояния — без мигания
            if (_shownHealth != h.Color) { _tray.Icon = TrayIcons[h.Color]; _shownHealth = h.Color; }
            if (_shownTip != h.Tooltip) { _tray.Text = h.Tooltip; _shownTip = h.Tooltip; }
            // Строка состояния: что происходит с MCP — работает / запускается (ждём порт) / ошибка запуска / остановлен / обновление.
            // Только чёрный текст, без цветов — и для ошибок (решение Славы 19.10: инженерное приложение)
            _stState.ForeColor = SystemColors.ControlText;
            if (_updInstalling != null) { _stState.Text = Txt.F("UiStateUpdating", _updInstalling); }
            else if (_host.Running) { _stState.Text = Txt.F("UiStateRunning", cfg.Port, cfg.Bases.Count); }
            else if (_mcpStartingSince.HasValue)
            {
                var sec = (int)(DateTime.Now - _mcpStartingSince.Value).TotalSeconds;
                _stState.Text = Txt.F("UiStateStarting", cfg.Port, sec, (int)McpPatience.TotalSeconds);
            }
            else if (!_mcpStoppedByUser && !string.IsNullOrEmpty(_mcpError)) { _stState.Text = Txt.F("UiStateStartFailed", _mcpError); }
            else { _stState.Text = Txt.F("UiStateStopped", cfg.Port, cfg.Bases.Count); }
            _mcpToggle.Text = Txt.S(_host.Running ? "UiMcpStop" : "UiMcpStart");
        }

        /// <summary>Строка состояния в режиме облака: «Bilem Cloud · баз в облаке: 1 из 2» или что не так (часть L).</summary>
        internal static string CloudStatusLine(Newtonsoft.Json.Linq.JObject svc, int total)
        {
            var st = svc?["cloud"] as Newtonsoft.Json.Linq.JObject;
            var bases = (st?["bases"] as Newtonsoft.Json.Linq.JArray ?? new Newtonsoft.Json.Linq.JArray()).OfType<Newtonsoft.Json.Linq.JObject>();
            var active = bases.Count(b => (string)b["state"] == "active");
            if ((string)st?["state"] == "Working" && !bases.Any(b => (string)b["state"] == "paused"))
                return Txt.F("UiStateCloud", active, total);
            return "Bilem Cloud · " + CloudPanel.StateText(st);
        }

        /// <summary>Повторный запуск exe — не второй экземпляр, а показ уже работающего окна.</summary>
        private void ListenForSecondLaunch()
        {
            try
            {
                _showEvent = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, Program.ShowEventName);
                var t = new System.Threading.Thread(() =>
                {
                    while (true)
                    {
                        _showEvent.WaitOne();
                        try { BeginInvoke((Action)ShowFromTray); } catch { return; }
                    }
                }) { IsBackground = true };
                t.Start();
            }
            catch (Exception e) { Log.Warn(e.Message); }
        }

        // ---------------- Отображение ----------------

        private void RefreshAll()
        {
            var cfg = AgentConfig.Load();
            _bases.Items.Clear();
            foreach (var b in cfg.Bases)
            {
                var it = new ListViewItem(b.Slug) { Tag = b.Slug };
                it.SubItems.Add(b.DisplayName);
                it.SubItems.Add(b.Version);
                it.SubItems.Add(b.Path);
                it.SubItems.Add(b.VerifiedAt.HasValue
                    ? (b.LastCheckOk == true ? Txt.F("UiStatusOk", b.VerifiedAt.Value)
                        : b.LastCheckOk == false ? Txt.F("UiStatusCheckFailed", b.VerifiedAt.Value)
                        : Txt.F("UiStatusChecked", b.VerifiedAt.Value))
                    : Txt.S("UiStatusNotChecked"));
                _bases.Items.Add(it);
            }
            RefreshUrl();
            RefreshAccess();
            _print?.Reload();
            _smtp?.Reload();
            _cloud?.Reload();
            ApplyCloudMode();
            UpdateHealth();
        }

        private string FullUrl()
        {
            var cfg = AgentConfig.Load();
            return cfg.McpUrl(cfg.GetOrCreateToken());
        }

        private void RefreshUrl()
        {
            var u = FullUrl();
            _url.Text = _showToken.Checked ? u : Connectors.Mask(u);
        }

        private void RefreshAccess()
        {
            var cfg = AgentConfig.Load();
            var cur = _accessBase.SelectedItem as string;
            _accessBase.Items.Clear();
            foreach (var b in cfg.Bases) _accessBase.Items.Add(b.Slug);
            if (_accessBase.Items.Count > 0) _accessBase.SelectedItem = cur != null && _accessBase.Items.Contains(cur) ? cur : _accessBase.Items[0];
            else ShowAccess();
        }

        private void ShowAccess()
        {
            var b = AgentConfig.Load().Find(_accessBase.SelectedItem as string ?? "");
            if (b == null)
            {
                _accessWrite.Text = Txt.S("NoBases");
                _accessTree.Load(new AccessCatalog(), null, null);
                UpdateAccessSummary();
                return;
            }
            var writes = b.WriteInOneC ?? new List<string>();
            var at = b.WriteDeniedAt ?? b.VerifiedAt;
            _accessWrite.Text = !at.HasValue ? Txt.S("UiWriteNotChecked")
                : writes.Count == 0 ? Txt.F("UiWriteDenied", at.Value) : Txt.F("UiWriteAllowedN", writes.Count, at.Value);
            _accessWrite.ForeColor = !at.HasValue ? Color.DarkGoldenrod : writes.Count == 0 ? Color.DarkGreen : Color.DarkGoldenrod;
            _tips.SetToolTip(_accessWrite, Txt.F("UiRoles", b.User, string.Join(", ", b.Roles)));
            _accessTree.Load(Access.CatalogOf(b), b.ReadEffective, b.Write);
            UpdateAccessSummary();
        }

        /// <summary>Сводка «что изменится» до «Применить».</summary>
        private void UpdateAccessSummary()
        {
            var b = AgentConfig.Load().Find(_accessBase.SelectedItem as string ?? "");
            if (b == null) { _accessSummary.Text = ""; _accessApply.Enabled = _accessCancel.Enabled = false; return; }
            var cat = Access.CatalogOf(b);
            var (read, write) = Access.Normalize(cat, _accessTree.Read, _accessTree.Write);
            var c = Access.Diff(b, read, write);
            var pending1C = !c.Any && Access.NeedsOneC(b, read, write);
            _accessApply.Enabled = c.Any || pending1C;
            _accessCancel.Enabled = c.Any;
            if (!c.Any)
            {
                _accessSummary.Text = pending1C ? Txt.S("UiAccessPending1C")
                    : Txt.F("UiAccessNow", read.Count, cat.Items.Count(i => !i.Locked), write.Count) + (cat.FromConfiguration ? "" : " " + Txt.S("UiAccessFallbackNote"));
                return;
            }
            string Names(List<string> l) => string.Join(", ", l.Take(3).Select(cat.TitleOf)) + (l.Count > 3 ? Txt.F("UiAccessMore", l.Count - 3) : "");
            var parts = new List<string>();
            if (c.ReadOn.Count > 0) parts.Add(Txt.F("UiAccessReadOn", c.ReadOn.Count, Names(c.ReadOn)));
            if (c.ReadOff.Count > 0) parts.Add(Txt.F("UiAccessReadOff", c.ReadOff.Count, Names(c.ReadOff)));
            if (c.WriteOn.Count > 0) parts.Add(Txt.F("UiAccessWriteOn", c.WriteOn.Count, Names(c.WriteOn)));
            if (c.WriteOff.Count > 0) parts.Add(Txt.F("UiAccessWriteOff", c.WriteOff.Count, Names(c.WriteOff)));
            _accessSummary.Text = Txt.S("UiAccessWillChange") + " " + string.Join("; ", parts) + ".";
        }

        /// <summary>
        /// «Применить»: слой MCP — сразу (инструменты больше не видят снятое); слой 1С — роли и состав OData, нужен пароль
        /// администратора 1С (не сохраняется), затем автоматическая проверка. Отказался от пароля — MCP уже применён, 1С — позже.
        /// </summary>
        private void ApplyAccess()
        {
            var cfg = AgentConfig.Load();
            var b = cfg.Find(_accessBase.SelectedItem as string ?? "");
            if (b == null) return;
            var cat = Access.CatalogOf(b);
            var (read, write) = Access.Normalize(cat, _accessTree.Read, _accessTree.Write);
            var c = Access.Diff(b, read, write);
            if (c.WriteOn.Count > 0 && (b.Write == null || b.Write.Count == 0)
                && MessageBox.Show(this, Txt.S("UiAccessWriteWarn"), Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

            if (c.Any)
            {
                b.Read = read.ToList();
                b.Write = write.ToList();
                cfg.Upsert(b);
                cfg.Save();
                _host.Reload();
                Log.Info(Txt.F("AccessChangedLog", b.Slug, Environment.UserName,
                    c.ReadOn.Count, c.ReadOff.Count, c.WriteOn.Count, c.WriteOff.Count,
                    string.Join(", ", c.ReadOn.Concat(c.ReadOff).Concat(c.WriteOn).Concat(c.WriteOff).Distinct().Select(cat.TitleOf))));
            }
            if (Access.NeedsOneC(b, read, write))
            {
                string user, pwd;
                using (var f = new CredForm(Txt.F("UiAccessCredTitle", b.Slug), Txt.S("UiAccessCredHint")))
                {
                    if (f.ShowDialog(this) != DialogResult.OK) { Info(Txt.S("UiAccessMcpOnly")); AfterChange(); return; }
                    user = f.User; pwd = f.Password;
                }
                var r = read.ToList(); var w = write.ToList();
                Busy(() => { using (var run = new RunForm(Txt.F("UiAccessRunTitle", b.Slug), ob => ob.ApplyAccess(b.Slug, user, pwd, r, w))) run.ShowDialog(this); });
            }
            AfterChange();
        }

        /// <summary>Разделы из подсистем 1С (для баз, подключённых до v0.2): нужен вход администратора, в базе ничего не меняется.</summary>
        private void ReloadCatalog()
        {
            var b = AgentConfig.Load().Find(_accessBase.SelectedItem as string ?? "");
            if (b == null) return;
            using (var f = new CredForm(Txt.F("UiAccessReloadTitle", b.Slug), Txt.S("UiAccessReloadHint")))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                var user = f.User; var pwd = f.Password;
                Busy(() => { using (var run = new RunForm(Txt.F("UiAccessReloadTitle", b.Slug), ob => ob.LoadCatalog(b.Slug, user, pwd))) run.ShowDialog(this); });
            }
            ShowAccess();
        }

        private void AddJournal(DateTime time, string kind, string text)
        {
            var it = new ListViewItem(time.ToString("dd.MM HH:mm:ss"));
            it.SubItems.Add(kind);
            it.SubItems.Add(Connectors.Mask(text));
            if (kind == "Fail") it.ForeColor = Color.DarkRed;
            else if (kind == "Warn") it.ForeColor = Color.DarkGoldenrod;
            else if (kind == "Ok") it.ForeColor = Color.DarkGreen;
            _journal.Items.Add(it);
            if (_journal.Items.Count > 2000) _journal.Items.RemoveAt(0);
            it.EnsureVisible();
        }

        /// <summary>Крестик и «свернуть» — в трей; подсказку «работает в трее» показываем один раз за установку.</summary>
        private void HideToTray()
        {
            Hide();
            var cfg = AgentConfig.Load();
            if (!cfg.TrayHintShown)
            {
                _tray.ShowBalloonTip(8000, "Bilem Agent", Txt.S("UiTrayHint"), ToolTipIcon.Info);
                cfg.TrayHintShown = true;
                cfg.Save();
            }
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void Info(string msg) => MessageBox.Show(this, msg, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        private void Warn(string msg) => MessageBox.Show(this, msg, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

        private static void Open(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }
    }
}
