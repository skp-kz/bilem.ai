using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Bilem.Agent;
using Xunit;

namespace Bilem.Agent.Core.Tests
{
    /// <summary>Окно «Настройка базы», текст для поддержки и итог проверок (stand #75, 11.11, beta.9).</summary>
    public class RunFormTests
    {
        private static IEnumerable<Control> All(Control c) => c.Controls.Cast<Control>().SelectMany(k => new[] { k }.Concat(All(k)));

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        private static void Pump(Func<bool> until)
        {
            var end = DateTime.Now.AddSeconds(10);
            while (!until() && DateTime.Now < end) { Application.DoEvents(); Thread.Sleep(10); }
        }

        /// <summary>Упасть на шаге проверок с длинной причиной: блок ошибки занимает несколько строк.</summary>
        private static bool FailOnVerify(Onboarding ob)
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var reason = string.Join(", ", Enumerable.Repeat("Catalog_ОченьДлинноеИмяОбъекта", 8));
            typeof(Onboarding).GetMethod("Begin", flags).Invoke(ob, new object[] { "verify" });
            for (var i = 0; i < 40; i++) Log.Info("строка журнала " + i);
            typeof(Onboarding).GetMethod("End", flags).Invoke(ob, new object[] { StepStatus.Fail, RunLog.Short(reason, 160) });
            typeof(Onboarding).GetMethod("Fail", flags).Invoke(ob, new object[] { reason, Txt.S("HintRunSetupAgain") });
            return false;
        }

        /// <summary>На минимальном размере и при ошибке видны все 9 шагов без прокрутки, журнал не меньше 10 строк, кнопки на месте.</summary>
        [Fact]
        public void AllStepsAndTenLogLinesFitAtMinimumSize()
        {
            // Продолжение await в окне должно вернуться в этот поток, а у xunit свой контекст
            var saved = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            try { CheckMinimumSize(); }
            finally { SynchronizationContext.SetSynchronizationContext(saved); }
        }

        private static void CheckMinimumSize()
        {
            using (var f = new RunForm("Настройка базы", FailOnVerify, "setup", "demo") { StartPosition = FormStartPosition.Manual, ShowInTaskbar = false, Opacity = 0 })
            {
                f.Show();
                var all = All(f).ToList();
                var close = all.OfType<Button>().Single(b => b.Text == Txt.S("UiClose"));
                Pump(() => close.Enabled);
                Assert.Equal(f.LogicalToDeviceUnits(720), f.MinimumSize.Width);
                Assert.True(f.MinimumSize.Height >= f.LogicalToDeviceUnits(560));
                f.Size = f.MinimumSize;
                f.PerformLayout();
                for (var i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(5); }

                var grid = all.OfType<DataGridView>().Single();
                Assert.Equal(Onboarding.SetupSteps.Length, grid.Rows.Count);
                Assert.Equal(ScrollBars.None, grid.ScrollBars);
                Assert.True(grid.ColumnHeadersHeight + grid.Rows.GetRowsHeight(DataGridViewElementStates.Visible) <= grid.ClientSize.Height, "шаги не помещаются: " + grid.ClientSize.Height);
                var log = all.OfType<TextBox>().Single();
                Assert.True(log.ClientSize.Height >= 10 * log.Font.Height, "журнал ниже 10 строк: " + log.ClientSize.Height);
                Assert.True(SendMessage(log.Handle, 0xCE, IntPtr.Zero, IntPtr.Zero) > 0, "журнал не прокручен к концу");
                Assert.Contains(all.OfType<Button>(), b => b.Text == Txt.S("UiCopySupport") && b.Visible);
                Assert.Contains(all.OfType<Button>(), b => b.Text == Txt.S("UiSaveLog") && b.Visible);
            }
        }

        [Fact]
        public void Support_IsMarkdownWithoutSecrets()
        {
            var s = RunLog.Support(new SupportInfo
            {
                Title = "Настройка базы", ScalePercent = 150,
                Base = new RunFacts { Name = "demo", Configuration = "Бухгалтерия", ConfigVersion = "3.0.73.1", Platform = "8.3.24.1342", PlatformBits = 64, Organizations = new List<string> { "Ромашка Плюс" } },
                Steps = new[] { new Step { Id = "verify", Title = "Проверки", Status = StepStatus.Fail, Detail = "a|b", Duration = TimeSpan.FromSeconds(1.5) } },
                FailedStep = 9, FailedTitle = "Проверки", Error = "нет доступа", Hint = "сделайте так",
                Log = "Pwd=\"123\"\nC:\\Users\\ivan.petrov\\AppData\\x.log\nБИН 123456789012\nТОО \"Альфа\"\nРомашка Плюс\n1b4e28ba-2fa1-11d2-883f-123456789012",
            });
            Assert.Contains("150%", s);
            Assert.Contains("8.3.24.1342, " + Txt.F("SupportBits", 64), s);
            Assert.Contains("| 1 | Проверки | " + Txt.S("UiStep_Fail") + " — a\\|b | " + Txt.F("SupportSeconds", "1.5") + " |", s);
            Assert.Contains(Txt.F("UiRunStopped", 9, "Проверки", "нет доступа"), s);
            Assert.Contains("C:\\Users\\<user>\\AppData", s);
            Assert.Contains("883f-123456789012", s);
            foreach (var secret in new[] { "Pwd=\"123\"", "ivan.petrov", "БИН 123456789012", "Альфа", "Ромашка" }) Assert.DoesNotContain(secret, s);
        }

