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
        // «Шаг 5 из 9 · 44%» и полоса — только там, где число шагов известно заранее (Onboarding.Planned)
        private readonly Label _progressText = new Label { AutoSize = true, Dock = DockStyle.Top, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Padding = new Padding(0, 0, 0, 4) };
        private readonly ProgressBar _progressBar = new ProgressBar { Dock = DockStyle.Top, Height = 18, Style = ProgressBarStyle.Continuous, Maximum = 100 };
        private readonly Panel _progress = new Panel { Dock = DockStyle.Top, Padding = new Padding(8, 8, 8, 6), Visible = false };
        private Onboarding _ob;
        private readonly Button _close = new Button { Text = Txt.S("UiClose"), Enabled = false, AutoSize = true };
        // При ошибке — лог запуска одним действием для поддержки
        private readonly Button _copyLog = new Button { Text = Txt.S("UiCopyLog"), AutoSize = true, Visible = false };
        private readonly Button _saveLog = new Button { Text = Txt.S("UiSaveLog"), AutoSize = true, Visible = false };
        private readonly string _logKind, _slug;
        private DateTime _started;
        private readonly Func<Onboarding, bool> _work;
        public bool Success { get; private set; }

        /// <param name="logKind">Для имени файла лога: setup, verify, remove, access.</param>
        public RunForm(string title, Func<Onboarding, bool> work, string logKind = "run", string slug = null)
        {
            _work = work;
            _logKind = logKind;
            _slug = slug;
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
            bottom.Controls.Add(_saveLog);
            bottom.Controls.Add(_copyLog);
            bottom.Controls.Add(_result);
            _close.Click += (s, e) => Close();
            _copyLog.Click += (s, e) => CopyLog();
            _saveLog.Click += (s, e) => SaveLog();

            _progress.Height = Px(58);
            _progress.Controls.Add(_progressBar);
            _progress.Controls.Add(_progressText);

            Controls.Add(split);
            Controls.Add(_progress);
            Controls.Add(bottom);
            Shown += async (s, e) => await Start();
            FormClosing += (s, e) => { if (!_close.Enabled) e.Cancel = true; };
        }

        private async Task Start()
        {
            _started = DateTime.Now;
            var ob = _ob = new Onboarding();
            ob.StepChanged += st => BeginInvoke((Action)(() => { ShowStep(st); ShowProgress(null); }));
            Action<LogEntry> onLog = en => BeginInvoke((Action)(() => _log.AppendText(en + Environment.NewLine)));
            Log.Written += onLog;
            try { Success = await Task.Run(() => _work(ob)); }
            finally { Log.Written -= onLog; }
            _result.Text = Success ? Txt.S("UiRunOk") : Txt.S("UiRunFail");
            _result.ForeColor = Success ? Color.DarkGreen : Color.DarkRed;
            ShowProgress(Success);
            _copyLog.Visible = _saveLog.Visible = !Success;
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

        /// <param name="finished">null — идёт, true/false — закончилось успешно или с ошибкой.</param>
        private void ShowProgress(bool? finished)
        {
            if (_ob == null || _ob.Planned == 0) return;
            var p = StepProgress.Of(_ob.Steps.ToArray(), _ob.Planned, finished);
            _progress.Visible = true;
            _progressText.Text = p.Text();
            _progressText.ForeColor = p.Failed ? Color.DarkRed : p.Done == p.Total ? Color.DarkGreen : SystemColors.ControlText;
            _progressBar.Value = p.Percent;
            // Красная полоса: у ProgressBar с темой Windows цвет не задать, только состояние Error (PBM_SETSTATE)
            SendMessage(_progressBar.Handle, 0x410, (IntPtr)(p.Failed ? 2 : 1), IntPtr.Zero);
        }

        private string LogText() =>
            RunLog.Build(Text, _slug, _started, _ob?.Steps.ToArray() ?? new Step[0], _progress.Visible ? _progressText.Text : null, _result.Text, _log.Text);

        private void CopyLog()
        {
            try { Clipboard.SetText(LogText()); MessageBox.Show(this, Txt.S("UiLogCopied"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private void SaveLog()
        {
            using (var d = new SaveFileDialog { FileName = RunLog.FileName(_logKind, _slug, _started), Filter = Txt.S("UiLogFilter"), InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    System.IO.File.WriteAllText(d.FileName, LogText(), new System.Text.UTF8Encoding(true));
                    MessageBox.Show(this, Txt.F("UiLogSaved", d.FileName), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
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
