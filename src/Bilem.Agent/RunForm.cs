using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Bilem.Agent.Core;

namespace Bilem.Agent
{
    /// <summary>Окно «Настройка»: список шагов с галочками и живой журнал. Работа идёт в фоне, окно не зависает.</summary>
    internal sealed class RunForm : BilemForm
    {
        private readonly ListView _steps = new ListView { View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, Dock = DockStyle.Fill };
        private readonly TextBox _log = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 9f), BackColor = SystemColors.Window };
        private readonly Label _result = new Label { AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        private readonly Button _close = new Button { Text = Txt.S("UiClose"), Enabled = false, AutoSize = true };
        private readonly Func<Onboarding, bool> _work;
        public bool Success { get; private set; }

        public RunForm(string title, Func<Onboarding, bool> work)
        {
            _work = work;
            Text = title;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(820, 620);
            MinimumSize = new Size(600, 420);

            _steps.Columns.Add(Txt.S("UiStepColumn"), Px(420));
            _steps.Columns.Add(Txt.S("UiStatusColumn"), Px(300));

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 220 };
            split.Panel1.Controls.Add(_steps);
            split.Panel2.Controls.Add(_log);

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
            bottom.Controls.Add(_close);
            bottom.Controls.Add(_result);
            _close.Click += (s, e) => Close();

            Controls.Add(split);
            Controls.Add(bottom);
            Shown += async (s, e) => await Start();
            FormClosing += (s, e) => { if (!_close.Enabled) e.Cancel = true; };
        }

        private async Task Start()
        {
            var ob = new Onboarding();
            ob.StepChanged += st => BeginInvoke((Action)(() => ShowStep(st)));
            Action<LogEntry> onLog = en => BeginInvoke((Action)(() => _log.AppendText(en + Environment.NewLine)));
            Log.Written += onLog;
            try { Success = await Task.Run(() => _work(ob)); }
            finally { Log.Written -= onLog; }
            _result.Text = Success ? Txt.S("UiRunOk") : Txt.S("UiRunFail");
            _result.ForeColor = Success ? Color.DarkGreen : Color.DarkRed;
            _close.Enabled = true;
            _close.Focus();
        }

        private void ShowStep(Step st)
        {
            ListViewItem it = null;
            foreach (ListViewItem x in _steps.Items) if ((string)x.Tag == st.Id) it = x;
            if (it == null) { it = new ListViewItem(st.Title) { Tag = st.Id }; it.SubItems.Add(""); _steps.Items.Add(it); }
            string mark;
            switch (st.Status)
            {
                case StepStatus.Ok: mark = "✔ "; it.ForeColor = Color.DarkGreen; break;
                case StepStatus.Fail: mark = "✖ "; it.ForeColor = Color.DarkRed; break;
                case StepStatus.Warn: mark = "! "; it.ForeColor = Color.DarkGoldenrod; break;
                case StepStatus.Running: mark = "… "; it.ForeColor = SystemColors.ControlText; break;
                case StepStatus.Skipped: mark = "– "; it.ForeColor = SystemColors.GrayText; break;
                default: mark = ""; break;
            }
            it.Text = mark + st.Title;
            it.SubItems[1].Text = Txt.S("UiStep_" + st.Status) + (string.IsNullOrEmpty(st.Detail) ? "" : " — " + st.Detail);
            it.EnsureVisible();
        }
    }

    /// <summary>Логин и пароль администратора 1С (пароль нигде не сохраняется).</summary>
    internal sealed class CredForm : BilemForm
    {
        public string User => _user.Text.Trim();
        public string Password => _pwd.Text;
        private readonly TextBox _user = new TextBox { Width = 260, Text = "Администратор" };
        private readonly TextBox _pwd = new TextBox { Width = 260, UseSystemPasswordChar = true };

        public CredForm(string title, string hint)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Padding = new Padding(12) };
            t.Controls.Add(new Label { Text = hint, AutoSize = true, MaximumSize = new Size(420, 0) }, 0, 0);
            t.SetColumnSpan(t.GetControlFromPosition(0, 0), 2);
            t.Controls.Add(new Label { Text = Txt.S("UiAdminLogin"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            t.Controls.Add(_user, 1, 1);
            t.Controls.Add(new Label { Text = Txt.S("UiAdminPassword"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
            t.Controls.Add(_pwd, 1, 2);
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = Txt.S("UiCancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
            var b = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
            b.Controls.Add(cancel); b.Controls.Add(ok);
            t.Controls.Add(b, 0, 3);
            t.SetColumnSpan(b, 2);
            Controls.Add(t);
            AcceptButton = ok; CancelButton = cancel;
        }
    }
}
