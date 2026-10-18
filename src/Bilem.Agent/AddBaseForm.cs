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

            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(10) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            int r = 0;
            var lbl = new Label { Text = Txt.S("UiFoundHint"), AutoSize = true };
            grid.Controls.Add(lbl, 0, r); grid.SetColumnSpan(lbl, 3); r++;
            grid.Controls.Add(_found, 0, r); grid.SetColumnSpan(_found, 3); r++;
            grid.Controls.Add(new Label { Text = Txt.S("UiFolder"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, r);
            grid.Controls.Add(_path, 1, r); grid.Controls.Add(browse, 2, r); r++;
            grid.Controls.Add(new Label { Text = Txt.S("UiSlug"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, r);
            grid.Controls.Add(_slug, 1, r); r++;
            grid.Controls.Add(new Label { Text = Txt.S("UiAdminLogin"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, r);
            grid.Controls.Add(_user, 1, r); r++;
            grid.Controls.Add(new Label { Text = Txt.S("UiAdminPassword"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, r);
            grid.Controls.Add(_pwd, 1, r); r++;
            grid.Controls.Add(_iis, 1, r); r++;
            var note = new Label { Text = Txt.S("UiAddBaseNote"), AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(780, 0) };
            grid.Controls.Add(note, 0, r); grid.SetColumnSpan(note, 3); r++;

            var ok = new Button { Text = Txt.S("UiConnectBase"), AutoSize = true };
            var cancel = new Button { Text = Txt.S("UiCancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
            ok.Click += (s, e) => Accept();
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
            buttons.Controls.Add(cancel); buttons.Controls.Add(ok);

            Controls.Add(grid);
            Controls.Add(buttons);
            AcceptButton = ok; CancelButton = cancel;
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
