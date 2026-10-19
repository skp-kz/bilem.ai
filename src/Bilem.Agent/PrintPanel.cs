using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Bilem.Agent.Core;
using Bilem.Agent.Core.Mcp;
using Bilem.Agent.Core.Print;

namespace Bilem.Agent
{
    /// <summary>
    /// «Печатные формы» (переделка Славы 18.10, вечер — «список слева», как «Параметры» Windows 11): сверху — база и «Скопировать из другой базы…»;
    /// слева — Общее · Счёт на оплату · Доверенность · Акт сверки с точкой статуса; справа — белая карточка настроек выбранной формы;
    /// внизу — «Сохранить» и «Пробный документ…».
    /// Картинки — в C:\ProgramData\Bilem\bases\&lt;имя&gt;\print (только Администраторы и SYSTEM).
    /// </summary>
    internal sealed class PrintPanel : UserControl
    {
        private readonly ComboBox _base = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        private readonly Label _warn = new Label { AutoSize = true, BackColor = Color.FromArgb(255, 243, 205), ForeColor = Color.FromArgb(120, 80, 0), Padding = new Padding(6, 3, 6, 3), Margin = new Padding(0, 4, 0, 2), Visible = false };
        private readonly Dictionary<PrintForm, Label> _status = new Dictionary<PrintForm, Label>();
        private readonly Dictionary<PrintForm, ReadyLevel> _levels = new Dictionary<PrintForm, ReadyLevel>();

        // Общее
        private readonly Dictionary<PrintImage, PictureBox> _pics = new Dictionary<PrintImage, PictureBox>();
        private readonly TextBox _address = Tb(380);
        /// <summary>Папка документов — общая для всех баз (служба пишет PDF сюда; часть I).</summary>
        private readonly TextBox _docsRoot = Tb(380);
        private readonly TextBox _director = Tb(220);
        private readonly TextBox _directorTitle = Tb(160);
        private readonly TextBox _chief = Tb(220);
        // Счёт
        private readonly Parts _invParts = new Parts();
        private readonly TextBox _issuedBy = Tb(260);
        private readonly TextBox _kbe = Tb(40);
        private readonly TextBox _knp = Tb(50);
        private readonly CheckBox _paySample = Check("UiPrintPaySample");
        private readonly TextBox _header = Multi();
        private readonly TextBox _footer = Multi();
        // Доверенность
        private readonly Parts _poaParts = new Parts();
        private readonly NumericUpDown _poaDays = new NumericUpDown { Minimum = 1, Maximum = 365, Width = 60 };
        private readonly CheckBox _poaId = Check("UiPrintPoaIdentity");
        private readonly Label _poaSigners = GrayLabel();
        // Акт сверки
        private readonly Parts _actParts = new Parts();
        private readonly Label _actSigner = new Label { AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        private readonly ComboBox _actPeriod = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
        private readonly CheckBox _actMirror = Check("UiPrintActMirror");
        private readonly CheckBox _actByContracts = Check("UiPrintActByContracts");
        private readonly TextBox _actIntro = Multi();

        private readonly Func<IWin32Window> _owner;
        private bool _loading;

        private sealed class Parts
        {
            public readonly CheckBox Logo = Check("UiPrintPartLogo"), Stamp = Check("UiPrintPartStamp"), Sign = Check("UiPrintPartSign");
            public Control Row()
            {
                var f = Flow();
                f.Controls.Add(Logo); f.Controls.Add(Stamp); f.Controls.Add(Sign);
                return f;
            }
            public void Show(FormParts p) { p = p ?? new FormParts(); Logo.Checked = p.Logo; Stamp.Checked = p.Stamp; Sign.Checked = p.Sign; }
            public FormParts Value() => new FormParts { Logo = Logo.Checked, Stamp = Stamp.Checked, Sign = Sign.Checked };
            public IEnumerable<CheckBox> All => new[] { Logo, Stamp, Sign };
        }

        public PrintPanel(Func<IWin32Window> owner)
        {
            _owner = owner;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(10, 8, 10, 8) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var top = Flow();
            top.Controls.Add(new Label { Text = Txt.S("UiAccessBase"), AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
            top.Controls.Add(_base);
            var copy = new LinkLabel { Text = Txt.S("UiPrintCopyFrom"), AutoSize = true, Margin = new Padding(10, 6, 0, 0) };
            copy.LinkClicked += (s, e) => CopyFrom();
            top.Controls.Add(copy);
            // Строки — явно: скрытая жёлтая метка иначе сдвигает список форм в строку «по размеру»
            root.Controls.Add(top, 0, 0);
            root.Controls.Add(_warn, 0, 1);

            // Слева — список форм (как «Параметры» Windows 11), справа — белая карточка настроек выбранной формы
            _nav.Add(PrintForm.General, Txt.S("UiPrintTabGeneral"), separatorAfter: true);
            _nav.Add(PrintForm.Invoice, Txt.S("UiPrintTabInvoice"));
            _nav.Add(PrintForm.PowerOfAttorney, Txt.S("UiPrintTabPoa"));
            _nav.Add(PrintForm.Reconciliation, Txt.S("UiPrintTabAct"));
            Page(PrintForm.General, GeneralTab());
            Page(PrintForm.Invoice, InvoiceTab());
            Page(PrintForm.PowerOfAttorney, PoaTab());
            Page(PrintForm.Reconciliation, ActTab());
            _nav.Level = f => _levels.TryGetValue(f, out var l) ? l : ReadyLevel.Soon;
            _nav.Selected += ShowForm;
            var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 6, 0, 6) };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LogicalToDeviceUnits(186)));
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _nav.Dock = DockStyle.Fill;
            _nav.Margin = new Padding(0, 0, 8, 0);
            _card.Dock = DockStyle.Fill;
            _card.Margin = new Padding(0);
            content.Controls.Add(_nav, 0, 0);
            content.Controls.Add(_card, 1, 0);
            root.Controls.Add(content, 0, 2);
            ShowForm(PrintForm.General);

