using System;
using System.Drawing;
using System.Windows.Forms;

namespace Bilem.Agent
{
    /// <summary>
    /// Базовое окно Bilem Agent: иконка приложения (из ресурса целиком — все размеры, Windows берёт нужный под масштаб),
    /// системный шрифт, масштабирование под DPI. От него наследуются все окна и диалоги (проверяется тестом).
    /// </summary>
    internal class BilemForm : Form
    {
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
        protected void Wrap(Label l, Control host, int pad)
        {
            void Fit() { var w = host.ClientSize.Width - Px(pad); if (w > Px(100)) l.MaximumSize = new Size(w, 0); }
            host.Resize += (s, e) => Fit();
            host.HandleCreated += (s, e) => Fit();
            Fit();
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
