using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Bilem.Agent;
using Xunit;

namespace Bilem.Agent.Core.Tests
{
    public class AddBaseFormTests
    {
        private static IEnumerable<Control> All(Control c) => c.Controls.Cast<Control>().SelectMany(k => new[] { k }.Concat(All(k)));

        /// <summary>На минимальном размере окна видны список и поля нормальной ширины (stand #75: в beta.7 поля сжимались до ~70 px).</summary>
        [Fact]
        public void ListAndFieldsFitAtMinimumSize()
        {
            using (var f = new AddBaseForm { StartPosition = FormStartPosition.Manual, ShowInTaskbar = false, Opacity = 0 })
            {
                f.Show();
                Assert.Equal(new Size(f.LogicalToDeviceUnits(640), f.LogicalToDeviceUnits(520)), f.MinimumSize);
                f.Size = f.MinimumSize;
                f.PerformLayout();

                var all = All(f).ToList();
                var list = all.OfType<ListView>().Single();
                Assert.Equal(2, list.Columns.Count);
                Assert.True(list.Height >= f.LogicalToDeviceUnits(130), "список ниже 6 строк: " + list.Height);
                Assert.Equal(4, all.OfType<TextBox>().Count());
                foreach (var t in all.OfType<TextBox>())
                    Assert.True(t.Width >= f.LogicalToDeviceUnits(320), "поле уже 320 px: " + t.Width);
                Assert.Same(list, f.ActiveControl);
            }
        }

        /// <summary>«Подключить» неактивна, пока не указана папка.</summary>
        [Fact]
        public void ConnectIsDisabledUntilFolderIsSet()
        {
            using (var f = new AddBaseForm { StartPosition = FormStartPosition.Manual, ShowInTaskbar = false, Opacity = 0 })
            {
                f.Show();
                var all = All(f).ToList();
                var connect = all.OfType<Button>().Single(b => b.Text == Txt.S("UiConnectBase"));
                var path = all.OfType<TextBox>().First();
                Assert.False(connect.Enabled);
                path.Text = @"C:\Bases\Demo";
                Assert.True(connect.Enabled);
                path.Text = "";
                Assert.False(connect.Enabled);
            }
        }
    }
}
