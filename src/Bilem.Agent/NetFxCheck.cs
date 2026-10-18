using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Bilem.Agent
{
    /// <summary>
    /// Проверка .NET Framework 4.8 — первым делом при запуске (решение Славы 18.10, F6 п. 3): exe стартует на любом CLR 4
    /// (в .config нет sku 4.8), и вместо системной ошибки Windows человек видит своё окно «установить?».
    /// Только типы .NET 4.0 и WinForms — ничего из Bilem.Agent.Core (он требует 4.8), тексты — здесь, не в ресурсах Core.
    /// </summary>
    internal static class NetFxCheck
    {
        /// <summary>Release ≥ 528040 — .NET Framework 4.8 или новее.</summary>
        public const int Net48Release = 528040;
        public const string OfflineUrl = "https://dotnet.microsoft.com/download/dotnet-framework/net48";

        public static bool Installed()
        {
            // Проверка окна на машине, где .NET 4.8 есть (как BILEM_TEST_NO_NETFX=1 у MSI)
            if (Environment.GetEnvironmentVariable("BILEM_TEST_NO_NETFX") == "1") return false;
            try
            {
                using (var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                    return k?.GetValue("Release") is int r && r >= Net48Release;
            }
            catch { return true; }   // не смогли прочитать — не мешаем запуску
        }

        /// <summary>Нет 4.8 — окно «Установить / Закрыть». true — можно продолжать запуск агента.</summary>
        public static bool EnsureOrAsk()
        {
            if (Installed()) return true;
            Application.EnableVisualStyles();
            using (var f = new NetFxForm()) f.ShowDialog();
            return false;
        }

        /// <summary>Web-установщик Microsoft рядом с агентом (ставит MSI): Program Files\Bilem Agent\redist\ndp48-web.exe.</summary>
        public static string WebInstaller => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "redist", "ndp48-web.exe");

        /// <summary>От BilemForm (значок, шрифт): его конструктор — только API .NET 4.0; Px (LogicalToDeviceUnits, 4.7+) здесь не вызываем.</summary>
        private sealed class NetFxForm : BilemForm
        {
            public NetFxForm()
            {
                Text = "Bilem Agent";
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = MinimizeBox = false;
                StartPosition = FormStartPosition.CenterScreen;
                ClientSize = new Size(480, 230);

                var title = new Label { Text = "Не найден необходимый компонент", AutoSize = true, Location = new Point(16, 16), Font = new Font(Font.FontFamily, Font.Size * 1.25f) };
                var body = new Label
                {
                    Location = new Point(16, 48), Size = new Size(448, 120),
                    Text = "Для работы Bilem Agent требуется компонент Microsoft .NET Framework 4.8, который не установлен на этом компьютере.\r\n\r\n" +
                           "После нажатия кнопки «Установить» запустится веб-установщик Microsoft .NET Framework 4.8. Следуйте его инструкциям; " +
                           "если установщик запросит перезагрузку, выполните её. Затем запустите Bilem Agent с ярлыка на рабочем столе.",
                };
                var link = new LinkLabel { Text = "Нет интернета — автономный установщик", AutoSize = true, Location = new Point(16, 168) };
                link.LinkClicked += (s, e) => Open(OfflineUrl);
                var install = new Button { Text = "Установить", AutoSize = true, Location = new Point(288, 196), DialogResult = DialogResult.None };
                var close = new Button { Text = "Закрыть", AutoSize = true, Location = new Point(384, 196), DialogResult = DialogResult.Cancel };
                install.Click += (s, e) =>
                {
                    if (File.Exists(WebInstaller))
                    {
                        // Через оболочку с повышением прав — установщик Microsoft сам скачает .NET и попросит перезагрузку
                        try { Process.Start(new ProcessStartInfo(WebInstaller) { UseShellExecute = true, Verb = "runas" }); Close(); }
                        catch (Exception ex) { MessageBox.Show(this, "Не удалось запустить установщик .NET: " + ex.Message + "\r\n\r\nСкачайте его вручную: " + OfflineUrl, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                    }
                    else { Open(OfflineUrl); Close(); }
                };
                Controls.AddRange(new Control[] { title, body, link, install, close });
                AcceptButton = install;
                CancelButton = close;
            }

            private static void Open(string url)
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
            }
        }
    }
}
