using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace Bilem.Agent
{
    /// <summary>
    /// Размер и положение главного окна — настройка пользователя Windows (%LOCALAPPDATA%\Bilem\window.json), не config.json.
    /// Запоминаем только то, что пользователь изменил сам; окно за пределами экранов — на основной экран.
    /// </summary>
    internal static class WindowPlacement
    {
        /// <summary>Размер по умолчанию и минимальный — в логических пикселях (96 dpi), масштабируются под экран.</summary>
        public static readonly Size DefaultSize = new Size(640, 440);
        public static readonly Size MinSize = new Size(560, 380);

        private sealed class Saved
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public bool Maximized { get; set; }
            public int Dpi { get; set; }
        }

        private static string File => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bilem", "window.json");

        /// <summary>Вернуть сохранённое положение, если оно есть и видно на каком-нибудь экране.</summary>
        public static bool TryRestore(Form f)
        {
            try
            {
                if (!System.IO.File.Exists(File)) return false;
                var s = JsonConvert.DeserializeObject<Saved>(System.IO.File.ReadAllText(File));
                if (s == null || s.Width <= 0 || s.Height <= 0) return false;
                var dpi = f.DeviceDpi > 0 ? f.DeviceDpi : 96;
                var scale = s.Dpi > 0 ? (double)dpi / s.Dpi : 1.0;
                var r = new Rectangle(s.X, s.Y, (int)(s.Width * scale), (int)(s.Height * scale));
                r.Width = Math.Max(r.Width, f.MinimumSize.Width);
                r.Height = Math.Max(r.Height, f.MinimumSize.Height);
                if (!IsVisibleOnAnyScreen(r)) return false;
                f.StartPosition = FormStartPosition.Manual;
                f.Bounds = r;
                if (s.Maximized) f.WindowState = FormWindowState.Maximized;
                return true;
            }
            catch { return false; }
        }

        public static void Save(Form f)
        {
            try
            {
                var b = f.WindowState == FormWindowState.Normal ? f.Bounds : f.RestoreBounds;
                var s = new Saved { X = b.X, Y = b.Y, Width = b.Width, Height = b.Height, Maximized = f.WindowState == FormWindowState.Maximized, Dpi = f.DeviceDpi };
                Directory.CreateDirectory(Path.GetDirectoryName(File));
                System.IO.File.WriteAllText(File, JsonConvert.SerializeObject(s));
            }
            catch { }
        }

        /// <summary>Сброс — к размеру по умолчанию, по центру основного экрана.</summary>
        public static void Reset(Form f)
        {
            try { if (System.IO.File.Exists(File)) System.IO.File.Delete(File); } catch { }
            f.WindowState = FormWindowState.Normal;
            var size = new Size(Scale(f, DefaultSize.Width), Scale(f, DefaultSize.Height));
            var wa = Screen.PrimaryScreen.WorkingArea;
            f.Bounds = new Rectangle(wa.X + (wa.Width - size.Width) / 2, wa.Y + (wa.Height - size.Height) / 2, size.Width, size.Height);
        }

        /// <summary>Хотя бы заголовок окна (полоса 40 px) — на каком-нибудь экране.</summary>
        internal static bool IsVisibleOnAnyScreen(Rectangle r)
        {
            var title = new Rectangle(r.X, r.Y, r.Width, Math.Min(40, r.Height));
            return Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(title) && Rectangle.Intersect(s.WorkingArea, title).Width >= 100);
        }

        private static int Scale(Form f, int logical) => (int)Math.Round(logical * (f.DeviceDpi > 0 ? f.DeviceDpi : 96) / 96.0);
    }
}
