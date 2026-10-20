using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Bilem.Agent.Core;
using Bilem.Agent.Core.Cloud;
using Newtonsoft.Json.Linq;

namespace Bilem.Agent
{
    /// <summary>
    /// Вкладка MCP, блок 2 «MCP в облаке — Bilem Cloud» (часть L): тумблер «Включить», адрес облака, логин, состояние канала,
    /// таблица баз этого компьютера с «Подключить…» / «Отключить», облачный адрес организации. Тарифа здесь нет — только статусы.
    /// Канал держит служба; окно пишет настройку и подключения, состояние берёт у службы.
    /// </summary>
    internal sealed class CloudPanel : UserControl
    {
        public const string CabinetUrl = "https://app.bilem.ai";

        private readonly ToggleSwitch _on = new ToggleSwitch { Text = Txt.S("UiCloudOn"), Margin = new Padding(0, 2, 0, 4) };
        private readonly Label _about = new Label { Text = Txt.S("UiCloudAbout"), AutoSize = true, Margin = new Padding(0, 2, 0, 4) };
        private readonly LinkLabel _cabinet = new LinkLabel { Text = Txt.S("UiCloudCabinet"), AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
        private readonly Label _noService = new Label { Text = Txt.S("UiCloudNeedService"), AutoSize = true, ForeColor = SystemColors.GrayText, Visible = false };
        private readonly TableLayoutPanel _details = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Top };
        private readonly Label _url = new Label { AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        private readonly TextBox _login = new TextBox { Width = 240 };
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
            root.Controls.Add(_noService);
            root.Controls.Add(_about);
            _cabinet.LinkClicked += (s, e) => Open(CabinetUrl);
            root.Controls.Add(_cabinet);

            _details.Controls.Add(Line(Col(Txt.S("UiCloudUrl")), _url));
            _login.Leave += (s, e) => SaveLogin();
            _details.Controls.Add(Line(Col(Txt.S("UiCloudLogin")), _login, Gray(Txt.S("UiCloudLoginNote"))));
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
            foreach (var l in new[] { _about, _noService, _orgNote, _state }) l.MaximumSize = new Size(w - LogicalToDeviceUnits(6), 0);
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

        private void SaveLogin()
        {
            if (_loading) return;
            var cfg = AgentConfig.Load();
            var v = _login.Text.Trim();
            if ((cfg.Cloud.LoginHint ?? "") == v) return;
            cfg.Cloud.LoginHint = v;
            cfg.Save();
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
            _url.Text = cfg.Cloud.EffectiveUrl + "   " + Txt.S("UiCloudUrlNote");
            if (!_login.Focused) _login.Text = cfg.Cloud.LoginHint ?? "";
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
                it.SubItems.Add(bind == null ? "—" : (bind.OrgName ?? "—") + (string.IsNullOrEmpty(bind.Login) ? "" : " (" + bind.Login + ")"));
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
            SaveLogin();
            var slug = (string)_bases.SelectedItems[0].Tag;
            using (var f = new CloudConnectForm(slug, AgentConfig.Load().Cloud.LoginHint)) f.ShowDialog(_owner());
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
    /// «Подключить…» базу к облаку (часть L): заявка → кабинет в браузере → код из кабинета → привязка. Код — 8 символов, 10 минут, один раз.
    /// </summary>
    internal sealed class CloudConnectForm : BilemForm
    {
        private readonly string _slug;
        private readonly TextBox _code = new TextBox { Width = 140, CharacterCasing = CharacterCasing.Upper, MaxLength = 12 };
        private readonly Label _msg = new Label { AutoSize = true, Margin = new Padding(0, 8, 0, 0), MaximumSize = new Size(460, 0) };
        private readonly Button _ok = new Button { Text = Txt.S("UiCloudConnectGo"), AutoSize = true, Enabled = false };
        private readonly Button _reopen = new Button { Text = Txt.S("UiCloudReopen"), AutoSize = true, Enabled = false };
        private ConnectRequest _req;

        public CloudConnectForm(string slug, string login)
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
            if (!string.IsNullOrEmpty(login)) f.Controls.Add(new Label { Text = Txt.F("UiCloudConnectLogin", login), AutoSize = true, Margin = new Padding(0, 4, 0, 0) });
            var step1 = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
            step1.Controls.Add(new Label { Text = Txt.S("UiCloudStep1"), AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
            _reopen.Click += (s, e) => Reopen();
            step1.Controls.Add(_reopen);
            f.Controls.Add(step1);
            var step2 = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
            step2.Controls.Add(new Label { Text = Txt.S("UiCloudStep2"), AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
            step2.Controls.Add(_code);
            _ok.Click += (s, e) => Finish();
            step2.Controls.Add(_ok);
            f.Controls.Add(step2);
            f.Controls.Add(_msg);
            var cancel = new Button { Text = Txt.S("UiCancel"), AutoSize = true, DialogResult = DialogResult.Cancel, Margin = new Padding(0, 10, 0, 0) };
            f.Controls.Add(cancel);
            CancelButton = cancel;
            AcceptButton = _ok;
            Controls.Add(f);
            _code.TextChanged += (s, e) => _ok.Enabled = _req != null && CloudApi.NormalizeCode(_code.Text).Length == 8;
            Shown += (s, e) => Begin();
        }

        private void Busy(bool on) { UseWaitCursor = on; _ok.Enabled = !on && _req != null && CloudApi.NormalizeCode(_code.Text).Length == 8; _reopen.Enabled = !on && _req != null; }

        private void Begin()
        {
            _msg.Text = Txt.S("UiCloudRequesting");
            Busy(true);
            Task.Run(() => CloudConnect.Begin(_slug)).ContinueWith(t => BeginInvoke((Action)(() =>
            {
                Busy(false);
                if (t.Exception != null) { _msg.Text = (t.Exception.InnerException ?? t.Exception).Message; _reopen.Enabled = true; _req = null; return; }
                _req = t.Result;
                _msg.Text = Txt.F("UiCloudOpened", _req.ExpiresAt.ToString("HH:mm"));
                Open(_req.ConnectUrl);
                _code.Focus();
                Busy(false);
            })));
        }

        /// <summary>«Открыть ещё раз»: кабинет заново; заявка истекла — новая.</summary>
        private void Reopen()
        {
            if (_req == null || DateTime.Now > _req.ExpiresAt) { Begin(); return; }
            Open(_req.ConnectUrl);
        }

        private static void Open(string url) { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }

        private void Finish()
        {
            if (_req == null) return;
            var code = _code.Text;
            Busy(true);
            _msg.Text = Txt.S("UiCloudExchanging");
            Task.Run(() => CloudConnect.Finish(_slug, _req, code)).ContinueWith(t => BeginInvoke((Action)(() =>
            {
                Busy(false);
                if (t.Exception != null)
                {
                    var e = t.Exception.InnerException ?? t.Exception;
                    _msg.Text = e.Message;
                    if (e is CloudException ce && (ce.Code == "code_expired" || ce.Code == "too_many_attempts" || ce.Code == "code_used")) { _req = null; _reopen.Enabled = true; }
                    return;
                }
                MessageBox.Show(this, Txt.F("UiCloudConnectedMsg", _slug, t.Result.OrgName ?? "—"), "Bilem Cloud", MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.OK;
                Close();
            })));
        }
    }
}