        [Fact]
        public void Stopped_NamesStepReasonAndAction()
        {
            var s = RunLog.Stopped(9, "Проверки", "упало", "сделайте так");
            Assert.Contains(Txt.F("UiRunStopped", 9, "Проверки", "упало"), s);
            Assert.Contains(Txt.S("WhatToDo") + " сделайте так", s);
        }

        /// <summary>Откат на Apache: первая неудачная проверка не считается ошибкой, повтор шага — тот же шаг.</summary>
        [Fact]
        public void Progress_RetriedStepCountsOnce()
        {
            var steps = new[]
            {
                new Step { Id = "verify", Status = StepStatus.Fail },
                new Step { Id = "apache_fallback", Status = StepStatus.Ok },
                new Step { Id = "verify", Status = StepStatus.Ok },
            };
            var p = StepProgress.Of(steps, 2, finished: true);
            Assert.False(p.Failed);
            Assert.Equal(2, p.Total);
            Assert.False(StepProgress.Of(steps.Take(2).ToList(), 2).Failed);
        }

        /// <summary>«Базы 1С»: короткие колонки не обрезаются, длинный статус не даёт горизонтальной прокрутки.</summary>
        [Fact]
        public void BaseColumns_FitContentWithoutScroll()
        {
            using (var f = new BilemForm { ShowInTaskbar = false, Opacity = 0, StartPosition = FormStartPosition.Manual, Size = new Size(700, 400) })
            using (var lv = new ListView { View = View.Details, Dock = DockStyle.Fill })
            {
                foreach (var c in new[] { "Имя", "Конфигурация", "Версия", "Папка", "Статус" }) lv.Columns.Add(c, 40);
                lv.Items.Add(new ListViewItem(new[] { "make_it_easy_2026", "Бухгалтерия", "3.0.73.1", @"C:\bds\Make IT", new string('x', 300) }));
                f.Controls.Add(lv);
                f.Show();
                f.FitContent(lv, 3);
                var name = TextRenderer.MeasureText("make_it_easy_2026", lv.Font, Size.Empty, TextFormatFlags.NoPadding).Width;
                Assert.True(lv.Columns[0].Width >= name, $"колонка «Имя» {lv.Columns[0].Width} уже текста {name}");
                Assert.True(lv.Columns.Cast<ColumnHeader>().Sum(c => c.Width) <= lv.ClientSize.Width);
                Assert.True(lv.Columns[3].Width >= f.LogicalToDeviceUnits(80));
            }
        }

        /// <summary>Настоящая вкладка «Базы 1С»: подгонка колонок не зацикливается на полосах прокрутки (beta.9: окно настроек не открывалось).</summary>
        [Theory]
        [InlineData(560, 0)]
        [InlineData(560, 1)]
        [InlineData(860, 1)]
        [InlineData(860, 30)]
        [InlineData(1290, 2)]
        public void BasesTab_ColumnFitDoesNotLoop(int width, int items)
        {
            using (var main = new MainForm(viaService: true))
            using (var f = new BilemForm { ShowInTaskbar = false, Opacity = 0, StartPosition = FormStartPosition.Manual, Size = new Size(width, 520) })
            using (var tabs = new TabControl { Dock = DockStyle.Fill })
            {
                var page = main.Controls.OfType<TabControl>().Single().TabPages[0];
                var lv = All(page).OfType<ListView>().Single();
                for (var i = 0; i < items; i++)
                    lv.Items.Add(new ListViewItem(new[] { "make_it_easy_2026", "Бухгалтерия для Казахстана", "3.0.73.1", @"C:\bds\Make IT", "✖ проверка не прошла: " + new string('x', 200) }));
                var changes = 0;
                lv.ColumnWidthChanged += (s, e) => changes++;
                tabs.TabPages.Add(page);
                f.Controls.Add(tabs);
                f.Show();
                f.Width -= 40;
                var end = DateTime.Now.AddMilliseconds(300);
                Pump(() => DateTime.Now > end);
                Assert.True(changes < 100, $"колонки менялись {changes} раз");
                Assert.True(lv.Columns.Cast<ColumnHeader>().Sum(c => c.Width) <= lv.ClientSize.Width);
            }
        }

