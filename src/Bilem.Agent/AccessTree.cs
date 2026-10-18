using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Forms.VisualStyles;
using Bilem.Agent.Core;

namespace Bilem.Agent
{
    /// <summary>
    /// Дерево «Доступа»: разделы 1С и объекты, справа — две колонки галочек «Чтение» и «Запись».
    /// Галочка раздела ставит/снимает все объекты раздела; частичный выбор — третье состояние.
    /// Запись без чтения невозможна; закрытый раздел — с замком, галочки недоступны.
    /// </summary>
    internal sealed class AccessTree : UserControl
    {
        private readonly TreeView _tree = new TreeView
        {
            Dock = DockStyle.Fill, DrawMode = TreeViewDrawMode.OwnerDrawText, FullRowSelect = true, HideSelection = false,
            ShowNodeToolTips = true, ShowLines = false, BorderStyle = BorderStyle.None,
        };
        private readonly Panel _header = new Panel { Dock = DockStyle.Top };
        private AccessCatalog _cat = new AccessCatalog();
        private HashSet<string> _read = new HashSet<string>();
        private HashSet<string> _write = new HashSet<string>();

        /// <summary>Щёлкнули по замку закрытого раздела — показать, что даст облако.</summary>
        public event Action LockedClicked;
        public event Action Changed;

        public IReadOnlyCollection<string> Read => _read;
        public IReadOnlyCollection<string> Write => _write;

        private int ColW => LogicalToDeviceUnits(64);
        private int BoxSize => LogicalToDeviceUnits(13);
        private int ReadX => _tree.ClientSize.Width - ColW * 2;
        private int WriteX => _tree.ClientSize.Width - ColW;

        public AccessTree()
        {
            BorderStyle = BorderStyle.FixedSingle;
            _header.Height = LogicalToDeviceUnits(22);
            _header.Paint += PaintHeader;
            _tree.ItemHeight = LogicalToDeviceUnits(22);
            _tree.DrawNode += DrawNode;
            _tree.NodeMouseClick += NodeClick;
            _tree.KeyDown += KeyToggle;
            _tree.Resize += (s, e) => { _tree.Invalidate(); _header.Invalidate(); };
            // Стиль проводника: треугольники сворачивания разделов
            _tree.HandleCreated += (s, e) => { try { SetWindowTheme(_tree.Handle, "explorer", null); } catch { } };
            Controls.Add(_tree);
            Controls.Add(_header);
        }

        [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string appName, string idList);

        public void Load(AccessCatalog cat, IEnumerable<string> read, IEnumerable<string> write)
        {
            _cat = cat ?? new AccessCatalog();
            var n = Access.Normalize(_cat, read, write);
            _read = n.Read; _write = n.Write;
            var expanded = new HashSet<string>(_tree.Nodes.Cast<TreeNode>().Where(x => x.IsExpanded).Select(x => (string)x.Name));
            _tree.BeginUpdate();
            _tree.Nodes.Clear();
            foreach (var s in _cat.Sections)
            {
                var sn = new TreeNode { Name = s.Key, Tag = s };
                if (s.Locked) sn.ToolTipText = Txt.S("UiAccessLockedTip");
                foreach (var it in _cat.Items.Where(i => i.Section == s.Key).OrderBy(i => i.Title, StringComparer.CurrentCulture))
                    sn.Nodes.Add(new TreeNode(it.Title) { Name = it.FullName, Tag = it, ToolTipText = it.Locked || it.CanWrite ? it.FullName : it.FullName + "\n" + Txt.S("UiAccessWriteLockTip") });
                if (!s.Locked && !_cat.Items.Any(i => i.Section == s.Key && i.CanWrite)) sn.ToolTipText = Txt.S("UiAccessWriteLockTip");
                if (sn.Nodes.Count == 0) continue;
                _tree.Nodes.Add(sn);
                if (expanded.Contains(s.Key)) sn.Expand();
            }
            UpdateSectionTexts();
            _tree.EndUpdate();
        }

        public void Set(IEnumerable<string> read, IEnumerable<string> write)
        {
            var n = Access.Normalize(_cat, read, write);
            _read = n.Read; _write = n.Write;
            UpdateSectionTexts();
            _tree.Invalidate();
        }