            var actions = Flow();
            // «Сохранить» — основная, синяя
            var save = new Button { Text = Txt.S("UiPrintSave"), AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = NavList.Accent, ForeColor = Color.White, Padding = new Padding(10, 1, 10, 1) };
            save.FlatAppearance.BorderSize = 0;
            var trial = new Button { Text = Txt.S("UiPrintTrialDoc"), AutoSize = true, Padding = new Padding(6, 0, 6, 0) };
            save.Click += (s, e) => Save(true);
            trial.Click += (s, e) => Trial();
            actions.Controls.Add(save); actions.Controls.Add(trial);
            root.Controls.Add(actions, 0, 3);
            Controls.Add(root);

            _base.SelectedIndexChanged += (s, e) => ShowBase();
            foreach (var tb in new[] { _docsRoot, _address, _director, _directorTitle, _chief, _issuedBy, _kbe, _knp, _header, _footer, _actIntro }) tb.Leave += (s, e) => Save(false);
            foreach (var cb in _invParts.All.Concat(_poaParts.All).Concat(_actParts.All).Concat(new[] { _paySample, _poaId, _actMirror, _actByContracts }))
                cb.CheckedChanged += (s, e) => Save(false);
            _poaDays.ValueChanged += (s, e) => Save(false);
            _actPeriod.SelectedIndexChanged += (s, e) => Save(false);
        }

        // ---------------- Список форм и карточка ----------------

        private readonly NavList _nav = new NavList();
        private readonly Card _card = new Card();
        private readonly Dictionary<PrintForm, Control> _pages = new Dictionary<PrintForm, Control>();
        private PrintForm _current = PrintForm.General;