        /// <summary>Сохранённое окно больше экрана или за его краем (другое разрешение в удалённом сеансе) — целиком в рабочей области.</summary>
        [Fact]
        public void Placement_FitsWorkingArea()
        {
            var area = new Rectangle(0, 0, 1024, 728);
            Assert.Equal(area, WindowPlacement.FitToArea(new Rectangle(300, 200, 1920, 1080), area));
            Assert.Equal(new Rectangle(524, 228, 500, 500), WindowPlacement.FitToArea(new Rectangle(900, 600, 500, 500), area));
            Assert.Equal(new Rectangle(10, 20, 500, 500), WindowPlacement.FitToArea(new Rectangle(10, 20, 500, 500), area));
        }

        [Fact]
        public void Status_IsOneTextForEveryWindow()
        {
            var at = new DateTime(2026, 11, 11, 8, 57, 0);
            Assert.Equal(Txt.F("UiStatusCheckFailed", at, "причина"), new BaseConfig { VerifiedAt = at, LastCheckOk = false, LastCheckError = "причина" }.StatusText);
            Assert.Equal(Txt.F("UiStatusOkWarn", at), new BaseConfig { VerifiedAt = at, LastCheckOk = true, LastCheckWarning = "чужие" }.StatusText);
            Assert.Equal(Txt.F("UiStatusOk", at), new BaseConfig { VerifiedAt = at, LastCheckOk = true }.StatusText);
            Assert.Equal(Txt.S("UiStatusNotChecked"), new BaseConfig().StatusText);
        }

        [Fact]
        public void FailedCheck_HidesBaseFromMcp()
        {
            Assert.True(new BaseConfig().McpReady);
            Assert.True(new BaseConfig { LastCheckOk = true }.McpReady);
            Assert.False(new BaseConfig { LastCheckOk = false }.McpReady);
        }

        /// <summary>База с чужим составом OData (лог Славы, make_it_easy_2026): объект вне нашего состава — WARN, пока «Доступ» не применяли; после — FAIL.</summary>
        [Theory]
        [InlineData(false, 0, true)]
        [InlineData(true, 1, false)]
        public void ForeignODataObjects(bool accessApplied, int failures, bool warned)
        {
            var listener = new HttpListener();
            var port = new Random().Next(20000, 40000);
            listener.Prefixes.Add($"http://127.0.0.1:{port}/od/");
            listener.Start();
            var serving = new Thread(() =>
            {
                try
                {
                    while (listener.IsListening)
                    {
                        var c = listener.GetContext();
                        var path = c.Request.Url.AbsolutePath;
                        var auth = c.Request.Headers["Authorization"] != null;
                        string body = "{\"value\":[]}";
                        c.Response.StatusCode = 200;
                        if (!auth) c.Response.StatusCode = 401;
                        else if (path.EndsWith("$metadata")) body = "<EntitySet Name=\"Catalog_Наш\"/><EntitySet Name=\"Catalog_Чужой\"/>";
                        else if (path.Contains("Catalog_%D0%A7") || path.Contains("Catalog_Чужой")) c.Response.StatusCode = 500;
                        var bytes = Encoding.UTF8.GetBytes(body);
                        c.Response.OutputStream.Write(bytes, 0, bytes.Length);
                        c.Response.Close();
                    }
                }
                catch (HttpListenerException) { }
            }) { IsBackground = true };
            serving.Start();
            try
            {
                var b = new BaseConfig
                {
                    Slug = "demo", OData = $"http://127.0.0.1:{port}/od", User = "bilem", WebServer = "apache",
                    ODataObjects = new List<string> { "Справочник.Наш" },
                    Read = accessApplied ? new List<string> { "Справочник.Наш" } : null,
                    Catalog = new AccessCatalog { Items = { new AccessItem { FullName = "Справочник.Наш" }, new AccessItem { FullName = "Справочник.Чужой" } } },
                };
                var r = Verifier.Run(b, "x");
                Assert.Equal(failures, r.Failures);
                Assert.Equal(warned, r.Warning != null);
                Assert.Equal(accessApplied, r.Reason != null);
                if (accessApplied) Assert.Equal(Txt.S("HintApplyAccess"), r.Hint);
            }
            finally { listener.Stop(); }
        }
    }
}
