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
        private const int StepsRow = 2;
        // Таблица, а не ListView: ListView не переносит текст в ячейке. Высота — ровно по строкам, прокрутки нет
        private readonly DataGridView _steps = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, RowHeadersVisible = false, ScrollBars = ScrollBars.None, TabStop = false, MultiSelect = false,
            AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, AllowUserToResizeColumns = false, AllowUserToOrderColumns = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, Margin = new Padding(0, 0, 0, 6),
        };
        private readonly TextBox _log = new TextBox { Multiline = true, ReadOnly = true, HideSelection = false, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), BackColor = SystemColors.Window, Margin = new Padding(0, 0, 0, 6) };
        // Итог: при ошибке — на каком шаге, причина и что делать
        private readonly Label _result = new Label { AutoSize = true, Dock = DockStyle.Fill, Visible = false, Margin = new Padding(0, 0, 0, 6), Padding = new Padding(6) };
        // «Шаг 5 из 9 · 44%» и полоса — только там, где число шагов известно заранее (Onboarding.Planned)
        private readonly Label _progressText = new Label { AutoSize = true, Dock = DockStyle.Fill, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4), Visible = false };
        private readonly ProgressBar _progressBar = new ProgressBar { Dock = DockStyle.Top, Style = ProgressBarStyle.Continuous, Maximum = 100, Margin = new Padding(0, 0, 0, 6), Visible = false };
        private readonly TableLayoutPanel _root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        private Onboarding _ob;
        private Font _bold;
        private bool _fitting;
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
            Text = title;
            StartPosition = FormStartPosition.CenterParent;

            _steps.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            _steps.DefaultCellStyle.SelectionBackColor = SystemColors.Window;
            _steps.DefaultCellStyle.SelectionForeColor = SystemColors.ControlText;
            _steps.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = Txt.S("UiStepColumn"), FillWeight = 42, SortMode = DataGridViewColumnSortMode.NotSortable });
            _steps.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = Txt.S("UiStatusColumn"), FillWeight = 58, SortMode = DataGridViewColumnSortMode.NotSortable });
            _log.Resize += (s, e) => { if (_log.IsHandleCreated) ScrollLog(); };
            _steps.SelectionChanged += (s, e) => _steps.ClearSelection();
            _steps.RowHeightChanged += (s, e) => FitSteps();
            // После того как таблица пересчитает высоту строк под новую ширину
            _steps.Resize += (s, e) => { if (IsHandleCreated) BeginInvoke((Action)FitSteps); };
            // Настройка: все шаги известны заранее и видны сразу
            if (logKind == "setup")
                foreach (var id in Onboarding.SetupSteps) ShowStep(new Step { Id = id, Title = Txt.S("Step_" + id), Status = StepStatus.Pending });

            var bottom = new FlowLayoutPanel { Anchor = AnchorStyles.Right, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
            bottom.Controls.Add(_close);
            bottom.Controls.Add(_saveLog);
            bottom.Controls.Add(_copy);
            _close.Click += (s, e) => Close();
            _copy.Click += (s, e) => CopySupport();
            _saveLog.Click += (s, e) => SaveLog();
            _copied.Tick += (s, e) => { _copied.Stop(); _copy.Text = Txt.S("UiCopySupport"); };

            _progressBar.Height = Px(18);
            // Одна колонка, журнал — единственная растягивающаяся строка
            _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var rows = new Control[] { _progressText, _progressBar, _steps, _log, _result, bottom };
            _root.RowCount = rows.Length;
            for (int i = 0; i < rows.Length; i++)
            {
                _root.RowStyles.Add(rows[i] == _log ? new RowStyle(SizeType.Percent, 100) : i == StepsRow ? new RowStyle(SizeType.Absolute, Px(100)) : new RowStyle(SizeType.AutoSize));
                _root.Controls.Add(rows[i], 0, i);
            }
            Controls.Add(_root);
            Shown += async (s, e) => await Start();
            ResizeEnd += (s, e) => WindowPlacement.SaveRunSize(this);
            FormClosing += (s, e) => { if (!_close.Enabled) e.Cancel = true; };
            FormClosed += (s, e) => _copied.Dispose();
        }

        /// <summary>Размеры — здесь, в пикселях текущего экрана (см. AddBaseForm): окно собрано кодом, автомасштаб к нему не применяется.</summary>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _root.Padding = new Padding(Px(12));
            var area = Screen.FromControl(this).WorkingArea;
            FitMinimum();
            Size = new Size(Math.Min(Px(820), area.Width), Math.Min(Px(640), area.Height));
            WindowPlacement.RestoreRunSize(this);
            var around = StartPosition == FormStartPosition.CenterParent && Owner != null ? Owner.Bounds : area;
            var x = around.Left + (around.Width - Width) / 2;
            var y = around.Top + (around.Height - Height) / 2;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(Math.Max(area.Left, Math.Min(x, area.Right - Width)), Math.Max(area.Top, Math.Min(y, area.Bottom - Height)));
            FitSteps();
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            FitMinimum();
        }

        private void FitMinimum()
        {
            var area = Screen.FromControl(this).WorkingArea;
            MinimumSize = new Size(Math.Min(Px(720), area.Width), Math.Min(Px(560), area.Height));
        }

        /// <summary>Высота таблицы — по её строкам: все шаги видны, журнал берёт остальное.</summary>
        private void FitSteps()
        {
            if (_fitting || _root.RowStyles.Count <= StepsRow) return;
            _fitting = true;
            try
            {
                // Высота строк пересчитывается лениво; без этого таблица остаётся по старой ширине колонок
                if (IsHandleCreated) _steps.AutoResizeRows(DataGridViewAutoSizeRowsMode.AllCells);
                var h = _steps.ColumnHeadersHeight + _steps.Rows.GetRowsHeight(DataGridViewElementStates.Visible) + _steps.Margin.Vertical + 2;
                if ((int)_root.RowStyles[StepsRow].Height != h) _root.RowStyles[StepsRow].Height = h;
                if (_result.Visible) KeepLogLines();
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
            _result.Visible = true;
            FitSteps();
        }

        /// <summary>Блок ошибки и перенос строк забирают высоту у журнала: окно и его минимум подрастают, чтобы в журнале осталось 10 строк.</summary>
        private void KeepLogLines()
        {
            _root.PerformLayout();
            var need = Height + 10 * _log.Font.Height - _log.ClientSize.Height;
            var area = Screen.FromControl(this).WorkingArea;
            if (need > MinimumSize.Height) MinimumSize = new Size(MinimumSize.Width, Math.Min(need, area.Height));
        }

        private void ShowStep(Step st)
        {
            DataGridViewRow row = null;
            foreach (DataGridViewRow x in _steps.Rows) if ((string)x.Tag == st.Id) row = x;
            if (row == null) { row = _steps.Rows[_steps.Rows.Add()]; row.Tag = st.Id; }
            string mark;
            var color = SystemColors.ControlText;
            switch (st.Status)
            {
                case StepStatus.Ok: mark = "✔ "; color = Color.DarkGreen; break;
                case StepStatus.Fail: mark = "✗ "; color = Color.DarkRed; break;
                case StepStatus.Warn: mark = "! "; color = Color.DarkGoldenrod; break;
                case StepStatus.Running: mark = "▶ "; break;
                case StepStatus.Skipped: mark = "– "; color = SystemColors.GrayText; break;
                default: mark = ""; color = SystemColors.GrayText; break;
            }
            if (_bold == null || _bold.Size != _steps.Font.Size) _bold = new Font(_steps.Font, FontStyle.Bold);
            row.DefaultCellStyle.ForeColor = row.DefaultCellStyle.SelectionForeColor = color;
            row.DefaultCellStyle.Font = st.Status == StepStatus.Running ? _bold : null;
            row.Cells[0].Value = mark + st.Title;
            row.Cells[1].Value = Txt.S("UiStep_" + st.Status) + (string.IsNullOrEmpty(st.Detail) ? "" : " — " + st.Detail);
            FitSteps();
        }

        /// <param name="finished">null — идёт, true/false — закончилось успешно или с ошибкой.</param>
        private void ShowProgress(bool? finished)
        {
            if (_ob == null || _ob.Planned == 0) return;
            var p = StepProgress.Of(_ob.Steps.ToArray(), _ob.Planned, finished);
            _progressText.Visible = _progressBar.Visible = true;
            _progressText.Text = p.Text();
            _progressText.ForeColor = p.Failed ? Color.DarkRed : p.Done == p.Total ? Color.DarkGreen : SystemColors.ControlText;
            _progressBar.Value = p.Percent;
            // Красная полоса: у ProgressBar с темой Windows цвет не задать, только состояние Error (PBM_SETSTATE)
            SendMessage(_progressBar.Handle, 0x410, (IntPtr)(p.Failed ? 2 : 1), IntPtr.Zero);
        }

        private string LogText() =>
            RunLog.Build(Text, _slug, _started, _ob?.Steps.ToArray() ?? new Step[0], _progressText.Visible ? _progressText.Text : null, _result.Text, _log.Text);

        private string SupportText()
        {
            var steps = _ob.Steps.ToArray();
            var p = StepProgress.Of(steps, _ob.Planned, Success);
            var facts = _ob.Facts.Name != null ? _ob.Facts : RunFacts.From(AgentConfig.Load().Find(_slug));
            facts.Name = facts.Name ?? _slug;
            return RunLog.Support(new SupportInfo
            {
                Title = Text, ScalePercent = DeviceDpi * 100 / 96, Base = facts, Steps = steps, Log = _log.Text,
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
