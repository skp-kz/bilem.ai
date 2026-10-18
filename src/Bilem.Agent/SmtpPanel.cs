using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Bilem.Agent.Core;
using Bilem.Agent.Core.Mail;

namespace Bilem.Agent
{
    /// <summary>Тумблер вместо галочки: зелёная «таблетка» — включено, серая — выключено.</summary>
    internal sealed class ToggleSwitch : CheckBox
    {
        public ToggleSwitch()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AutoSize = false;
            Cursor = Cursors.Hand;
        }

        public override Size GetPreferredSize(Size proposed)
        {
            var t = TextRenderer.MeasureText(Text, Font);
            return new Size(LogicalToDeviceUnits(46) + t.Width + LogicalToDeviceUnits(6), Math.Max(t.Height, LogicalToDeviceUnits(24)) + 2);
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Size = GetPreferredSize(Size.Empty); }
        protected override void OnCreateControl() { base.OnCreateControl(); Size = GetPreferredSize(Size.Empty); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? SystemColors.Control);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int w = LogicalToDeviceUnits(40), h = LogicalToDeviceUnits(20), y = (Height - h) / 2;
            var on = Checked;
            using (var path = new GraphicsPath())
            {
                path.AddArc(0, y, h, h, 90, 180);
                path.AddArc(w - h, y, h, h, 270, 180);
                path.CloseFigure();
                using (var b = new SolidBrush(on ? Color.FromArgb(31, 157, 85) : Color.FromArgb(190, 190, 190))) g.FillPath(b, path);
            }
            int d = h - LogicalToDeviceUnits(4), x = on ? w - d - LogicalToDeviceUnits(2) : LogicalToDeviceUnits(2);
            g.FillEllipse(Brushes.White, x, y + LogicalToDeviceUnits(2), d, d);
            TextRenderer.DrawText(g, Text, Font, new Point(w + LogicalToDeviceUnits(8), (Height - TextRenderer.MeasureText(Text, Font).Height) / 2), Enabled ? ForeColor : SystemColors.GrayText);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, new Rectangle(w + LogicalToDeviceUnits(6), 0, Width - w - LogicalToDeviceUnits(6), Height));
        }
    }

    /// <summary>
    /// «SMTP» — отправка документов агента (только отправка). Один почтовый ящик на все базы: сервер, логин и пароль приложения
    /// вводятся один раз (DPAPI). У базы — своё имя отправителя, шаблон письма и подпись. Тумблер «SMTP-клиент включён»:
    /// выключен — всё ниже серое и не редактируется (и в меню трея).
    /// </summary>
    internal sealed class SmtpPanel : UserControl
    {
        private readonly ToggleSwitch _on = new ToggleSwitch { Text = Txt.S("UiSmtpOn"), Margin = new Padding(0, 2, 0, 4) };
        // Почтовый ящик (общий)
        private readonly ComboBox _preset = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
        private readonly Label _hint = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 2, 0, 4) };
        private readonly TextBox _host = new TextBox { Width = 190 };
        private readonly NumericUpDown _port = new NumericUpDown { Minimum = 1, Maximum = 65535, Width = 70 };
        private readonly ComboBox _sec = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
        private readonly TextBox _login = new TextBox { Width = 220 };
        private readonly TextBox _pwd = new TextBox { Width = 170, UseSystemPasswordChar = true };
        private readonly Label _pwdState = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(4, 6, 0, 0) };
        private readonly TextBox _from = new TextBox { Width = 220 };
        private readonly TextBox _testTo = new TextBox { Width = 220 };
        // Кому можно отправлять
        private readonly TextBox _trusted = new TextBox { Multiline = true, Height = 40, Width = 440 };
        private readonly TextBox _bcc = new TextBox { Width = 220 };
        private readonly NumericUpDown _hour = new NumericUpDown { Minimum = 0, Maximum = 1000, Width = 60 };
        private readonly NumericUpDown _day = new NumericUpDown { Minimum = 0, Maximum = 10000, Width = 60 };
        // Письмо от базы
        private readonly ComboBox _base = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
        private readonly TextBox _fromName = new TextBox { Width = 260 };
        private readonly ComboBox _lang = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
        private readonly TextBox _subject = new TextBox { Width = 440 };
        private readonly TextBox _body = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Width = 440, Height = 76 };
        private readonly TextBox _sign = new TextBox { Multiline = true, Width = 440, Height = 40 };

        private readonly List<Control> _gated = new List<Control>();
        private readonly List<GroupBox> _groups = new List<GroupBox>();
        private readonly Func<IWin32Window> _owner;
        private bool _loading;
        private MailSettings _cur = new MailSettings();
        private string _curSlug;

        public SmtpPanel(Func<IWin32Window> owner)
        {
            _owner = owner;
            AutoScroll = true;
            var root = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Location = new Point(0, 0), Padding = new Padding(10, 8, 10, 8) };

            root.Controls.Add(_on);
            root.Controls.Add(Gray(Txt.S("UiSmtpAbout")));

            // --- Почтовый ящик (общий для всех баз)
            var box = Group(Txt.S("UiSmtpBox"));
            var g1 = Table(box);
            _preset.Items.Add(Txt.S("UiSmtpPresetChoose"));
            foreach (var p in MailPresets.All) _preset.Items.Add(p.Name);
            _preset.SelectedIndex = 0;
            Add(g1, Line(Col(Txt.S("UiSmtpPreset")), _preset));
            _hint.Visible = false;
            Add(g1, _hint);
            _sec.Items.AddRange(new object[] { "SSL/TLS", "STARTTLS", Txt.S("UiSmtpNoTls") });
            Add(g1, Line(Col(Txt.S("UiSmtpHost")), _host, Lbl(Txt.S("UiSmtpPort")), _port, _sec));
            Add(g1, Line(Col(Txt.S("UiSmtpLogin")), _login));
            Add(g1, Line(Col(Txt.S("UiSmtpPassword")), _pwd, _pwdState));
            Add(g1, Line(Col(Txt.S("UiSmtpFrom")), _from));
            var test = new Button { Text = Txt.S("UiSmtpTest"), AutoSize = true };
            test.Click += async (s, e) => await SendTest(test);
            Add(g1, Line(Col(Txt.S("UiSmtpTestTo")), _testTo, test));
            root.Controls.Add(box);

            // --- Кому можно отправлять
            var who = Group(Txt.S("UiSmtpWho"));
            var g2 = Table(who);
            Add(g2, Lbl(Txt.S("UiSmtpTrusted")));
            Add(g2, _trusted);
            Add(g2, Gray(Txt.S("UiSmtpTrustedNote")));
            Add(g2, Line(Col(Txt.S("UiSmtpBccTo")), _bcc, Lbl(Txt.S("UiSmtpBccNote"), gray: true)));
            Add(g2, Line(Col(Txt.S("UiSmtpLimits")), _hour, Lbl(Txt.S("UiSmtpPerHour")), _day, Lbl(Txt.S("UiSmtpPerDay"))));
            root.Controls.Add(who);

            // --- Письмо от базы
            var letter = Group(Txt.S("UiSmtpLetter"));
            var g3 = Table(letter);
            Add(g3, Line(Col(Txt.S("UiAccessBase")), _base));
            Add(g3, Line(Col(Txt.S("UiSmtpFromName")), _fromName));
            _lang.Items.AddRange(new object[] { "Русский", "Қазақша" });
            Add(g3, Line(Col(Txt.S("UiSmtpTemplate")), _lang));
            Add(g3, Lbl(Txt.S("UiSmtpSubject"))); Add(g3, _subject);
            Add(g3, Lbl(Txt.S("UiSmtpBody"))); Add(g3, _body);
            Add(g3, Gray(Txt.S("UiSmtpVars")));
            Add(g3, Lbl(Txt.S("UiSmtpSignature"))); Add(g3, _sign);
            root.Controls.Add(letter);

            // Облако — серым, на своём месте
            var cloud = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(3, 6, 0, 0) };
            cloud.Controls.Add(new Label { Text = Txt.S("UiSmtpCloud"), AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 3, 0, 0) });
            cloud.Controls.Add(CloudBadge.Create(this));
            root.Controls.Add(cloud);

            var save = new Button { Text = Txt.S("UiPrintSave"), AutoSize = true, Margin = new Padding(3, 8, 0, 0) };
            save.Click += (s, e) => Save(true);
            root.Controls.Add(save);
            Controls.Add(root);
            _gated.AddRange(new Control[] { box, who, letter });

            // Узкое окно: группы и поля — по ширине вкладки, строки переносятся
            Resize += (s, e) =>
            {
                var w = Math.Max(LogicalToDeviceUnits(320), ClientSize.Width - LogicalToDeviceUnits(30) - SystemInformation.VerticalScrollBarWidth);
                foreach (var gb in _groups) { gb.MinimumSize = new Size(w, 0); gb.MaximumSize = new Size(w, 0); }
                foreach (var tb in new Control[] { _trusted, _subject, _body, _sign }) tb.Width = Math.Min(LogicalToDeviceUnits(560), w - LogicalToDeviceUnits(30));
                foreach (var l in _wrapped) l.MaximumSize = new Size(w - LogicalToDeviceUnits(30), 0);
            };

            _on.CheckedChanged += (s, e) =>
            {
                Gate();
                if (_loading) return;
                var c = AgentConfig.Load();
                c.Smtp = c.Smtp ?? new SmtpAccount();
                c.Smtp.Enabled = _on.Checked;
                c.Save();
                Log.Info(Txt.S(_on.Checked ? "UiSmtpOnLog" : "UiSmtpOffLog"));
                EnabledChanged2?.Invoke();
            };
            _base.SelectedIndexChanged += (s, e) => { if (_loading) return; SaveLetter(); ShowLetter(); };
            _preset.SelectedIndexChanged += (s, e) => ApplyPreset();
            _lang.SelectedIndexChanged += (s, e) => { if (_loading) return; KeepTemplate(1 - _lang.SelectedIndex); ShowTemplate(); };
        }

        /// <summary>Тумблер переключили здесь — меню трея обновит галочку.</summary>
        public event Action EnabledChanged2;

        private readonly List<Label> _wrapped = new List<Label>();

        private GroupBox Group(string title)
        {
            var g = new GroupBox { Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 6, 10, 8), Margin = new Padding(3, 8, 3, 3) };
            _groups.Add(g);
            return g;
        }

        private static TableLayoutPanel Table(GroupBox g)
        {
            var t = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
            g.Controls.Add(t);
            return t;
        }

        private static void Add(TableLayoutPanel t, Control c) => t.Controls.Add(c, 0, t.Controls.Count);

        private static FlowLayoutPanel Line(params Control[] cs)
        {
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 2, 0, 2) };
            f.Controls.AddRange(cs);
            return f;
        }

        private static Label Lbl(string t, bool gray = false) => new Label { Text = t, AutoSize = true, Margin = new Padding(0, 6, 4, 0), ForeColor = gray ? SystemColors.GrayText : SystemColors.ControlText };

        /// <summary>Подпись поля в колонке одной ширины — поля выстраиваются ровно.</summary>
        private Label Col(string t) => new Label { Text = t, AutoSize = false, Width = LogicalToDeviceUnits(150), Height = LogicalToDeviceUnits(22), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 2, 4, 0) };

        private Label Gray(string t)
        {
            var l = new Label { Text = t, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(LogicalToDeviceUnits(540), 0), Margin = new Padding(0, 3, 0, 4) };
            _wrapped.Add(l);
            return l;
        }

        /// <summary>Тумблер выключен — всё ниже серое и не редактируется.</summary>
        private void Gate() { foreach (var c in _gated) c.Enabled = _on.Checked; }

        public void Reload()
        {
            _loading = true;
            var cfg = AgentConfig.Load();
            var acc = cfg.Smtp ?? new SmtpAccount();
            _on.Checked = acc.Enabled;
            _host.Text = acc.Host ?? ""; _port.Value = Math.Max(1, Math.Min(65535, acc.Port));
            _sec.SelectedIndex = (int)acc.Security;
            _login.Text = acc.Login ?? ""; _pwd.Text = "";
            _pwdState.Text = Txt.S(string.IsNullOrEmpty(acc.PasswordProtected) ? "UiSmtpPwdNone" : "UiSmtpPwdSaved");
            _from.Text = acc.From ?? "";
            _testTo.Text = acc.TestTo ?? "";
            _trusted.Text = string.Join(", ", acc.Trusted ?? new List<string>());
            _bcc.Text = acc.Bcc ?? "";
            _hour.Value = Math.Max(0, Math.Min(1000, acc.LimitHour)); _day.Value = Math.Max(0, Math.Min(10000, acc.LimitDay));
            var cur = _base.SelectedItem as string ?? _curSlug;
            _base.Items.Clear();
            foreach (var b in cfg.Bases) _base.Items.Add(b.Slug);
            if (_base.Items.Count > 0) _base.SelectedItem = cur != null && _base.Items.Contains(cur) ? cur : _base.Items[0];
            _loading = false;
            ShowLetter();
            Gate();
        }

        public void RefreshEnabled() { _loading = true; _on.Checked = AgentConfig.Load().Smtp?.Enabled == true; _loading = false; Gate(); }

        private void ShowLetter()
        {
            _loading = true;
            _curSlug = _base.SelectedItem as string;
            var b = AgentConfig.Load().Find(_curSlug ?? "");
            _cur = b?.Mail ?? new MailSettings();
            _fromName.Text = _cur.FromName ?? "";
            _lang.SelectedIndex = string.Equals(_cur.Language, "kk", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            ShowTemplate();
            _sign.Text = _cur.Signature ?? "";
            _loading = false;
        }

        private void ShowTemplate()
        {
            var kk = _lang.SelectedIndex == 1;
            _subject.Text = kk ? _cur.SubjectKk : _cur.SubjectRu;
            _body.Text = ((kk ? _cur.BodyKk : _cur.BodyRu) ?? "").Replace("\n", Environment.NewLine);
        }

        /// <summary>Запомнить правки шаблона текущего языка перед переключением языка.</summary>
        private void KeepTemplate(int langIndex)
        {
            var body = _body.Text.Replace("\r\n", "\n");
            if (langIndex == 1) { _cur.SubjectKk = _subject.Text; _cur.BodyKk = body; } else { _cur.SubjectRu = _subject.Text; _cur.BodyRu = body; }
        }

        private void ApplyPreset()
        {
            if (_preset.SelectedIndex <= 0) { _hint.Text = ""; _hint.Visible = false; return; }
            var p = MailPresets.All[_preset.SelectedIndex - 1];
            _host.Text = p.Host; _port.Value = p.Port; _sec.SelectedIndex = (int)p.Security;
            _hint.Text = Txt.S(p.HintKey);
            _hint.Visible = true;
            if (!_wrapped.Contains(_hint)) _wrapped.Add(_hint);
            _hint.MaximumSize = new Size(Math.Max(LogicalToDeviceUnits(300), ClientSize.Width - LogicalToDeviceUnits(80)), 0);
        }

        /// <summary>Письмо выбранной базы — в конфиг (до переключения базы и при «Сохранить»).</summary>
        private void SaveLetter()
        {
            if (string.IsNullOrEmpty(_curSlug)) return;
            var cfg = AgentConfig.Load();
            var b = cfg.Find(_curSlug);
            if (b == null) return;
            KeepTemplate(_lang.SelectedIndex);
            _cur.FromName = _fromName.Text.Trim();
            _cur.Language = _lang.SelectedIndex == 1 ? "kk" : "ru";
            _cur.Signature = _sign.Text.Trim();
            b.Mail = _cur;
            cfg.Upsert(b);
            cfg.Save();
        }

        private void Save(bool tell)
        {
            var cfg = AgentConfig.Load();
            var a = cfg.Smtp ?? new SmtpAccount();
            a.Enabled = _on.Checked;
            a.Host = _host.Text.Trim(); a.Port = (int)_port.Value; a.Security = (SmtpSecurity)Math.Max(0, _sec.SelectedIndex);
            a.Login = _login.Text.Trim();
            // Пароль — только если ввели новый; хранится зашифрованным для этой машины, поле сразу очищается
            if (_pwd.Text.Length > 0) { a.SetPassword(_pwd.Text); _pwd.Text = ""; }
            a.From = _from.Text.Trim();
            a.TestTo = _testTo.Text.Trim();
            a.Trusted = SmtpAccount.ParseList(_trusted.Text);
            var bcc = SmtpAccount.ParseList(_bcc.Text);
            a.Bcc = bcc.FirstOrDefault();
            a.LimitHour = (int)_hour.Value; a.LimitDay = (int)_day.Value;
            cfg.Smtp = a;
            cfg.Save();
            SaveLetter();
            _trusted.Text = string.Join(", ", a.Trusted);
            _bcc.Text = a.Bcc ?? "";
            _pwdState.Text = Txt.S(string.IsNullOrEmpty(a.PasswordProtected) ? "UiSmtpPwdNone" : "UiSmtpPwdSaved");
            if (tell) Log.Ok(Txt.S("UiSmtpSaved"));
        }

        private async Task SendTest(Button btn)
        {
            Save(false);
            var cfg = AgentConfig.Load();
            if (cfg.Smtp == null || !cfg.Smtp.Configured) { MessageBox.Show(_owner(), Txt.S("MailNotConfigured"), "Bilem Agent", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            var to = _testTo.Text.Trim();
            var name = cfg.Find(_curSlug ?? "")?.Mail?.FromName;
            btn.Enabled = false;
            try
            {
                await Task.Run(() => MailService.Test(cfg.Smtp, to, name));
                MessageBox.Show(_owner(), Txt.F("MailTestOk", to), "Bilem Agent", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception e)
            {
                Log.Fail(e.Message);
                MessageBox.Show(_owner(), e.Message, "Bilem Agent", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { btn.Enabled = true; }
        }
    }
}
