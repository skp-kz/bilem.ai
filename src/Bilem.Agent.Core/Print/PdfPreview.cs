using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace Bilem.Agent.Core.Print
{
    /// <summary>
    /// Картинка первой страницы PDF (PNG) — для клиентов MCP, которые не показывают PDF. Рендер — Windows.Data.Pdf (есть в Windows 10+ / Server 2016+);
    /// нет рендера (старая Windows) — null, PDF всё равно отдаётся.
    /// </summary>
    public static class PdfPreview
    {
        public static byte[] FirstPagePng(byte[] pdf, int width = 1000)
        {
            try { return Task.Run(() => Render(pdf, width)).GetAwaiter().GetResult(); }
            catch (Exception e) { Log.Warn(Txt.F("PrintPreviewFailed", e.Message)); return null; }
        }

        private static async Task<byte[]> Render(byte[] pdf, int width)
        {
            using (var input = new InMemoryRandomAccessStream())
            {
                await input.WriteAsync(pdf.AsBuffer());
                input.Seek(0);
                var doc = await PdfDocument.LoadFromStreamAsync(input);
                using (var page = doc.GetPage(0))
                using (var output = new InMemoryRandomAccessStream())
                {
                    var opt = new PdfPageRenderOptions
                    {
                        DestinationWidth = (uint)width,
                        DestinationHeight = (uint)Math.Round(width * page.Size.Height / page.Size.Width),
                        BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
                    };
                    await page.RenderToStreamAsync(output, opt);
                    using (var ms = new MemoryStream())
                    {
                        await output.AsStreamForRead().CopyToAsync(ms);
                        return ms.ToArray();
                    }
                }
            }
        }
    }
}
