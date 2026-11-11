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
        private readonly TextBox _path = new TextBox { Dock = DockStyle.Fill };
        private readonly TextBox _slug = new TextBox { Width = 200 };
        private readonly TextBox _user = new TextBox { Width = 260, Text = "Администратор" };
        private readonly TextBox _pwd = new TextBox { Width = 260, UseSystemPasswordChar = true };
        private readonly CheckBox _iis = new CheckBox { Text = Txt.S("UiEnableIis"), Checked = true, AutoSize = true };

        public SetupOptions Options { get; private set; }

        public AddBaseForm()
        {
            Text = Txt.S("UiAddBaseTitle");
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(860, 600);
            MinimumSize = new Size(640, 480);

            _found.Columns.Add(Txt.S("UiFoundName"), Px(220));
            _found.Columns.Add(Txt.S("UiFoundKind"), Px(160));
            _found.Columns.Add(Txt.S("UiFoundWhere"), Px(420));
            foreach (var b in BaseDiscovery.FindAll())
            {
                var it = new ListViewItem(b.Name) { Tag = b };
                it.SubItems.Add(b.Kind == BaseKind.File ? (b.Exists ? Txt.S("UiKindFile") : Txt.S("UiKindFileMissing")) : Txt.S("UiKindServerSoon"));
                it.SubItems.Add(b.Kind == BaseKind.File ? b.Path : $"{b.Server}\\{b.Ref}");
                if (b.Kind != BaseKind.File || !b.Exists) it.ForeColor = SystemColors.GrayText;
                _found.Items.Add(it);
            }
            _found.SelectedIndexChanged += (s, e) =>
            {
                var b = _found.SelectedItems.Count == 1 ? (FoundBase)_found.SelectedItems[0].Tag : null;
                if (b != null && b.Kind == BaseKind.File && b.Exists) _path.Text = b.Path;
            };
            _path.TextChanged += (s, e) => _slug.Text = string.IsNullOrWhiteSpace(_path.Text) ? "" : BaseDiscovery.SlugFromPath(_path.Text);

            var browse = new Button { Text = Txt.S("UiBrowse"), AutoSize = true };
            browse.Click += (s, e) =>
            {
                using (var d = new FolderBrowserDialog { Description = Txt.S("UiBrowseHint"), ShowNewFolderButton = false })
                    if (d.ShowDialog(this) == DialogResult.OK) _path.Text = d.SelectedPath;
            };

            // Поля — своей панелью снизу: они получают высоту первыми, список берёт остаток. В общей таблице список при масштабе не сжимался и выталкивал поля под кнопки
            var grid = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, Padding = new Padding(0, 6, 0, 0) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            int r = 0;
            var lbl = new Label { Text = Txt.S("UiFoundHint"), AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 6) };
            grid.Controls.Add(new Label { Text = Txt.S("UiFolder"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, r);
            grid.Controls.Add(_path, 1, r); grid.Controls.Add(browse, 2, r); r++;
            grid.Controls.Add(new Label { Text = Txt.S("UiSlug"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, r);
            grid.Controls.Add(_slug, 1, r); r++;
            grid.Controls.Add(new Label { Text = Txt.S("UiAdminLogin"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, r);
            grid.Controls.Add(_user, 1, r); r++;
            grid.Controls.Add(new Label { Text = Txt.S("UiAdminPassword"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, r);
            grid.Controls.Add(_pwd, 1, r); r++;
            grid.Controls.Add(_iis, 0, r); grid.SetColumnSpan(_iis, 3); r++;
            var note = new Label { Text = Txt.S("UiAddBaseNote"), AutoSize = true, ForeColor = SystemColors.GrayText };
            grid.Controls.Add(note, 0, r); grid.SetColumnSpan(note, 3); r++;

            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 10, 10, 0) };
            body.Controls.Add(_found); body.Controls.Add(grid); body.Controls.Add(lbl);
            Wrap(lbl, body, 30); Wrap(note, body, 30); Wrap(_iis, body, 30);

            var ok = new Button { Text = Txt.S("UiConnectBase"), AutoSize = true };
            var cancel = new Button { Text = Txt.S("UiCancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
            ok.Click += (s, e) => Accept();
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
            buttons.Controls.Add(cancel); buttons.Controls.Add(ok);

            Controls.Add(body);
            Controls.Add(buttons);
            AcceptButton = ok; CancelButton = cancel;
        }

        /// <summary>Окно не больше рабочей области экрана: при крупном масштабе на маленьком мониторе низ не уезжает за край.</summary>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            var area = Screen.FromControl(this).WorkingArea;
            MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width), Math.Min(MinimumSize.Height, area.Height));
            Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
            Location = new Point(Math.Max(area.Left, Math.Min(Left, area.Right - Width)), Math.Max(area.Top, Math.Min(Top, area.Bottom - Height)));
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