        private void UpdateSectionTexts()
        {
            foreach (TreeNode sn in _tree.Nodes)
            {
                var s = (AccessSection)sn.Tag;
                var items = sn.Nodes.Cast<TreeNode>().Select(x => (AccessItem)x.Tag).ToList();
                var writable = items.Count(i => i.CanWrite);
                var text = s.Locked ? "🔒 " + s.Title
                    : writable == 0 ? Txt.F("UiAccessSectionCountRead", s.Title, items.Count(i => _read.Contains(i.FullName)), items.Count)
                    : Txt.F("UiAccessSectionCount", s.Title, items.Count(i => _read.Contains(i.FullName)), items.Count, items.Count(i => _write.Contains(i.FullName)), writable);
                if (sn.Text != text) sn.Text = text;
            }
        }

        // ---------- состояния ----------

        private enum Tri { Off, On, Mixed, Disabled }

        private Tri StateOf(TreeNode n, bool write)
        {
            if (n.Tag is AccessItem it)
            {
                if (it.Locked || (write && !it.CanWrite)) return Tri.Disabled;
                return (write ? _write : _read).Contains(it.FullName) ? Tri.On : Tri.Off;
            }
            var s = (AccessSection)n.Tag;
            if (s.Locked) return Tri.Disabled;
            var items = n.Nodes.Cast<TreeNode>().Select(x => (AccessItem)x.Tag).Where(i => !write || i.CanWrite).ToList();
            if (items.Count == 0) return Tri.Disabled;
            var on = items.Count(i => (write ? _write : _read).Contains(i.FullName));
            return on == 0 ? Tri.Off : on == items.Count ? Tri.On : Tri.Mixed;
        }

        private void Toggle(TreeNode n, bool write)
        {
            var st = StateOf(n, write);
            if (st == Tri.Disabled) return;
            var turnOn = st != Tri.On;
            var items = n.Tag is AccessItem one ? new List<AccessItem> { one } : n.Nodes.Cast<TreeNode>().Select(x => (AccessItem)x.Tag).ToList();
            foreach (var it in items)
            {
                if (it.Locked) continue;
                if (write)
                {
                    if (!it.CanWrite) continue;
                    // Запись ставит и чтение
                    if (turnOn) { _write.Add(it.FullName); _read.Add(it.FullName); } else _write.Remove(it.FullName);
                }
                else
                {
                    // Снятие чтения снимает и запись
                    if (turnOn) _read.Add(it.FullName); else { _read.Remove(it.FullName); _write.Remove(it.FullName); }
                }
            }
            UpdateSectionTexts();
            _tree.Invalidate();
            Changed?.Invoke();
        }

        private void NodeClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (e.Node.Tag is AccessSection s && s.Locked) { LockedClicked?.Invoke(); return; }
            if (e.X >= ReadX && e.X < WriteX) Toggle(e.Node, false);
            else if (e.X >= WriteX) Toggle(e.Node, true);
        }

        private void KeyToggle(object sender, KeyEventArgs e)
        {
            if (_tree.SelectedNode == null) return;
            if (e.KeyCode == Keys.Space) { Toggle(_tree.SelectedNode, e.Shift); e.Handled = e.SuppressKeyPress = true; }
        }

        // ---------- рисование ----------

        private void PaintHeader(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(SystemColors.Control);
            var off = _tree.Left;
            var f = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;
            TextRenderer.DrawText(g, Txt.S("UiAccessColRead"), Font, new Rectangle(off + ReadX, 0, ColW, _header.Height), SystemColors.ControlText, f);
            TextRenderer.DrawText(g, Txt.S("UiAccessColWrite"), Font, new Rectangle(off + WriteX, 0, ColW, _header.Height), SystemColors.ControlText, f);
            TextRenderer.DrawText(g, Txt.S("UiAccessColObject"), Font, new Rectangle(LogicalToDeviceUnits(6), 0, ReadX, _header.Height), SystemColors.ControlText, TextFormatFlags.VerticalCenter);
            using (var p = new Pen(SystemColors.ControlDark)) g.DrawLine(p, 0, _header.Height - 1, _header.Width, _header.Height - 1);
        }