        /// <summary>Содержимое карточки формы: первой строкой — «Не хватает: …», ниже — таблица настроек.</summary>
        private void Page(PrintForm form, Control body)
        {
            var col = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Location = new Point(0, 0), BackColor = Color.White };
            var st = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 8), MaximumSize = new Size(LogicalToDeviceUnits(560), 0) };
            _status[form] = st;
            col.Controls.Add(st);
            col.Controls.Add(body);
            _pages[form] = col;
        }

        private void ShowForm(PrintForm form)
        {
            _current = form;
            _nav.Current = form;
            _card.Host.SuspendLayout();
            _card.Host.Controls.Clear();
            _card.Host.Controls.Add(_pages[form]);
            _card.Host.AutoScrollPosition = new Point(0, 0);
            _card.Host.ResumeLayout();
        }

        /// <summary>Таблица настроек: подпись слева, значение справа.</summary>
        private TableLayoutPanel Grid()
        {
            var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(0) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LogicalToDeviceUnits(165)));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            return t;
        }

        private static void Add(TableLayoutPanel t, string labelKey, Control value, string noteKey = null, string note = null)
        {
            var r = t.RowCount++;
            t.Controls.Add(new Label { Text = labelKey == null ? "" : Txt.S(labelKey), AutoSize = true, Margin = new Padding(0, 7, 8, 0) }, 0, r);
            if (noteKey == null && note == null) { t.Controls.Add(value, 1, r); return; }
            var col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            col.Controls.Add(value);
            col.Controls.Add(Gray(note ?? Txt.S(noteKey)));
            t.Controls.Add(col, 1, r);
        }

        private Control GeneralTab()
        {
            var t = Grid();
            var imgs = Flow();
            foreach (var (kind, key) in new[] { (PrintImage.Logo, "UiPrintLogo"), (PrintImage.Stamp, "UiPrintStamp"), (PrintImage.Sign, "UiPrintSign") })
            {
                var box = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 10, 0) };
                box.Controls.Add(new Label { Text = Txt.S(key), AutoSize = true });
                var pic = new PictureBox { Width = LogicalToDeviceUnits(112), Height = LogicalToDeviceUnits(56), SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White };
                _pics[kind] = pic;
                box.Controls.Add(pic);
                var bb = Flow();
                var load = new LinkLabel { Text = Txt.S("UiPrintLoad"), AutoSize = true, Margin = new Padding(0, 2, 8, 0) };
                var del = new LinkLabel { Text = Txt.S("UiPrintRemove"), AutoSize = true, Margin = new Padding(0, 2, 0, 0) };
                load.LinkClicked += (s, e) => LoadImage(kind);
                del.LinkClicked += (s, e) => RemoveImage(kind);
                bb.Controls.Add(load); bb.Controls.Add(del);
                box.Controls.Add(bb);
                imgs.Controls.Add(box);
            }
            Add(t, "UiPrintImages", imgs, "UiPrintImagesNote");
            Add(t, "UiPrintAddressOverride", _address);
            Add(t, "UiPrintDirector", _director);
            Add(t, "UiPrintDirectorTitle", _directorTitle);
            Add(t, "UiPrintChief", _chief);
            var docs = Flow();
            docs.Controls.Add(_docsRoot);
            var pick = new LinkLabel { Text = Txt.S("UiPrintDocsPick"), AutoSize = true, Margin = new Padding(6, 6, 6, 0) };
            var open = new LinkLabel { Text = Txt.S("UiPrintDocsOpen"), AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
            pick.LinkClicked += (s, e) =>
            {
                using (var d = new FolderBrowserDialog { Description = Txt.S("UiPrintDocsPickTitle"), SelectedPath = PrintService.Root })
                    if (d.ShowDialog(this) == DialogResult.OK) { _docsRoot.Text = d.SelectedPath; Save(false); }
            };
            open.LinkClicked += (s, e) => { try { PrintService.EnsureRootPublic(); System.Diagnostics.Process.Start(PrintService.Root); } catch { } };
            docs.Controls.Add(pick); docs.Controls.Add(open);
            Add(t, "UiPrintDocs", docs, "UiPrintDocsNote");
            // Отдельного окна «Реквизиты из 1С…» нет (решение пачки 2 v0.2, п. 3а): адрес и ФИО из 1С загружаются при настройке базы
            // и показываются серой подсказкой в полях
            Add(t, null, Gray(Txt.S("UiPrintGeneralNote")));
            return t;
        }

        private Control InvoiceTab()
        {
            var t = Grid();
            Add(t, "UiPrintParts", _invParts.Row());
            Add(t, "UiPrintIssuedBy", _issuedBy, "UiPrintIssuedByNote");
            var codes = Flow();
            codes.Controls.Add(_kbe);
            codes.Controls.Add(new Label { Text = Txt.S("UiPrintKnp"), AutoSize = true, Margin = new Padding(10, 6, 4, 0) });
            codes.Controls.Add(_knp);
            Add(t, "UiPrintKbe", codes);
            Add(t, null, _paySample);
            Add(t, "UiPrintHeader", Box(_header));
            Add(t, "UiPrintFooter", Box(_footer), "UiPrintFooterNote");
            return t;
        }

        private Control PoaTab()
        {
            var t = Grid();
            Add(t, "UiPrintParts", _poaParts.Row());
            var days = Flow();
            days.Controls.Add(_poaDays);
            days.Controls.Add(new Label { Text = Txt.S("UiPrintPoaDaysUnit"), AutoSize = true, Margin = new Padding(4, 6, 0, 0) });
            Add(t, "UiPrintPoaDays", days, "UiPrintPoaDaysNote");
            Add(t, null, _poaId, "UiPrintPoaIdentityNote");
            _poaSigners.Margin = new Padding(0, 7, 0, 2);
            Add(t, "UiPrintSigners", _poaSigners);
            var send = Gray(Txt.S("UiPrintPoaSend"));
            send.Margin = new Padding(0, 7, 0, 2);
            Add(t, "UiPrintSend", send);
            return t;
        }

        private Control ActTab()
        {
            var t = Grid();
            Add(t, "UiPrintParts", _actParts.Row());
            Add(t, "UiPrintActSigner", _actSigner, "UiPrintActSignerNote");
            _actPeriod.Items.AddRange(new object[] { Txt.S("UiPrintActPeriodMonth"), Txt.S("UiPrintActPeriodQuarter"), Txt.S("UiPrintActPeriodYear") });
            Add(t, "UiPrintActPeriod", _actPeriod);
            Add(t, null, _actMirror, "UiPrintActMirrorNote");
            Add(t, null, _actByContracts);
            Add(t, "UiPrintActIntro", Box(_actIntro), "UiPrintActIntroNote");
            return t;
        }

        private static readonly string[] Periods = { "month", "quarter", "year" };

        // ---------------- Помощники вида ----------------

        private static FlowLayoutPanel Flow() => new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 2, 0, 2) };
        private static Label Gray(string t) => new Label { Text = t, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(390, 0), Margin = new Padding(0, 2, 0, 4) };
        private static Label GrayLabel() => new Label { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(390, 0), Margin = new Padding(0, 4, 0, 2) };
        private static TextBox Tb(int w) => new TextBox { Width = w, Margin = new Padding(0, 3, 0, 3) };
        private static TextBox Multi() => new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
        private static CheckBox Check(string key) => new CheckBox { Text = Txt.S(key), AutoSize = true, Margin = new Padding(0, 4, 12, 2) };
        private Panel Box(TextBox tb)
        {
            var p = new Panel { Height = LogicalToDeviceUnits(tb == _footer ? 40 : 56), Width = LogicalToDeviceUnits(390), Margin = new Padding(0, 3, 0, 3) };
            p.Controls.Add(tb);
            return p;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);

        /// <summary>Серая подсказка в пустом поле — значение из 1С («пусто = из 1С»).</summary>
        private static void Cue(TextBox tb, string text) { if (tb.IsHandleCreated) SendMessage(tb.Handle, 0x1501, (IntPtr)1, text ?? ""); else tb.HandleCreated += (s, e) => SendMessage(tb.Handle, 0x1501, (IntPtr)1, text ?? ""); }

        // ---------------- Данные ----------------

        private BaseConfig Current(AgentConfig cfg) => cfg.Find(_base.SelectedItem as string ?? "");

        public void Reload()
        {
            var cur = _base.SelectedItem as string;
            _base.Items.Clear();
            foreach (var b in AgentConfig.Load().Bases) _base.Items.Add(b.Slug);
            if (_base.Items.Count > 0) _base.SelectedItem = cur != null && _base.Items.Contains(cur) ? cur : _base.Items[0];
            else ShowBase();
        }

        private void ShowBase()
        {
            _loading = true;
            var b = Current(AgentConfig.Load());
            var ps = b?.Print ?? new PrintSettings();
            var org = ps.Addresses?.Keys.FirstOrDefault() ?? ps.Signers?.Keys.FirstOrDefault();
            OrgSigner s1c = null;
            if (org != null) ps.Signers?.TryGetValue(org, out s1c);

            // Общее: пусто = из 1С, значение 1С — серой подсказкой в поле
            _address.Text = ps.AddressOverride ?? "";
            _docsRoot.Text = AgentConfig.Load().DocumentsRoot ?? "";
            Cue(_docsRoot, PrintService.DefaultRoot);
            Cue(_address, ps.Addresses?.Values.FirstOrDefault() ?? Txt.S("UiPrintFrom1CNone"));
            _director.Text = ps.Director ?? "";
            Cue(_director, Words.ShortName(s1c?.Director?.Name) is var dn && !string.IsNullOrEmpty(dn) ? dn : Txt.S("UiPrintFrom1CNone"));
            _directorTitle.Text = ps.DirectorTitle ?? "";
            Cue(_directorTitle, string.IsNullOrEmpty(s1c?.Director?.Title) ? Txt.S("UiPrintDirectorTitleCue") : s1c.Director.Title);
            _chief.Text = ps.ChiefAccountant ?? "";
            Cue(_chief, Words.ShortName(s1c?.ChiefAccountant?.Name) is var cn && !string.IsNullOrEmpty(cn) ? cn : Txt.S("UiPrintFrom1CNone"));
            foreach (var kv in _pics)
            {
                kv.Value.Image?.Dispose();
                kv.Value.Image = null;
                var f = b == null ? null : PrintStore.Existing(b.Slug, kv.Key);
                if (f != null) using (var fs = File.OpenRead(f)) kv.Value.Image = Image.FromStream(fs);
            }
            // Счёт
            _invParts.Show(ps.Invoice);
            _issuedBy.Text = ps.IssuedByEffective;
            _kbe.Text = ps.Kbe ?? ""; _knp.Text = ps.Knp ?? "";
            _paySample.Checked = ps.InvoicePaymentSample;
            _header.Text = ps.HeaderText ?? "";
            _footer.Text = ps.InvoiceFooter ?? "";
            // Доверенность
            _poaParts.Show(ps.PowerOfAttorney);
            _poaDays.Value = Math.Max(_poaDays.Minimum, Math.Min(_poaDays.Maximum, ps.PoaValidDays));
            _poaId.Checked = ps.PoaIdentity;
            // Акт сверки
            _actParts.Show(ps.Reconciliation);
            _actPeriod.SelectedIndex = Math.Max(0, Array.IndexOf(Periods, (ps.ActPeriod ?? "quarter").ToLowerInvariant()));
            _actMirror.Checked = ps.ActMirror;
            _actByContracts.Checked = ps.ActByContracts;
            _actIntro.Text = ps.ActIntro ?? "";
            _loading = false;
            ShowStatus(b);
        }

        /// <summary>Точки статуса и строки «чего не хватает»; подписанты — как их увидит форма.</summary>
        private void ShowStatus(BaseConfig b)
        {
            var ps = b?.Print ?? new PrintSettings();
            var org = ps.Addresses?.Keys.FirstOrDefault() ?? ps.Signers?.Keys.FirstOrDefault();
            foreach (PrintForm form in Enum.GetValues(typeof(PrintForm)))
            {
                var (level, hint) = b == null ? (ReadyLevel.Soon, "") : PrintReadiness.Of(b, form);
                _levels[form] = level;
                _status[form].Text = hint;
                _status[form].ForeColor = level == ReadyLevel.Missing ? Color.FromArgb(150, 100, 0) : SystemColors.GrayText;
            }
            var (dir, title) = ps.DirectorFor(org);
            var blank = "/________/";
            _poaSigners.Text = Txt.F("UiPrintPoaSigners", dir ?? blank, ps.ChiefAccountantFor(org) ?? blank);
            _actSigner.Text = $"{title ?? Txt.S("UiPrintDirectorTitleCue")} {dir ?? blank}";
            var miss = b == null ? null : PrintStore.Missing(b.Slug);
            _warn.Text = miss == null ? "" : Txt.F("PrintNotConfigured", b.Slug, miss);
            _warn.Visible = miss != null;
            _nav.Invalidate();
        }

        private void Save(bool tell)
        {
            if (_loading) return;
            var cfg = AgentConfig.Load();
            var b = Current(cfg);
            if (b == null) return;
            var ps = b.Print = b.Print ?? new PrintSettings();
            string V(TextBox t) => string.IsNullOrWhiteSpace(t.Text) ? null : t.Text.Trim();
            ps.AddressOverride = V(_address);
            ps.Director = V(_director); ps.DirectorTitle = V(_directorTitle); ps.ChiefAccountant = V(_chief);
            ps.Invoice = _invParts.Value();
            ps.IssuedBy = V(_issuedBy) ?? PrintSettings.DefaultSignerTitle;
            ps.SignerName = null; ps.SignerTitle = null;
            ps.Kbe = _kbe.Text.Trim(); ps.Knp = _knp.Text.Trim();
            ps.InvoicePaymentSample = _paySample.Checked;
            ps.HeaderText = _header.Text.Trim();
            ps.InvoiceFooter = V(_footer);
            ps.PowerOfAttorney = _poaParts.Value();
            ps.PoaValidDays = (int)_poaDays.Value;
            ps.PoaIdentity = _poaId.Checked;
            ps.Reconciliation = _actParts.Value();
            ps.ActPeriod = Periods[Math.Max(0, _actPeriod.SelectedIndex)];
            ps.ActMirror = _actMirror.Checked;
            ps.ActByContracts = _actByContracts.Checked;
            ps.ActIntro = _actIntro.Text;
            cfg.DocumentsRoot = V(_docsRoot);
            cfg.Upsert(b); cfg.Save();
            if (tell) Log.Ok(Txt.F("UiPrintSaved", b.Slug));
            ShowStatus(b);
        }

        private void LoadImage(PrintImage kind)
        {
            var b = Current(AgentConfig.Load());
            if (b == null) return;
            using (var d = new OpenFileDialog { Filter = Txt.S(kind == PrintImage.Logo ? "UiPrintFilterLogo" : "UiPrintFilterPng") })
            {
                if (d.ShowDialog(_owner()) != DialogResult.OK) return;
                try
                {
                    var note = PrintStore.Save(b.Slug, kind, d.FileName);
                    Log.Ok(Txt.F("PrintImageSaved", Txt.S("UiPrint" + kind)) + (note == null ? "" : " " + note));
                    if (note != null) MessageBox.Show(_owner(), note, "Bilem Agent", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception e) { MessageBox.Show(_owner(), e.Message, "Bilem Agent", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
            ShowBase();
        }

        private void RemoveImage(PrintImage kind)
        {
            var b = Current(AgentConfig.Load());
            if (b == null) return;
            foreach (var p in _pics.Values) { p.Image?.Dispose(); p.Image = null; }
            PrintStore.Remove(b.Slug, kind);
            ShowBase();
        }

        private void CopyFrom()
        {
            var cfg = AgentConfig.Load();
            var to = Current(cfg);
            var others = cfg.Bases.Where(x => to != null && x.Slug != to.Slug).Select(x => x.Slug).ToList();
            if (to == null || others.Count == 0) return;
            var menu = new ContextMenuStrip();
            foreach (var slug in others)
                menu.Items.Add(slug, null, (s, e) =>
                {
                    var c = AgentConfig.Load(); var dst = c.Find(to.Slug); var src = c.Find(slug);
                    foreach (var p in _pics.Values) { p.Image?.Dispose(); p.Image = null; }
                    PrintStore.Copy(src, dst);
                    c.Upsert(dst); c.Save();
                    Log.Ok(Txt.F("UiPrintCopied", slug, dst.Slug));
                    ShowBase();
                });
            menu.Show(Cursor.Position);
        }

        /// <summary>«Пробный документ…» — форма текущей подвкладки (на «Общем» — счёт).</summary>
        private void Trial()
        {
            Save(false);
            var b = Current(AgentConfig.Load());
            if (b == null) return;
            var form = _current;
            if (form == PrintForm.Reconciliation) { using (var f = new ActTrialForm(b)) f.ShowDialog(_owner()); }
            else using (var f = new TrialForm(b, form == PrintForm.PowerOfAttorney)) f.ShowDialog(_owner());
        }
    }

    /// <summary>«Пробный документ…» для счёта и доверенности: последние 20 из 1С или номер → PDF открывается в просмотрщике.</summary>
    internal sealed class TrialForm : BilemForm
    {
        private readonly ListView _list = new ListView { View = View.Details, FullRowSelect = true, MultiSelect = false, Dock = DockStyle.Fill, HideSelection = false };
        private readonly TextBox _number = new TextBox { Width = 120 };
        private readonly BaseConfig _b;
        private readonly bool _poa;

        public TrialForm(BaseConfig b, bool poa = false)
        {
            _b = b; _poa = poa;
            Text = Txt.F(poa ? "UiPrintTrialPoaTitle" : "UiPrintTrialTitle", b.Slug);
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(Px(560), Px(380));
            _list.Columns.Add(Txt.S("UiPrintColNumber"), Px(70));
            _list.Columns.Add(Txt.S("UiPrintColDate"), Px(80));
            _list.Columns.Add(Txt.S(poa ? "UiPrintColEmployee" : "UiPrintColBuyer"), Px(240));
            _list.Columns.Add(Txt.S(poa ? "UiPrintColFrom" : "UiPrintColSum"), Px(110), poa ? HorizontalAlignment.Left : HorizontalAlignment.Right);
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6) };
            bottom.Controls.Add(new Label { Text = Txt.S("UiPrintOrNumber"), AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
            bottom.Controls.Add(_number);
            var make = new Button { Text = Txt.S("UiPrintMake"), AutoSize = true };
            make.Click += (s, e) => Make();
            _list.DoubleClick += (s, e) => Make();
            bottom.Controls.Add(make);
            AcceptButton = make;
            Controls.Add(_list);
            Controls.Add(bottom);
            Shown += (s, e) => LoadList();
        }

        private void LoadList()
        {
            try
            {
                Cursor = Cursors.WaitCursor;
                var rt = BaseRuntime.FromConfig(_b);
                if (_poa)
                    foreach (var r in PoaSource.Search(rt.OData, null, null, 20).Items)
                    {
                        var it = new ListViewItem(r.Number) { Tag = r.Number };
                        it.SubItems.Add(r.Date.ToString("dd.MM.yyyy"));
                        it.SubItems.Add(r.Person ?? "");
                        it.SubItems.Add(r.From ?? "");
                        _list.Items.Add(it);
                    }
                else
                    foreach (var r in InvoiceSource.Recent(rt.OData))
                    {
                        var it = new ListViewItem(r.Number) { Tag = r.Number };
                        it.SubItems.Add(r.Date.ToString("dd.MM.yyyy"));
                        it.SubItems.Add(r.Buyer);
                        it.SubItems.Add(Words.Money(r.Sum));
                        _list.Items.Add(it);
                    }
            }
            catch (Exception e) { MessageBox.Show(this, e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { Cursor = Cursors.Default; }
        }

        private void Make()
        {
            var number = _number.Text.Trim();
            if (number.Length == 0 && _list.SelectedItems.Count > 0) number = (string)_list.SelectedItems[0].Tag;
            if (number.Length == 0) return;
            try
            {
                Cursor = Cursors.WaitCursor;
                var b = AgentConfig.Load().Find(_b.Slug);
                var od = BaseRuntime.FromConfig(_b).OData;
                var r = _poa ? PrintService.PowerOfAttorney(b, od, number) : PrintService.Invoice(b, od, number);
                Audit.Write(Audit.Print, b.Slug, _poa ? Core.Mail.MailService.DocPowerOfAttorney : Core.Mail.MailService.DocInvoice, Path.GetFileNameWithoutExtension(r.Path), r.Path, r.Pdf);
                Log.Ok(Txt.F("PrintDone", r.Path));
                Process.Start(new ProcessStartInfo(r.Path) { UseShellExecute = true });
            }
            catch (Exception e) { MessageBox.Show(this, e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { Cursor = Cursors.Default; }
        }
    }

    /// <summary>«Пробный документ…» для акта сверки: контрагент (БИН или название) и период → PDF в просмотрщике.</summary>
    internal sealed class ActTrialForm : BilemForm
    {
        private readonly TextBox _cp = new TextBox { Width = 260 };
        private readonly DateTimePicker _from = new DateTimePicker { Format = DateTimePickerFormat.Short, Width = 110 };
        private readonly DateTimePicker _to = new DateTimePicker { Format = DateTimePickerFormat.Short, Width = 110 };
        private readonly BaseConfig _b;

        public ActTrialForm(BaseConfig b)
        {
            _b = b;
            Text = Txt.F("UiPrintTrialActTitle", b.Slug);
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            var (f, t) = ReconciliationSource.DefaultPeriod(b.Print?.ActPeriod, DateTime.Now);
            _from.Value = f; _to.Value = t;
            var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Padding = new Padding(10), Dock = DockStyle.Fill };
            grid.Controls.Add(new Label { Text = Txt.S("UiPrintActCounterparty"), AutoSize = true, Margin = new Padding(0, 6, 8, 0) }, 0, 0);
            grid.Controls.Add(_cp, 1, 0);
            grid.Controls.Add(new Label { Text = Txt.S("UiPrintActFromTo"), AutoSize = true, Margin = new Padding(0, 6, 8, 0) }, 0, 1);
            var period = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
            period.Controls.Add(_from);
            period.Controls.Add(new Label { Text = "—", AutoSize = true, Margin = new Padding(4, 6, 4, 0) });
            period.Controls.Add(_to);
            grid.Controls.Add(period, 1, 1);
            var make = new Button { Text = Txt.S("UiPrintMake"), AutoSize = true };
            make.Click += (s, e) => Make();
            grid.Controls.Add(make, 1, 2);
            AcceptButton = make;
            Controls.Add(grid);
        }

        private void Make()
        {
            if (_cp.Text.Trim().Length < 2) return;
            try
            {
                Cursor = Cursors.WaitCursor;
                var b = AgentConfig.Load().Find(_b.Slug);
                var r = PrintService.Reconciliation(b, BaseRuntime.FromConfig(_b).OData, _cp.Text.Trim(), _from.Value.Date, _to.Value.Date);
                Audit.Write(Audit.Print, b.Slug, Core.Mail.MailService.DocReconciliation, Path.GetFileNameWithoutExtension(r.Path), r.Path, r.Pdf);
                Log.Ok(Txt.F("PrintDone", r.Path));
                Process.Start(new ProcessStartInfo(r.Path) { UseShellExecute = true });
            }
            catch (Exception e) { MessageBox.Show(this, e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { Cursor = Cursors.Default; }
        }
    }

    /// <summary>
    /// Список форм слева, как в «Параметрах» Windows 11: строка — название и точка статуса справа; выбранная — серый фон
    /// и синяя полоска 3 px слева, наведение — светлый фон; разделитель после «Общего». Рисуем сами, без рамки.
    /// </summary>
    internal sealed class NavList : Control
    {
        public static readonly Color Accent = Color.FromArgb(0, 103, 192);
        private static readonly Color SelectedBack = Color.FromArgb(230, 230, 230), HoverBack = Color.FromArgb(240, 240, 240);
        private readonly List<(PrintForm Form, string Text, bool Separator)> _items = new List<(PrintForm, string, bool)>();
        private int _hover = -1;
        private PrintForm _current;

        public event Action<PrintForm> Selected;
        public Func<PrintForm, ReadyLevel> Level { get; set; } = f => ReadyLevel.Soon;
        public PrintForm Current { get => _current; set { _current = value; Invalidate(); } }

        public NavList()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        public void Add(PrintForm form, string text, bool separatorAfter = false) { _items.Add((form, text, separatorAfter)); Invalidate(); }

        private int RowH => LogicalToDeviceUnits(36);
        private int SepH => LogicalToDeviceUnits(13);

        /// <summary>Прямоугольник строки i (с учётом разделителей выше).</summary>
        private Rectangle RowRect(int i)
        {
            var y = 0;
            for (var k = 0; k < i; k++) y += RowH + (_items[k].Separator ? SepH : 0);
            return new Rectangle(0, y, Width, RowH);
        }

        private int HitTest(Point p)
        {
            for (var i = 0; i < _items.Count; i++) if (RowRect(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e) { var h = HitTest(e.Location); if (h != _hover) { _hover = h; Invalidate(); } base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            var i = HitTest(e.Location);
            if (i >= 0) { Current = _items[i].Form; Selected?.Invoke(Current); }
            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? SystemColors.Control);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            for (var i = 0; i < _items.Count; i++)
            {
                var (form, text, sep) = _items[i];
                var r = RowRect(i);
                var sel = form == _current;
                if (sel || i == _hover)
                    using (var path = Card.Rounded(new Rectangle(r.X, r.Y + 1, r.Width - 1, r.Height - 2), LogicalToDeviceUnits(5)))
                    using (var b = new SolidBrush(sel ? SelectedBack : HoverBack)) g.FillPath(b, path);
                if (sel)
                    using (var path = Card.Rounded(new Rectangle(r.X + 1, r.Y + r.Height / 4, LogicalToDeviceUnits(3), r.Height / 2), LogicalToDeviceUnits(1)))
                    using (var b = new SolidBrush(Accent)) g.FillPath(b, path);
                var d = LogicalToDeviceUnits(9);
                var dot = new Rectangle(r.Right - d - LogicalToDeviceUnits(12), r.Y + (r.Height - d) / 2, d, d);
                var level = Level(form);
                var color = level == ReadyLevel.Ready ? Color.FromArgb(46, 160, 67) : level == ReadyLevel.Missing ? Color.FromArgb(230, 170, 20) : Color.FromArgb(170, 170, 170);
                using (var b = new SolidBrush(color)) g.FillEllipse(b, dot);
                var tr = new Rectangle(r.X + LogicalToDeviceUnits(14), r.Y, dot.Left - r.X - LogicalToDeviceUnits(18), r.Height);
                TextRenderer.DrawText(g, text, Font, tr, SystemColors.ControlText, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                if (sep)
                    using (var p = new Pen(Color.FromArgb(215, 215, 215)))
                    {
                        var y = r.Bottom + SepH / 2;
                        g.DrawLine(p, r.X + LogicalToDeviceUnits(10), y, r.Right - LogicalToDeviceUnits(10), y);
                    }
            }
        }
    }

    /// <summary>Белая карточка со скруглёнными углами и тонкой рамкой; содержимое — в Host (с прокруткой).</summary>
    internal sealed class Card : Panel
    {
        public Panel Host { get; } = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White };

        public Card()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Padding = new Padding(16, 14, 6, 10);
            Controls.Add(Host);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? SystemColors.Control);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), LogicalToDeviceUnits(8)))
            {
                using (var b = new SolidBrush(Color.White)) g.FillPath(b, path);
                using (var p = new Pen(Color.FromArgb(218, 218, 218))) g.DrawPath(p, path);
            }
        }

        public static System.Drawing.Drawing2D.GraphicsPath Rounded(Rectangle r, int radius)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            var d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
