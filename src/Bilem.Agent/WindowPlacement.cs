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
        public static readonly Size DefaultSize = new Size(860, 520);
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

        // Новое имя файла: прежний размер запоминался и при простом перемещении окна, с ним новый размер по умолчанию не дошёл бы до людей
        private static string File => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bilem", "settings-window.json");

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
                // Сохранено при другом разрешении (удалённый сеанс): окно не должно быть больше экрана или уходить за край
                f.Bounds = FitToArea(r, Screen.FromRectangle(r).WorkingArea);
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

        private static string RunFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bilem", "run-window.json");

        /// <summary>Окно шагов: только размер, положение — по центру родителя. Не меньше минимума и не больше экрана.</summary>
        public static void RestoreRunSize(Form f)
        {
            try
            {
                if (!System.IO.File.Exists(RunFile)) return;
                var s = JsonConvert.DeserializeObject<Saved>(System.IO.File.ReadAllText(RunFile));
                if (s == null || s.Width <= 0 || s.Height <= 0) return;
                var scale = s.Dpi > 0 && f.DeviceDpi > 0 ? (double)f.DeviceDpi / s.Dpi : 1.0;
                var area = Screen.FromControl(f).WorkingArea;
                f.Size = new Size(Math.Min(Math.Max((int)(s.Width * scale), f.MinimumSize.Width), area.Width), Math.Min(Math.Max((int)(s.Height * scale), f.MinimumSize.Height), area.Height));
            }
            catch { }
        }

        public static void SaveRunSize(Form f)
        {
            try
            {
                if (f.WindowState != FormWindowState.Normal) return;
                Directory.CreateDirectory(Path.GetDirectoryName(RunFile));
                System.IO.File.WriteAllText(RunFile, JsonConvert.SerializeObject(new Saved { Width = f.Width, Height = f.Height, Dpi = f.DeviceDpi }));
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

        /// <summary>Окно целиком в рабочей области: не больше её и не за краем.</summary>
        internal static Rectangle FitToArea(Rectangle r, Rectangle area)
        {
            var w = Math.Min(r.Width, area.Width);
            var h = Math.Min(r.Height, area.Height);
            return new Rectangle(Math.Max(area.X, Math.Min(r.X, area.Right - w)), Math.Max(area.Y, Math.Min(r.Y, area.Bottom - h)), w, h);
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