        private void DrawNode(object sender, DrawTreeNodeEventArgs e)
        {
            var g = e.Graphics;
            // Фон — от текста вправо: левее система рисует треугольник сворачивания
            var row = new Rectangle(e.Bounds.Left, e.Bounds.Top, _tree.ClientSize.Width - e.Bounds.Left, e.Bounds.Height);
            var selected = (e.State & TreeNodeStates.Selected) != 0 && _tree.Focused;
            using (var bg = new SolidBrush(selected ? SystemColors.Highlight : SystemColors.Window)) g.FillRectangle(bg, row);
            var isSection = e.Node.Tag is AccessSection;
            var locked = (e.Node.Tag is AccessSection s && s.Locked) || (e.Node.Tag is AccessItem i && i.Locked);
            var color = selected ? SystemColors.HighlightText : locked ? SystemColors.GrayText : SystemColors.WindowText;
            var font = Font; // обычный шрифт, как в списке «Базы 1С»
            var textRect = new Rectangle(e.Bounds.Left, e.Bounds.Top, Math.Max(0, ReadX - e.Bounds.Left - LogicalToDeviceUnits(4)), e.Bounds.Height);
            TextRenderer.DrawText(g, e.Node.Text, font, textRect, color, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (locked && isSection)
            {
                // Метка «Облако» — у замка закрытого раздела
                var w = TextRenderer.MeasureText(e.Node.Text, font).Width;
                CloudBadge.Draw(g, Font, e.Bounds.Left + w + LogicalToDeviceUnits(6), e.Bounds.Top + (e.Bounds.Height - LogicalToDeviceUnits(16)) / 2, this);
            }
            DrawBox(g, e.Bounds.Top, e.Bounds.Height, ReadX, StateOf(e.Node, false));
            // Запись вне белого списка — замок вместо галочки (у закрытого раздела — серые галочки)
            var ws = StateOf(e.Node, true);
            if (ws == Tri.Disabled && !locked)
                TextRenderer.DrawText(g, "🔒", Font, new Rectangle(WriteX, e.Bounds.Top, ColW, e.Bounds.Height), SystemColors.GrayText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            else DrawBox(g, e.Bounds.Top, e.Bounds.Height, WriteX, ws);
        }

        private void DrawBox(Graphics g, int top, int h, int x, Tri st)
        {

            var cs = st == Tri.On ? CheckBoxState.CheckedNormal : st == Tri.Mixed ? CheckBoxState.MixedNormal
                : st == Tri.Disabled ? CheckBoxState.UncheckedDisabled : CheckBoxState.UncheckedNormal;
            var p = new Point(x + (ColW - BoxSize) / 2, top + (h - BoxSize) / 2);
            CheckBoxRenderer.DrawCheckBox(g, p, cs);
        }
    }

    /// <summary>Метка «Облако» — одинаковая везде, где видна платная возможность: маленький прямоугольник, серым.</summary>
    internal static class CloudBadge
    {
        public static Label Create(Control scale) => new Label
        {
            Text = Txt.S("UiCloudBadge"), AutoSize = true, BorderStyle = BorderStyle.FixedSingle, ForeColor = SystemColors.GrayText,
            Font = new Font(SystemFonts.MessageBoxFont.FontFamily, SystemFonts.MessageBoxFont.Size * 0.85f), Padding = new Padding(2, 0, 2, 0),
            Margin = new Padding(6, 3, 0, 0), Cursor = Cursors.Hand,
        };

        public static void Draw(Graphics g, Font baseFont, int x, int y, Control scale)
        {
            using (var f = new Font(baseFont.FontFamily, baseFont.Size * 0.85f))
            {
                var text = Txt.S("UiCloudBadge");
                var sz = TextRenderer.MeasureText(text, f);
                var r = new Rectangle(x, y, sz.Width + scale.LogicalToDeviceUnits(4), scale.LogicalToDeviceUnits(16));
                using (var p = new Pen(SystemColors.GrayText)) g.DrawRectangle(p, r);
                TextRenderer.DrawText(g, text, f, r, SystemColors.GrayText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }
}
