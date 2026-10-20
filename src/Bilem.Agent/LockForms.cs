using System;
using System.Drawing;
using System.Windows.Forms;
using Bilem.Agent.Core;

namespace Bilem.Agent
{
    /// <summary>Пароль настроек на входе: «Настройки…» и «Скопировать адрес MCP». Пароль не задан — сразу true.</summary>
    internal static class SettingsGate
    {
        public static bool Pass(string what)
        {
            try { if (!SettingsLock.Enabled) return true; }
            catch { return true; }   // настройки не читаются — дальше окно само скажет, что не так
            using (var f = new UnlockForm(what)) return f.ShowDialog() == DialogResult.OK;
        }
    }

    /// <summary>«Пароль настроек Bilem Agent»: одно поле; неверно — строка ошибки, после 5 попыток — пауза.</summary>
    internal sealed class UnlockForm : BilemForm
    {
        private readonly TextBox _pwd = new TextBox { Width = 260, UseSystemPasswordChar = true };
        private readonly Label _error = new Label { AutoSize = true, MaximumSize = new Size(380, 0) };

        public UnlockForm(string what)
        {
            Text = Txt.S("UiLockTitle");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            var t = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Padding = new Padding(12) };
            t.Controls.Add(new Label { Text = Txt.F("UiLockPrompt", what), AutoSize = true, MaximumSize = new Size(380, 0) });
            t.Controls.Add(_pwd);
            t.Controls.Add(_error);
            var ok = new Button { Text = "OK", AutoSize = true };
            var cancel = new Button { Text = Txt.S("UiCancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
            var b = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
            b.Controls.Add(cancel); b.Controls.Add(ok);
            t.Controls.Add(b);
            Controls.Add(t);
            AcceptButton = ok; CancelButton = cancel;
            ok.Click += (s, e) =>
            {
                var wait = SettingsLock.LockedFor();
                if (wait == null && SettingsLock.TryUnlock(_pwd.Text, what)) { DialogResult = DialogResult.OK; return; }
                wait = SettingsLock.LockedFor();
                _error.Text = wait != null ? Txt.F("UiLockWait", (int)Math.Ceiling(wait.Value.TotalSeconds)) : Txt.S("UiLockWrong");
                _pwd.SelectAll();
                _pwd.Focus();
            };
        }
    }

    /// <summary>Задать / сменить пароль настроек: новый и повтор (при смене — сначала текущий).</summary>
    internal sealed class SetLockForm : BilemForm
    {
        private readonly TextBox _current = new TextBox { Width = 220, UseSystemPasswordChar = true };
        private readonly TextBox _new = new TextBox { Width = 220, UseSystemPasswordChar = true };
        private readonly TextBox _repeat = new TextBox { Width = 220, UseSystemPasswordChar = true };
        private readonly Label _error = new Label { AutoSize = true, MaximumSize = new Size(380, 0) };

        public SetLockForm(bool change)
        {
            Text = Txt.S(change ? "UiLockChangeTitle" : "UiLockSetTitle");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Padding = new Padding(12) };
            var hint = new Label { Text = Txt.F("UiLockSetHint", SettingsLock.MinLength), AutoSize = true, MaximumSize = new Size(380, 0), Margin = new Padding(0, 0, 0, 8) };
            t.Controls.Add(hint, 0, 0); t.SetColumnSpan(hint, 2);
            var row = 1;
            void Row(string key, TextBox tb) { t.Controls.Add(new Label { Text = Txt.S(key), AutoSize = true, Anchor = AnchorStyles.Left }, 0, row); t.Controls.Add(tb, 1, row++); }
            if (change) Row("UiLockCurrent", _current);
            Row("UiLockNew", _new);
            Row("UiLockRepeat", _repeat);
            t.Controls.Add(_error, 0, row); t.SetColumnSpan(_error, 2); row++;
            var ok = new Button { Text = "OK", AutoSize = true };
            var cancel = new Button { Text = Txt.S("UiCancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
            var b = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
            b.Controls.Add(cancel); b.Controls.Add(ok);
            t.Controls.Add(b, 0, row); t.SetColumnSpan(b, 2);
            Controls.Add(t);
            AcceptButton = ok; CancelButton = cancel;
            ok.Click += (s, e) =>
            {
                try
                {
                    if (change && !SettingsLock.TryUnlock(_current.Text, Txt.S("UiLockWhatChange")))
                    {
                        var wait = SettingsLock.LockedFor();
                        _error.Text = wait != null ? Txt.F("UiLockWait", (int)Math.Ceiling(wait.Value.TotalSeconds)) : Txt.S("UiLockWrongCurrent");
                        return;
                    }
                    if (_new.Text != _repeat.Text) { _error.Text = Txt.S("UiLockMismatch"); return; }
                    SettingsLock.Set(_new.Text);
                    DialogResult = DialogResult.OK;
                }
                catch (AgentException ex) { _error.Text = ex.Message; }
            };
        }
    }
}
