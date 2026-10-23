using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Bilem.Agent.Core;
using Bilem.Agent.Core.Cloud;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent
{
    /// <summary>
    /// Вкладка MCP, блок 2 «MCP в облаке — Bilem Cloud» (часть L): тумблер «Включить», адрес облака, состояние канала,
    /// таблица баз этого компьютера с «Подключить…» / «Отключить», облачный адрес организации. Тарифа здесь нет — только статусы.
    /// Канал держит служба; окно пишет настройку и подключения, состояние берёт у службы.
    /// </summary>
    internal sealed class CloudPanel : UserControl
    {
        public const string CabinetUrl = "https://app.bilem.ai";

        private readonly ToggleSwitch _on = new ToggleSwitch { Text = Txt.S("UiCloudOn"), Margin = new Padding(0, 2, 0, 4) };
        /// <summary>Правка Славы 21.10: «Платно, по подписке…» — первой строкой, всегда (цен и тарифа в агенте нет).</summary>
        private readonly Label _paid = new Label { Text = Txt.S("UiCloudPaid"), AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
        private readonly Label _about = new Label { Text = Txt.S("UiCloudAbout"), AutoSize = true, Margin = new Padding(0, 2, 0, 4) };
        private readonly LinkLabel _cabinet = new LinkLabel { Text = Txt.S("UiCloudCabinet"), AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
        private readonly Label _noService = new Label { Text = Txt.S("UiCloudNeedService"), AutoSize = true, ForeColor = SystemColors.GrayText, Visible = false };
        private readonly TableLayoutPanel _details = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Top };
        private readonly Label _url = new Label { AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        private readonly Label _state = new Label { AutoSize = true, Margin = new Padding(0, 6, 0, 4) };
        private readonly ListView _bases = new ListView { View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, Height = 130, Width = 520 };
        private readonly Button _connect = new Button { Text = Txt.S("UiCloudConnect"), AutoSize = true };
        private readonly Button _disconnect = new Button { Text = Txt.S("UiCloudDisconnect"), AutoSize = true };
        private readonly TextBox _orgUrl = new TextBox { ReadOnly = true, Width = 360 };
        private readonly FlowLayoutPanel _orgRow;
        private readonly Label _orgNote = new Label { Text = Txt.S("UiCloudOrgNote"), AutoSize = true, ForeColor = SystemColors.GrayText };
        private readonly Func<IWin32Window> _owner;
        private readonly bool _svc;
        private bool _loading;
        private JObject _status;

        /// <summary>Режим сменили здесь — окно обновит блок локального MCP и строку состояния.</summary>
        public event Action ModeChanged;

        public CloudPanel(Func<IWin32Window> owner, bool viaService)
        {
            _owner = owner;
            _svc = viaService;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            var root = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Location = new Point(0, 0), Margin = new Padding(0) };
            root.Controls.Add(_on);
            root.Controls.Add(_paid);
            root.Controls.Add(_noService);
            root.Controls.Add(_about);
            _cabinet.LinkClicked += (s, e) => Open(CabinetUrl);
            root.Controls.Add(_cabinet);

            _details.Controls.Add(Line(Col(Txt.S("UiCloudUrl")), _url));
            _details.Controls.Add(_state);
            _bases.Columns.Add(Txt.S("UiCloudColBase"), LogicalToDeviceUnits(90));
            _bases.Columns.Add(Txt.S("UiCloudColOrg"), LogicalToDeviceUnits(190));
            _bases.Columns.Add(Txt.S("UiCloudColState"), LogicalToDeviceUnits(220));
            _bases.SelectedIndexChanged += (s, e) => Buttons();
            _bases.DoubleClick += (s, e) => { if (_connect.Enabled) ConnectSelected(); };
            _details.Controls.Add(_bases);
            _connect.Click += (s, e) => ConnectSelected();
            _disconnect.Click += (s, e) => DisconnectSelected();
            _details.Controls.Add(Line(_connect, _disconnect));
            var copy = new Button { Text = Txt.S("UiCopy"), AutoSize = true };
            copy.Click += (s, e) => { try { Clipboard.SetText(_orgUrl.Text); } catch { } };
            _orgRow = Line(Col(Txt.S("UiCloudOrgUrl")), _orgUrl, copy);
            _details.Controls.Add(_orgRow);
            _details.Controls.Add(_orgNote);
            root.Controls.Add(_details);
            Controls.Add(root);

            _on.CheckedChanged += (s, e) => { if (!_loading) Toggle(); };
        }

        /// <summary>Ширина блока — от окна: длинные строки переносятся, таблица баз — во всю ширину.</summary>
        public void FitWidth(int w)
        {
            w = Math.Max(LogicalToDeviceUnits(360), w);
            foreach (var l in new[] { _paid, _about, _noService, _orgNote, _state }) l.MaximumSize = new Size(w - LogicalToDeviceUnits(6), 0);
            _bases.Width = w - LogicalToDeviceUnits(6);
            _orgUrl.Width = Math.Min(LogicalToDeviceUnits(420), w - LogicalToDeviceUnits(200));
        }

        private static FlowLayoutPanel Line(params Control[] cs)
        {
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 2, 0, 2) };
            f.Controls.AddRange(cs);
            return f;
        }

        private Label Col(string t) => new Label { Text = t, AutoSize = false, Width = LogicalToDeviceUnits(90), Height = LogicalToDeviceUnits(22), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 2, 4, 0) };
        private static Label Gray(string t) => new Label { Text = t, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(4, 6, 0, 0) };

        private static void Open(string url) { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }

        public bool CloudOn => AgentConfig.Load().Cloud?.Enabled == true;

        // ---------------- режим ----------------

        private void Toggle()
        {
            var on = _on.Checked;
            var ask = Txt.S(on ? "UiCloudOnConfirm" : "UiCloudOffConfirm");
            if (MessageBox.Show(_owner(), ask, "Bilem Cloud", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            {
                _loading = true; _on.Checked = !on; _loading = false;
                return;
            }
            var cfg = AgentConfig.Load();
            cfg.Cloud = cfg.Cloud ?? new CloudSettings();
            cfg.Cloud.Enabled = on;
            cfg.Save();
            Log.Info(Txt.S(on ? "UiCloudOnLog" : "UiCloudOffLog"));
            Reload();
            ModeChanged?.Invoke();
        }

        // ---------------- отображение ----------------

        public void Reload()
        {
            _loading = true;
            var cfg = AgentConfig.Load();
            var on = cfg.Cloud?.Enabled == true;
            _on.Checked = on;
            _on.Enabled = _svc;
            _noService.Visible = !_svc;
            _about.Visible = _cabinet.Visible = !on;
            _details.Visible = on;
            _url.Text = cfg.Cloud.EffectiveUrl;
            _loading = false;
            Fill();
        }

        /// <summary>Состояние службы (каждые 2 с): канал, связь, базы.</summary>
        public void UpdateStatus(JObject svcStatus)
        {
            _status = svcStatus?["cloud"] as JObject;
            if (!_details.Visible) return;
            _state.Text = StateText(_status);
            Fill();
        }

        /// <summary>«Работает · баз в облаке: 1 · связь: 5 с назад» и другие состояния (протокол, статусы).</summary>
        internal static string StateText(JObject st)
        {
            if (st == null) return Txt.S("UiSvcNotRunning");
            var bases = (st["bases"] as JArray ?? new JArray()).OfType<JObject>().ToList();
            var active = bases.Count(b => (string)b["state"] == "active");
            var ago = (DateTime?)st["last_contact"];
            var link = ago.HasValue ? Txt.F("UiCloudAgo", Math.Max(0, (int)(DateTime.Now - ago.Value).TotalSeconds)) : "";
            switch ((string)st["state"])
            {
                case "Working":
                    if (bases.Any(b => (string)b["state"] == "paused")) return Txt.S("UiCloudStatePaused") + link;
                    return Txt.F("UiCloudStateWorking", active) + link;
                case "NoKey": return Txt.S("UiCloudStateNoKey");
                case "NoConnection": return Txt.F("UiCloudStateNoConnection", ago.HasValue ? ago.Value.ToString("dd.MM HH:mm") : "—", (string)st["error"] ?? "");
                case "KeyRevoked": return Txt.S("UiCloudStateKeyRevoked");
                case "Replaced": return Txt.S("UiCloudStateReplaced");
                default: return Txt.S("UiCloudStateConnecting");
            }
        }

        internal static string BaseStateText(string state, string reason)
        {
            switch (state)
            {
                case "active": return Txt.S("UiCloudBaseActive");
                case "paused": return Txt.S("UiCloudBasePaused");
                case "revoked": return Txt.S("UiCloudBaseRevoked");
                default: return Txt.S("UiCloudBaseNone");
            }
        }

        private void Fill()
        {
            var cfg = AgentConfig.Load();
            var store = CloudStore.Load();
            var states = (_status?["bases"] as JArray ?? new JArray()).OfType<JObject>().ToDictionary(b => (string)b["slug"] ?? "", b => b, StringComparer.OrdinalIgnoreCase);
            var sel = _bases.SelectedItems.Count == 1 ? (string)_bases.SelectedItems[0].Tag : null;
            _bases.BeginUpdate();
            _bases.Items.Clear();
            foreach (var b in cfg.Bases)
            {
                var bind = store.Binding(b.Slug);
                string state = bind == null ? null : bind.RevokedInCabinet ? "revoked" : states.TryGetValue(b.Slug, out var s) ? (string)s["state"] : "active";
                var it = new ListViewItem(b.Slug) { Tag = b.Slug };
                // Логин кабинета здесь не показываем (Слава, 23.10): кто входит в кабинет — информация админки, не окна на сервере клиента
                it.SubItems.Add(bind == null ? "—" : bind.OrgName ?? "—");
                it.SubItems.Add(BaseStateText(state, null));
                if (b.Slug == sel) it.Selected = true;
                _bases.Items.Add(it);
            }
            _bases.EndUpdate();
            // Облачный адрес организации — после первой подключённой базы (у разных организаций — свой: показываем выбранной или первой)
            var shown = store.Binding(sel ?? "") ?? store.Bindings.FirstOrDefault(x => !x.RevokedInCabinet);
            var url = shown?.OrgUrl;
            _orgRow.Visible = _orgNote.Visible = !string.IsNullOrEmpty(url);
            if (url != null && _orgUrl.Text != url) _orgUrl.Text = url;
            Buttons();
        }

        private void Buttons()
        {
            var slug = _bases.SelectedItems.Count == 1 ? (string)_bases.SelectedItems[0].Tag : null;
            var bind = slug == null ? null : CloudStore.Load().Binding(slug);
            _connect.Enabled = slug != null && (bind == null || bind.RevokedInCabinet);
            _disconnect.Enabled = bind != null;
            _connect.Text = Txt.S(bind?.RevokedInCabinet == true ? "UiCloudReconnect" : "UiCloudConnect");
        }

        // ---------------- подключить / отключить ----------------

        private void ConnectSelected()
        {
            if (_bases.SelectedItems.Count != 1) return;
            var slug = (string)_bases.SelectedItems[0].Tag;
            using (var f = new CloudConnectForm(slug)) f.ShowDialog(_owner());
            Fill();
        }

        private void DisconnectSelected()
        {
            if (_bases.SelectedItems.Count != 1) return;
            var slug = (string)_bases.SelectedItems[0].Tag;
            if (MessageBox.Show(_owner(), Txt.F("UiCloudDisconnectConfirm", slug), "Bilem Cloud", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            Cursor = Cursors.WaitCursor;
            Task.Run(() => CloudConnect.Disconnect(slug)).ContinueWith(t => BeginInvoke((Action)(() =>
            {
                Cursor = Cursors.Default;
                if (t.Exception != null) MessageBox.Show(_owner(), (t.Exception.InnerException ?? t.Exception).Message, "Bilem Cloud", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Fill();
            })));
        }
    }

    /// <summary>
    /// «Подключить…» базу к облаку (решение 0017, вторая редакция — stand #77): заявка → крупный код подключения (выделяется мышью) + иконка копирования + отсчёт;
    /// код вставляют в кабинете (1С-Базы → Подключить базу), владелец нажимает «Разрешить» на телефоне — окно узнаёт это опросом заявки.
    /// Второго кода и браузера нет. Отклонено / истекло / ошибка — «Подключить…» ещё раз (новая заявка).
    /// </summary>
    internal sealed class CloudConnectForm : BilemForm
    {
        private readonly string _slug;
        // Код — главное в окне: крупно, жирный моноширинный, только чтение, но выделяется мышью и копируется Ctrl+C и без кнопки
        private readonly TextBox _code = new TextBox
        {
            ReadOnly = true, BorderStyle = BorderStyle.None, TabStop = false,
            Font = new Font(CodeFont(), 30f, FontStyle.Bold), Margin = new Padding(0, 0, 8, 0),
        };
        private readonly CopyIcon _copy = new CopyIcon { Enabled = false };
        private readonly FlowLayoutPanel _row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0), WrapContents = false, Visible = false };
        private readonly ToolTip _tips = new ToolTip();
        private readonly Label _left = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 2, 0, 0) };
        private readonly Label _msg = new Label { AutoSize = true, Margin = new Padding(0, 8, 0, 0), MaximumSize = new Size(460, 0) };
        private readonly Button _again = new Button { Text = Txt.S("UiCloudConnect"), AutoSize = true, Visible = false, Margin = new Padding(0, 10, 6, 0) };
        private readonly System.Windows.Forms.Timer _tick = new System.Windows.Forms.Timer { Interval = 1000 };
        private bool _waitingOwner;
        private ConnectRequest _req;
        private DateTime? _until;
        private CancellationTokenSource _cts;

        public CloudConnectForm(string slug)
        {
            _slug = slug;
            Text = Txt.F("UiCloudConnectTitle", slug);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            var f = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(14), WrapContents = false };
            f.Controls.Add(new Label { Text = Txt.F("UiCloudConnectBase", slug, Environment.MachineName), AutoSize = true });
            f.Controls.Add(new Label { Text = Txt.S("UiCloudConnCode"), AutoSize = true, Margin = new Padding(0, 10, 0, 2) });
            var row = _row;
            _code.BackColor = BackColor;
            _code.Width = TextRenderer.MeasureText("WWWW-WWWW", _code.Font).Width + 4;
            row.Controls.Add(_code);
            _copy.Margin = new Padding(0, Math.Max(0, (_code.PreferredHeight - _copy.Height) / 2), 0, 0);
            _tips.SetToolTip(_copy, Txt.S("UiCopy"));
            _copy.Click += (s, e) => CopyCode();
            row.Controls.Add(_copy);
            f.Controls.Add(row);
            f.Controls.Add(_left);
            f.Controls.Add(_msg);
            var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
            _again.Click += (s, e) => Begin();
            buttons.Controls.Add(_again);
            var cancel = new Button { Text = Txt.S("UiCancel"), AutoSize = true, DialogResult = DialogResult.Cancel, Margin = new Padding(0, 10, 0, 0) };
            buttons.Controls.Add(cancel);
            f.Controls.Add(buttons);
            CancelButton = cancel;
            Controls.Add(f);
            _tick.Tick += (s, e) => Countdown();
            Shown += (s, e) => Begin();
            FormClosed += (s, e) => { _cts?.Cancel(); _tick.Dispose(); _tips.Dispose(); };
        }

        /// <summary>Код есть — крупно с иконкой копирования; нет (ждём облако, ошибка) — строки нет совсем, без прочерка.</summary>
        private void ShowCode(string code)
        {
            _code.Text = code ?? "";
            _copy.Enabled = code != null;
            _row.Visible = code != null;
        }

        /// <summary>Consolas есть в любой Windows начиная с Vista; нет — системный моноширинный.</summary>
        private static FontFamily CodeFont()
        {
            try { return new FontFamily("Consolas"); } catch { return FontFamily.GenericMonospace; }
        }

        /// <summary>Ошибка словами: нет связи — «облако не отвечает» (подробности — в журнале), 404 / 5xx — тексты CloudApi.Message.</summary>
        private static string Why(Exception e)
        {
            e = (e as AggregateException)?.InnerException ?? e;
            if (e is CloudException ce && ce.Code == "no_connection") { Log.Warn(ce.Message); return Txt.S("CloudNoAnswer"); }
            return e.Message;
        }

        /// <summary>Срок — в местном времени (облако присылает UTC с «Z»).</summary>
        private static DateTime Local(DateTime t) => t.Kind == DateTimeKind.Utc ? t.ToLocalTime() : t;

        private void Countdown()
        {
            if (_until == null) { _left.Text = ""; return; }
            var left = Local(_until.Value) - DateTime.Now;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            _left.Text = Txt.F("UiCloudCodeLeft", $"{(int)left.TotalMinutes}:{left.Seconds:00}");
        }

        private void Begin()
        {
            _cts?.Cancel();
            _waitingOwner = false;
            _req = null;
            _until = null;
            ShowCode(null);
            _again.Visible = false;
            _left.Text = Txt.S("UiCloudRequesting");
            _msg.Text = "";
            UseWaitCursor = true;
            Task.Run(() => CloudConnect.Begin(_slug)).ContinueWith(t => BeginInvoke((Action)(() =>
            {
                UseWaitCursor = false;
                if (t.Exception != null) { End(Why(t.Exception)); return; }
                _req = t.Result;
                ShowCode(_req.Code);
                _until = _req.ExpiresAt;
                _msg.Text = Txt.S("UiCloudPending");
                Countdown();
                _tick.Start();
                _code.Focus();
                _code.SelectAll();
                Wait(_req);
            })));
        }

        private void Wait(ConnectRequest req)
        {
            var cts = _cts = new CancellationTokenSource();
            Task.Run(() => CloudConnect.Wait(_slug, req, st => BeginInvoke((Action)(() => Changed(st))), cts.Token)).ContinueWith(t =>
            {
                if (cts.IsCancellationRequested || IsDisposed) return;
                BeginInvoke((Action)(() =>
                {
                    if (t.Exception != null) { End(Why(t.Exception)); return; }
                    _tick.Stop();
                    MessageBox.Show(this, Txt.F("UiCloudConnectedMsg", _slug, t.Result.OrgName ?? "—"), "Bilem Cloud", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    DialogResult = DialogResult.OK;
                    Close();
                }));
            });
        }

        /// <summary>Код вставили в кабинете — ждём «Разрешить» владельца (свои 10 минут).</summary>
        private void Changed(ConnectStatus st)
        {
            if (IsDisposed || st.Status != "waiting_owner") return;
            _waitingOwner = true;
            _msg.Text = Txt.F("UiCloudWaitingOwner", st.OrgName ?? st.OrgId ?? "—");
            if (st.ExpiresAt != null) _until = st.ExpiresAt;
            Countdown();
        }

        /// <summary>Заявка закончилась без подключения: код больше не годится — «Подключить…» ещё раз.</summary>
        private void End(string msg)
        {
            _tick.Stop();
            _req = null;
            _until = null;
            _left.Text = "";
            ShowCode(null);
            _msg.Text = msg;
            _again.Visible = true;
            _again.Focus();
        }

        private void CopyCode()
        {
            if (_req == null) return;
            try { Clipboard.SetText(_req.Code); _copy.Done(); if (!_waitingOwner) _msg.Text = Txt.S("UiCloudCopied") + " " + Txt.S("UiCloudPending"); } catch { }
        }
    }

    /// <summary>Квадратная кнопка-иконка «копировать» 24×24, как у сниппетов кода: два листка; после клика на секунду — галочка. Рисуется сама, без шрифтов иконок.</summary>
    internal sealed class CopyIcon : Button
    {
        private readonly System.Windows.Forms.Timer _back = new System.Windows.Forms.Timer { Interval = 1000 };
        private bool _done;

        public CopyIcon()
        {
            Size = new Size(24, 24);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            AccessibleName = Txt.S("UiCopy");
            _back.Tick += (s, e) => { _back.Stop(); _done = false; Invalidate(); };
        }

        public void Done() { _done = true; Invalidate(); _back.Stop(); _back.Start(); }

        protected override void OnEnabledChanged(EventArgs e) { _back.Stop(); _done = false; base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var c = !Enabled ? SystemColors.GrayText : _done ? Color.FromArgb(0, 128, 0) : SystemColors.ControlText;
            using (var pen = new Pen(c, 1.6f))
            {
                if (_done) { g.DrawLines(pen, new[] { new PointF(6, 12.5f), new PointF(10, 16.5f), new PointF(18, 7.5f) }); return; }
                g.DrawRectangle(pen, 9, 9, 9, 10);                                                           // передний листок
                g.DrawLines(pen, new[] { new PointF(6, 15), new PointF(6, 5.5f), new PointF(14, 5.5f) });    // задний — уголком
            }
        }

        protected override void Dispose(bool disposing) { if (disposing) _back.Dispose(); base.Dispose(disposing); }
    }
}
