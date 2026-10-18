using System.Drawing;
using System.Windows.Forms;
using Bilem.Agent;
using Xunit;

namespace Bilem.Agent.Core.Tests
{
    /// <summary>Пачка 4: компактное окно.</summary>
    public class Batch4Tests
    {
        [Fact]
        public void Window_DefaultAndMinimumAreCompact()
        {
            Assert.Equal(new Size(640, 440), WindowPlacement.DefaultSize);
            Assert.Equal(new Size(560, 380), WindowPlacement.MinSize);
        }

        /// <summary>Пачка 6: значки в exe — ровно файлы assets/icons (без старых копий).</summary>
        [Theory]
        [InlineData("bilem-agent.ico")]
        [InlineData("tray-green.ico")]
        [InlineData("tray-yellow.ico")]
        [InlineData("tray-red.ico")]
        [InlineData("tray-grey.ico")]
        public void Icons_EmbeddedEqualAssets(string name)
        {
            var root = System.AppContext.BaseDirectory;
            while (root != null && !System.IO.File.Exists(System.IO.Path.Combine(root, "Bilem.Agent.sln"))) root = System.IO.Path.GetDirectoryName(root);
            Assert.NotNull(root);
            var file = System.IO.File.ReadAllBytes(System.IO.Path.Combine(root, "assets", "icons", name));
            using (var s = typeof(WindowPlacement).Assembly.GetManifestResourceStream(name))
            using (var ms = new System.IO.MemoryStream())
            {
                Assert.NotNull(s);
                s.CopyTo(ms);
                using (var md5 = System.Security.Cryptography.MD5.Create())
                    Assert.Equal(System.BitConverter.ToString(md5.ComputeHash(file)), System.BitConverter.ToString(md5.ComputeHash(ms.ToArray())));
            }
        }

        [Fact]
        public void Window_OffScreenIsRejected()
        {
            Assert.False(WindowPlacement.IsVisibleOnAnyScreen(new Rectangle(-20000, -20000, 640, 440)));
            var wa = Screen.PrimaryScreen.WorkingArea;
            Assert.True(WindowPlacement.IsVisibleOnAnyScreen(new Rectangle(wa.X + 10, wa.Y + 10, 640, 440)));
        }
    }
}
