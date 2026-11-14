using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Bilem.Agent.Core;

namespace Bilem.Agent
{
    /// <summary>
    /// Окно «Настройка базы» / «Проверка базы» (#97, 14.11): низкое и широкое, две колонки — шаги столбиком слева,
    /// живой журнал справа на всю высоту; под ними полоса прогресса, при ошибке — причина и что делать. Работа идёт в фоне, окно не зависает.
    /// </summary>
    internal sealed class RunForm : BilemForm
    {
        // Левая колонка — ~40% ширины, но не уже 280 px: названия шагов не обрезаются на 100–150%
        private const int StepsMinWidth = 280;
        private const double StepsShare = 0.4;
        private readonly TableLayoutPanel _split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 0, 0, 6) };
        // Шаги — строки-подписи сверху вниз (без растяжения): подробность после тире обрезается многоточием, полный текст — в подсказке
        private readonly Panel _stepsBox = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 0, 6, 0) };
        private readonly TableLayoutPanel _steps = new TableLayoutPanel
        {
            Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty, GrowStyle = TableLayoutPanelGrowStyle.AddRows,
        };
        private readonly TextBox _log = new TextBox { Multiline = true, WordWrap = true, ReadOnly = true, HideSelection = false, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), BackColor = SystemColors.Window, Margin = Padding.Empty };
        // Итог: при ошибке — на каком шаге, причина и что делать
        private readonly Label _result = new Label { AutoSize = true, Dock = DockStyle.Fill, Visible = false, Margin = new Padding(0, 0, 0, 6), Padding = new Padding(6) };
        // Полоса и «44%» — только там, где число шагов известно заранее (Onboarding.Planned)
        private readonly TableLayoutPanel _progress = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, AutoSize = true, Margin = new Padding(0, 0, 0, 6), Visible = false };
        private readonly ProgressBar _progressBar = new ProgressBar { Dock = DockStyle.Fill, Style = ProgressBarStyle.Continuous, Maximum = 100, Margin = Padding.Empty };
        private readonly Label _percent = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(6, 0, 0, 0), Text = "0%" };
        private readonly TableLayoutPanel _root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        private readonly ToolTip _tip = new ToolTip();
        private Onboarding _ob;
        private Font _bold;
        private bool _fitting;
        private readonly string _title;
        private readonly Button _close = new Button { Text = Txt.S("UiClose"), Enabled = false, AutoSize = true, Margin = Padding.Empty };
        // Когда закончилось — текст для поддержки и лог одним действием
        private readonly Button _copy = new Button { Text = Txt.S("UiCopySupport"), AutoSize = true, Visible = false, Margin = new Padding(0, 0, 6, 0) };
        private readonly Button _saveLog = new Button { Text = Txt.S("UiSaveLog"), AutoSize = true, Visible = false, Margin = new Padding(0, 0, 6, 0) };
        private readonly Timer _copied = new Timer { Interval = 2000 };
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
            _title = Text = title;
            StartPosition = FormStartPosition.CenterParent;

            _log.Resize += (s, e) => { if (_log.IsHandleCreated) ScrollLog(); };
            _steps.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _split.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Px(StepsMinWidth)));
            _split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _split.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _stepsBox.Controls.Add(_steps);
            _split.Controls.Add(_stepsBox, 0, 0);
            _split.Controls.Add(_log, 1, 0);
            _split.Resize += (s, e) => FitColumns();
            // Перенос строк в блоке ошибки пересчитывается при раскладке — после неё ещё раз проверяем, что шаги помещаются
            _stepsBox.Resize += (s, e) => { if (_result.Visible && IsHandleCreated) BeginInvoke((Action)FitSteps); };
            // Настройка: все шаги известны заранее и видны сразу
            if (logKind == "setup")
                foreach (var id in Onboarding.SetupSteps) ShowStep(new Step { Id = id, Title = Txt.S("Step_" + id), Status = StepStatus.Pending });

            _progress.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _progress.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _progress.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _progress.Controls.Add(_progressBar, 0, 0);
            _progress.Controls.Add(_percent, 1, 0);

            var bottom = new FlowLayoutPanel { Anchor = AnchorStyles.Right, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
            bottom.Controls.Add(_close);
            bottom.Controls.Add(_saveLog);
            bottom.Controls.Add(_copy);
            _close.Click += (s, e) => Close();
            _copy.Click += (s, e) => CopySupport();
            _saveLog.Click += (s, e) => SaveLog();
            _copied.Tick += (s, e) => { _copied.Stop(); _copy.Text = Txt.S("UiCopySupport"); };

            // Колонки шагов и журнала — единственная растягивающаяся строка; полоса, итог и кнопки — на всю ширину под ними
            _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var rows = new Control[] { _split, _progress, _result, bottom };
            _root.RowCount = rows.Length;
            for (int i = 0; i < rows.Length; i++)
            {
                _root.RowStyles.Add(rows[i] == _split ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
                _root.Controls.Add(rows[i], 0, i);
            }
            Controls.Add(_root);
            Shown += async (s, e) => await Start();
            ResizeEnd += (s, e) => WindowPlacement.SaveRunSize(this);
            FormClosing += (s, e) => { if (!_close.Enabled) e.Cancel = true; };
            FormClosed += (s, e) => { _copied.Dispose(); _tip.Dispose(); };
        }

        /// <summary>Размеры — здесь, в пикселях текущего экрана (см. AddBaseForm): окно собрано кодом, автомасштаб к нему не применяется.</summary>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _root.Padding = new Padding(Px(12));
            _progressBar.MinimumSize = new Size(0, Px(18));
            _percent.Font = new Font(Font, FontStyle.Bold);
            var area = Screen.FromControl(this).WorkingArea;
            FitMinimum();
            Size = new Size(Math.Min(Px(860), area.Width), Math.Min(Px(400), area.Height));
            WindowPlacement.RestoreRunSize(this);
            var around = StartPosition == FormStartPosition.CenterParent && Owner != null ? Owner.Bounds : area;
            var x = around.Left + (around.Width - Width) / 2;
            var y = around.Top + (around.Height - Height) / 2;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(Math.Max(area.Left, Math.Min(x, area.Right - Width)), Math.Max(area.Top, Math.Min(y, area.Bottom - Height)));
            FitColumns();
            FitSteps();
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            FitMinimum();
            FitColumns();
            FitSteps();
        }

        private void FitMinimum()
        {
            var area = Screen.FromControl(this).WorkingArea;
            MinimumSize = new Size(Math.Min(Px(720), area.Width), Math.Min(Px(340), area.Height));
        }

        /// <summary>Левая колонка — 40% ширины, не уже 280 px.</summary>
        private void FitColumns()
        {
            if (_split.ColumnStyles.Count < 2) return;
            var w = Math.Max(Px(StepsMinWidth) + _stepsBox.Margin.Horizontal, (int)(_split.ClientSize.Width * StepsShare));
            if ((int)_split.ColumnStyles[0].Width != w) _split.ColumnStyles[0].Width = w;
        }

        private int StepHeight => Font.Height + Px(6);

        /// <summary>Строки шагов — по высоте шрифта; если блок ошибки отнял высоту и шаги не помещаются, окно подрастает.</summary>
        private void FitSteps()
        {
            var h = StepHeight;
            for (var i = 0; i < _steps.RowStyles.Count; i++)
                if ((int)_steps.RowStyles[i].Height != h) _steps.RowStyles[i].Height = h;
            if (_fitting || !IsHandleCreated || !_result.Visible) return;
            _fitting = true;
            try
            {
                _root.PerformLayout();
                var need = _steps.RowStyles.Count * h - _stepsBox.ClientSize.Height;
                if (need <= 0) return;
                var area = Screen.FromControl(this).WorkingArea;
                var height = Math.Min(Height + need, area.Height);
                if (height <= Height) return;
                MinimumSize = new Size(MinimumSize.Width, Math.Max(MinimumSize.Height, height));
                Height = height;
                if (Bottom > area.Bottom) Top = Math.Max(area.Top, area.Bottom - Height);
            }
            finally { _fitting = false; }
        }

        private async Task Start()
        {
            _started = DateTime.Now;
            var ob = _ob = new Onboarding();
            ob.StepChanged += st => BeginInvoke((Action)(() => { ShowStep(st); ShowProgress(null); }));
            Action<LogEntry> onLog = en => BeginInvoke((Action)(() => { _log.AppendText(en + Environment.NewLine); ScrollLog(); }));
            Log.Written += onLog;
            try { Success = await Task.Run(() => _work(ob)); }
            finally { Log.Written -= onLog; }
            ShowProgress(Success);
            ShowResult();
            _copy.Visible = _saveLog.Visible = true;
            _copy.MinimumSize = _copy.Size;
            _close.Enabled = true;
            AcceptButton = _copy;
            _copy.Focus();
        }

        /// <summary>К концу журнала: WM_VSCROLL/SB_BOTTOM. ScrollToCaret не прокручивает поле без фокуса.</summary>
        private void ScrollLog() => SendMessage(_log.Handle, 0x115, (IntPtr)7, IntPtr.Zero);

        private void ShowResult()
        {
            var p = StepProgress.Of(_ob.Steps.ToArray(), _ob.Planned, Success);
            _result.ForeColor = Success ? Color.DarkGreen : Color.DarkRed;
            _result.BackColor = Success ? SystemColors.Control : Color.FromArgb(255, 240, 240);
            _result.Text = Success ? Txt.S("UiRunOk")
                : p.FailedStep != null ? RunLog.Stopped(p.Current, p.FailedStep.Title, _ob.FailMessage ?? p.FailedStep.Detail ?? Txt.S("UiRunFail"), _ob.FailHint ?? Txt.S("HintCopySupport"))
                : RunLog.Short(RunLog.Mask(_ob.FailMessage ?? Txt.S("UiRunFail"))) + Environment.NewLine + Txt.S("WhatToDo") + " " + RunLog.Short(RunLog.Mask(_ob.FailHint ?? Txt.S("HintCopySupport")));
            // Перенос — по ширине окна, а не по самой длинной строке причины
            _result.MaximumSize = new Size(Math.Max(Px(200), _root.ClientSize.Width - _root.Padding.Horizontal), 0);
            _result.Visible = true;
            FitSteps();
        }

        /// <summary>Строка шага: «✓ Права Windows», «▸ Пользователь bilem», «· Состав OData», «✗ … — причина»; подробность — после тире.</summary>
        internal static string StepLine(Step st)
        {
            string mark;
            switch (st.Status)
            {
                case StepStatus.Ok: mark = "✓"; break;
                case StepStatus.Fail: mark = "✗"; break;
                case StepStatus.Warn: mark = "!"; break;
                case StepStatus.Running: mark = "▸"; break;
                case StepStatus.Skipped: mark = "–"; break;
                default: mark = "·"; break;
            }
            var detail = !string.IsNullOrEmpty(st.Detail) ? st.Detail : st.Status == StepStatus.Skipped ? Txt.S("UiStep_Skipped") : null;
            return mark + " " + st.Title + (detail == null ? "" : " — " + detail);
        }

        private void ShowStep(Step st)
        {
            Label row = null;
            foreach (Control x in _steps.Controls) if ((string)x.Tag == st.Id) row = (Label)x;
            if (row == null)
            {
                row = new Label { Tag = st.Id, AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty, Padding = new Padding(Px(4), 0, Px(4), 0), UseMnemonic = false };
                _steps.RowStyles.Add(new RowStyle(SizeType.Absolute, StepHeight));
                _steps.RowCount = _steps.RowStyles.Count;
                _steps.Controls.Add(row, 0, _steps.RowCount - 1);
            }
            // Текущий — жирным, ожидает — серым, упал — красным
            var color = SystemColors.ControlText;
            switch (st.Status)
            {
                case StepStatus.Ok: color = Color.DarkGreen; break;
                case StepStatus.Fail: color = Color.DarkRed; break;
                case StepStatus.Warn: color = Color.DarkGoldenrod; break;
                case StepStatus.Running: break;
                default: color = SystemColors.GrayText; break;
            }
            if (_bold == null || _bold.Size != Font.Size) _bold = new Font(Font, FontStyle.Bold);
            row.ForeColor = color;
            row.Font = st.Status == StepStatus.Running ? _bold : null;
            row.Text = StepLine(st);
            _tip.SetToolTip(row, row.Text);
            FitSteps();
        }

        /// <param name="finished">null — идёт, true/false — закончилось успешно или с ошибкой.</param>
        private void ShowProgress(bool? finished)
        {
            if (_ob == null || _ob.Planned == 0) return;
            var p = StepProgress.Of(_ob.Steps.ToArray(), _ob.Planned, finished);
            _progress.Visible = true;
            _percent.Text = p.Percent + "%";
            _percent.ForeColor = p.Failed ? Color.DarkRed : p.Done == p.Total ? Color.DarkGreen : SystemColors.ControlText;
            _progressBar.Value = p.Percent;
            // Красная полоса: у ProgressBar с темой Windows цвет не задать, только состояние Error (PBM_SETSTATE)
            SendMessage(_progressBar.Handle, 0x410, (IntPtr)(p.Failed ? 2 : 1), IntPtr.Zero);
            // «Настройка базы «demo» · шаг 5 из 9»
            Text = _title + " · " + Txt.F("UiTitleStep", p.Current, p.Total);
        }

        private string ProgressText() => _ob != null && _ob.Planned > 0 ? StepProgress.Of(_ob.Steps.ToArray(), _ob.Planned, _close.Enabled ? Success : (bool?)null).Text() : null;

        private string LogText() =>
            RunLog.Build(_title, _slug, _started, _ob?.Steps.ToArray() ?? new Step[0], ProgressText(), _result.Text, _log.Text);

        private string SupportText()
        {
            var steps = _ob.Steps.ToArray();
            var p = StepProgress.Of(steps, _ob.Planned, Success);
            var facts = _ob.Facts.Name != null ? _ob.Facts : RunFacts.From(AgentConfig.Load().Find(_slug));
            facts.Name = facts.Name ?? _slug;
            return RunLog.Support(new SupportInfo
            {
                Title = _title, ScalePercent = DeviceDpi * 100 / 96, Base = facts, Steps = steps, Log = _log.Text,
                Progress = _ob.Planned > 0 ? p.Text() : null,
                FailedStep = !Success && p.FailedStep != null ? p.Current : 0, FailedTitle = p.FailedStep?.Title,
                Error = _ob.FailMessage ?? p.FailedStep?.Detail, Hint = _ob.FailHint,
            });
        }

        private void CopySupport()
        {
            try { Clipboard.SetText(SupportText()); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            _copy.Text = Txt.S("UiCopied");
            _copied.Stop();
            _copied.Start();
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
