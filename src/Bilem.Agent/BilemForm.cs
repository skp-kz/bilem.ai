using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Bilem.Agent
{
    /// <summary>
    /// Базовое окно Bilem Agent: иконка приложения (из ресурса целиком — все размеры, Windows берёт нужный под масштаб),
    /// системный шрифт, масштабирование под DPI. От него наследуются все окна и диалоги (проверяется тестом).
    /// </summary>
    internal class BilemForm : Form
    {
        private bool _fittingColumns;

        public BilemForm()
        {
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            Icon = AppIcon;
        }

        /// <summary>Иконка приложения — из встроенного ресурса целиком (не Icon.ExtractAssociatedIcon: он отдаёт только 32 px).</summary>
        internal static Icon AppIcon => LoadIcon("bilem-agent.ico");

        internal static Icon LoadIcon(string name)
        {
            using (var s = typeof(BilemForm).Assembly.GetManifestResourceStream(name))
                return s == null ? SystemIcons.Application : new Icon(s);
        }

        internal static Icon LoadIcon(string name, Size size)
        {
            using (var s = typeof(BilemForm).Assembly.GetManifestResourceStream(name))
                return s == null ? SystemIcons.Application : new Icon(s, size);
        }

        /// <summary>Логические пиксели (96 dpi) → пиксели устройства: для ширины колонок и ограничений, которые WinForms сам не масштабирует.</summary>
        protected int Px(int logical) => LogicalToDeviceUnits(logical);

        /// <summary>Надпись переносится по словам по ширине контейнера (pad — отступы справа и слева, логические px).</summary>
        protected void Wrap(Control l, Control host, int pad)
        {
            void Fit() { var w = host.ClientSize.Width - Px(pad); if (w > Px(100)) l.MaximumSize = new Size(w, 0); }
            host.Resize += (s, e) => Fit();
            host.HandleCreated += (s, e) => Fit();
            Fit();
        }

        /// <summary>Колонки — по содержимому, но не шире половины списка; колонка fill берёт остаток. Без горизонтальной прокрутки, полный текст — в подсказке.</summary>
        internal void FitContent(ListView lv, int fill)
        {
            // Смена ширины колонок включает и выключает полосы прокрутки, а это снова Resize списка: без защиты окно крутится и не показывается
            if (_fittingColumns || !lv.IsHandleCreated || lv.Columns.Count == 0) return;
            _fittingColumns = true;
            try
            {
                for (int i = 0; i < lv.Columns.Count; i++)
                {
                    if (i == fill) continue;
                    lv.AutoResizeColumn(i, ColumnHeaderAutoResizeStyle.HeaderSize);
                    var header = lv.Columns[i].Width;
                    if (lv.Items.Count > 0) lv.AutoResizeColumn(i, ColumnHeaderAutoResizeStyle.ColumnContent);
                    // Запас: ширины «по содержимому» впритык хватает не всегда, и текст обрезается многоточием
                    lv.Columns[i].Width = Math.Min(Math.Max(lv.Columns[i].Width + Px(12), header), lv.ClientSize.Width / 2);
                }
                // Ширину списка берём после подгонки: полоса прокрутки могла её изменить
                var others = lv.Columns.Cast<ColumnHeader>().Where(c => c.Index != fill).ToList();
                var widths = others.Select(c => c.Width).ToArray();
                var rest = lv.ClientSize.Width - widths.Sum();
                // Узкое окно: сжимается самая широкая колонка, пока не останется место под fill. Считаем в памяти, колонкам — один раз
                var min = Px(80);
                while (rest < min && widths.Length > 0 && widths.Max() > min)
                {
                    widths[Array.IndexOf(widths, widths.Max())]--;
                    rest++;
                }
                for (int i = 0; i < others.Count; i++)
                    if (others[i].Width != widths[i]) others[i].Width = widths[i];
                lv.Columns[fill].Width = Math.Max(min, rest);
            }
            finally { _fittingColumns = false; }
        }

        /// <summary>Последняя колонка списка занимает оставшуюся ширину — без горизонтальной прокрутки в узком окне.</summary>
        protected void FillLastColumn(ListView lv)
        {
            void Fit()
            {
                if (lv.Columns.Count == 0) return;
                var rest = lv.ClientSize.Width;
                for (int i = 0; i < lv.Columns.Count - 1; i++) rest -= lv.Columns[i].Width;
                lv.Columns[lv.Columns.Count - 1].Width = Math.Max(Px(80), rest);
            }
            lv.Resize += (s, e) => Fit();
            lv.HandleCreated += (s, e) => Fit();
        }
    }
}
