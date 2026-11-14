using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Bilem.Agent.Core;

namespace Bilem.Agent
{
    /// <summary>«Добавить базу»: выбор из найденных (ibases.v8i всех профилей) или папка вручную → логин/пароль администратора 1С.</summary>
    internal sealed class AddBaseForm : BilemForm
    {
        private readonly ListView _found = new ListView { View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, Dock = DockStyle.Fill };
        private readonly TextBox _path = new TextBox { Dock = DockStyle.Fill, Margin = Padding.Empty };
        private readonly TextBox _slug = new TextBox { Dock = DockStyle.Fill };
        private readonly TextBox _user = new TextBox { Dock = DockStyle.Fill, Text = "Администратор" };
        private readonly TextBox _pwd = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        private readonly CheckBox _iis = new CheckBox { Text = Txt.S("UiEnableIis"), Checked = true, AutoSize = true, Dock = DockStyle.Fill };
        private readonly TableLayoutPanel _root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };

        public SetupOptions Options { get; private set; }

        public AddBaseForm()
        {
            Text = Txt.S("UiAddBaseTitle");
            StartPosition = FormStartPosition.CenterParent;

            _found.ShowItemToolTips = true;
            _found.Columns.Add(Txt.S("UiFoundName"), Px(300));
            _found.Columns.Add(Txt.S("UiFoundKind"), Px(KindColumn));
            _found.Resize += (s, e) => FitColumns();
            _found.HandleCreated += (s, e) => FitColumns();
            foreach (var b in BaseDiscovery.FindAll())
            {
                var it = new ListViewItem(b.Name) { Tag = b, ToolTipText = b.Kind == BaseKind.File ? b.Path : $"{b.Server}\\{b.Ref}" };
                it.SubItems.Add(b.Kind == BaseKind.File ? (b.Exists ? Txt.S("UiKindFile") : Txt.S("UiKindFileMissing")) : Txt.S("UiKindServerSoon"));
                if (b.Kind != BaseKind.File || !b.Exists) it.ForeColor = SystemColors.GrayText;
                _found.Items.Add(it);
            }
            _found.SelectedIndexChanged += (s, e) =>
            {
                var b = _found.SelectedItems.Count == 1 ? (FoundBase)_found.SelectedItems[0].Tag : null;
                if (b != null && b.Kind == BaseKind.File && b.Exists) _path.Text = b.Path;
            };
            var ok = new Button { Text = Txt.S("UiConnectBase"), AutoSize = true, Enabled = false, Margin = new Padding(0, 0, 6, 0), TabIndex = 0 };
            var cancel = new Button { Text = Txt.S("UiCancel"), DialogResult = DialogResult.Cancel, AutoSize = true, Margin = Padding.Empty, TabIndex = 1 };
            ok.Click += (s, e) => Accept();
            _path.TextChanged += (s, e) =>
            {
                _slug.Text = string.IsNullOrWhiteSpace(_path.Text) ? "" : BaseDiscovery.SlugFromPath(_path.Text);
                ok.Enabled = !string.IsNullOrWhiteSpace(_path.Text);
            };

            var browse = new Button { Text = Txt.S("UiBrowse"), AutoSize = true, Margin = new Padding(6, 0, 0, 0), Anchor = AnchorStyles.Right };
            browse.Click += (s, e) =>
            {
                using (var d = new FolderBrowserDialog { Description = Txt.S("UiBrowseHint"), ShowNewFolderButton = false })
                    if (d.ShowDialog(this) == DialogResult.OK) _path.Text = d.SelectedPath;
            };

            // Поля — таблица без объединённых ячеек: длинный текст в объединённой ячейке раздувал колонку подписей и сжимал поля
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Margin = new Padding(0, 0, 0, 6) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var pathRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Margin = new Padding(3) };
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pathRow.Controls.Add(_path, 0, 0); pathRow.Controls.Add(browse, 1, 0);
            int r = 0;
            foreach (var (caption, field) in new (string, Control)[] { ("UiFolder", pathRow), ("UiSlug", _slug), ("UiAdminLogin", _user), ("UiAdminPassword", _pwd) })
            {
                grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                grid.Controls.Add(new Label { Text = Txt.S(caption), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 3, 6, 3) }, 0, r);
                grid.Controls.Add(field, 1, r++);
            }

            var hint = new Label { Text = Txt.S("UiFoundHint"), AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 6) };
            var note = new Label { Text = Txt.S("UiAddBaseNote"), AutoSize = true, Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 6) };
            _found.Margin = new Padding(0, 0, 0, 6);
            _iis.Margin = new Padding(0, 0, 0, 6);
            var buttons = new FlowLayoutPanel { Anchor = AnchorStyles.Right, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
            buttons.Controls.Add(cancel); buttons.Controls.Add(ok);

            // Одна колонка, список — единственная растягивающаяся строка; остальные строки берут высоту по содержимому, текст переносится по ширине колонки
            _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var rows = new Control[] { hint, _found, grid, _iis, note, buttons };
            _root.RowCount = rows.Length;
            for (int i = 0; i < rows.Length; i++)
            {
                _root.RowStyles.Add(rows[i] == _found ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
                _root.Controls.Add(rows[i], 0, i);
            }
            Controls.Add(_root);
            AcceptButton = ok; CancelButton = cancel;
            ActiveControl = _found;
        }

        private const int KindColumn = 220;

        /// <summary>«Тип» — постоянной ширины, «Название» — всё остальное: без горизонтальной прокрутки.</summary>
        private void FitColumns()
        {
            _found.Columns[1].Width = Px(KindColumn);
            _found.Columns[0].Width = Math.Max(Px(80), _found.ClientSize.Width - _found.Columns[1].Width);
        }

        /// <summary>
        /// Размеры задаём здесь, в пикселях текущего экрана: окно собрано кодом, автомасштаб WinForms к нему не применяется, а шрифт растёт с масштабом.
        /// С размерами в «сырых» пикселях при 125–200% окно оставалось маленьким и поля сжимались. Окно не больше рабочей области экрана.
        /// </summary>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _root.Padding = new Padding(Px(12));
            var area = Screen.FromControl(this).WorkingArea;
            FitMinimum();
            Size = new Size(Math.Min(Px(720), area.Width), Math.Min(Px(560), area.Height));
            var around = StartPosition == FormStartPosition.CenterParent && Owner != null ? Owner.Bounds : area;
            var x = around.Left + (around.Width - Width) / 2;
            var y = around.Top + (around.Height - Height) / 2;
            Location = new Point(Math.Max(area.Left, Math.Min(x, area.Right - Width)), Math.Max(area.Top, Math.Min(y, area.Bottom - Height)));
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            FitMinimum();
        }

        private void FitMinimum()
        {
            var area = Screen.FromControl(this).WorkingArea;
            MinimumSize = new Size(Math.Min(Px(640), area.Width), Math.Min(Px(520), area.Height));
        }

        private void Accept()
        {
            var path = _path.Text.Trim();
            if (!File.Exists(Path.Combine(path, "1Cv8.1CD"))) { MessageBox.Show(this, Txt.F("NoBaseFile", path) + Environment.NewLine + Txt.S("HintBaseFolder"), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (!Onboarding.SlugRx.IsMatch(_slug.Text.Trim())) { MessageBox.Show(this, Txt.F("BadSlug", _slug.Text) + Environment.NewLine + Txt.S("HintSlug"), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (string.IsNullOrWhiteSpace(_user.Text)) { MessageBox.Show(this, Txt.S("HintAdminLogin"), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            // Контроллер домена без IIS: Microsoft не рекомендует ставить туда IIS — спрашиваем до установки
            var allowDc = false;
            if (_iis.Checked && !Iis.Installed && WinInfo.IsDomainController())
            {
                if (MessageBox.Show(this, Txt.S("UiDcAsk"), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                allowDc = true;
            }
            // Зарегистрирован COM-коннектор другой платформы — им могут пользоваться другие программы: менять только с «да»
            var allowCom = false;
            var plat = Platform.Choose(Platform.Find());
            var inUse = plat == null ? null : Platform.ComInUse(plat);
            if (inUse != null)
            {
                if (MessageBox.Show(this, Txt.F("UiComAsk", inUse, plat.Version), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                allowCom = true;
            }
            Options = new SetupOptions { BasePath = path, Slug = _slug.Text.Trim(), AdminUser = _user.Text.Trim(), AdminPassword = _pwd.Text, EnableIis = _iis.Checked, AllowDomainController = allowDc, AllowComRegister = allowCom };
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
