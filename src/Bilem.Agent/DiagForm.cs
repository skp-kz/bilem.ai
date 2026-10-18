using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Bilem.Agent.Core;

namespace Bilem.Agent
{
    /// <summary>«Диагностика компонентов»: чек-лист всего стека — зелёное, жёлтое, красное и что делать. Ничего не меняет.</summary>
    internal sealed class DiagForm : BilemForm
    {
        private readonly ListView _list = new ListView { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, ShowItemToolTips = true };
        private readonly Label _summary = new Label { AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        private readonly Button _copy = new Button { Text = Txt.S("UiDiagCopy"), AutoSize = true, Enabled = false };
        private readonly Button _again = new Button { Text = Txt.S("UiDiagAgain"), AutoSize = true, Enabled = false };
        private readonly bool _mcpHere;
        private List<CheckResult> _results = new List<CheckResult>();

        public DiagForm(bool mcpRunningHere)
        {
            _mcpHere = mcpRunningHere;
            Text = Txt.S("UiDiagTitle");
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1000, 640);
            MinimumSize = new Size(700, 420);
            _list.Columns.Add(Txt.S("UiDiagColCheck"), Px(330));
            _list.Columns.Add(Txt.S("UiDiagColResult"), Px(280));
            _list.Columns.Add(Txt.S("UiDiagColHint"), Px(360));
            _list.Groups.Clear();

            var close = new Button { Text = Txt.S("UiClose"), AutoSize = true, DialogResult = DialogResult.Cancel };
            _copy.Click += (s, e) => { Clipboard.SetText(Diagnostics.Report(_results)); MessageBox.Show(this, Txt.S("UiDiagCopied"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information); };
            _again.Click += async (s, e) => await RunChecks();
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
            bar.Controls.AddRange(new Control[] { close, _copy, _again, _summary });
            var note = new Label { Text = Txt.S("UiDiagNote"), Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8), MaximumSize = new Size(960, 0), ForeColor = SystemColors.GrayText };
            Controls.Add(_list);
            Controls.Add(note);
            Controls.Add(bar);
            CancelButton = close;
            Shown += async (s, e) => await RunChecks();
        }

        private async Task RunChecks()
        {
            _copy.Enabled = _again.Enabled = false;
            _summary.Text = Txt.S("UiDiagRunning");
            _summary.ForeColor = SystemColors.ControlText;
            _list.Items.Clear();
            _results = await Task.Run(() => Diagnostics.Run(_mcpHere));
            var groups = new Dictionary<string, ListViewGroup>();
            foreach (var c in _results)
            {
                if (!groups.TryGetValue(c.Group, out var g)) { g = new ListViewGroup(c.Group); groups[c.Group] = g; _list.Groups.Add(g); }
                var mark = c.Status == CheckStatus.Ok ? "✔ " : c.Status == CheckStatus.Fail ? "✖ " : c.Status == CheckStatus.Warn ? "! " : "· ";
                var it = new ListViewItem(mark + c.Name, g);
                it.SubItems.Add(Connectors.Mask(c.Detail ?? ""));
                it.SubItems.Add(c.Status == CheckStatus.Ok ? "" : (c.Hint ?? ""));
                it.ToolTipText = c.Hint;
                it.ForeColor = c.Status == CheckStatus.Ok ? Color.DarkGreen : c.Status == CheckStatus.Fail ? Color.DarkRed : c.Status == CheckStatus.Warn ? Color.DarkGoldenrod : SystemColors.GrayText;
                _list.Items.Add(it);
            }
            var fails = _results.Count(x => x.Status == CheckStatus.Fail);
            var warns = _results.Count(x => x.Status == CheckStatus.Warn);
            _summary.Text = Txt.F("DiagSummary", _results.Count(x => x.Status == CheckStatus.Ok), warns, fails);
            _summary.ForeColor = fails > 0 ? Color.DarkRed : warns > 0 ? Color.DarkGoldenrod : Color.DarkGreen;
            _copy.Enabled = _again.Enabled = true;
        }
    }
}
