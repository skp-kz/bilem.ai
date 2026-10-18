using System;
using System.Drawing;
using System.IO;
using System.Linq;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;
using PdfSharp.Drawing;

namespace Bilem.Agent.Core.Print
{
    /// <summary>Общее для печатных форм: документ A4 со шрифтом PT Sans, логотип, подпись и печать поверх строки подписи.</summary>
    internal static class PdfKit
    {
        public const double PageW = 210, Margin = 15, Body = PageW - 2 * Margin;

        public static (Document Doc, Section Sec) NewDoc(string title, string subject, double fontSize = 9)
        {
            EmbeddedFonts.Ensure();
            var doc = new Document();
            doc.Info.Title = title;
            doc.Info.Author = "Bilem Agent";
            doc.Info.Subject = subject;
            var st = doc.Styles["Normal"];
            st.Font.Name = EmbeddedFonts.Family;
            st.Font.Size = fontSize;
            var sec = doc.AddSection();
            sec.PageSetup.PageFormat = PageFormat.A4;
            sec.PageSetup.LeftMargin = sec.PageSetup.RightMargin = MigraDoc.DocumentObjectModel.Unit.FromMillimeter(Margin);
            sec.PageSetup.TopMargin = sec.PageSetup.BottomMargin = MigraDoc.DocumentObjectModel.Unit.FromMillimeter(12);
            return (doc, sec);
        }

        /// <summary>Логотип в ячейку: вписан в 40×15 мм.</summary>
        public static void Logo(MigraDoc.DocumentObjectModel.Tables.Cell cell, string logo)
        {
            if (logo == null) return;
            var img = cell.AddImage(logo);
            img.LockAspectRatio = true;
            using (var bmp = Image.FromFile(logo))
            {
                if ((double)bmp.Width / bmp.Height > 40.0 / 15) img.Width = MigraDoc.DocumentObjectModel.Unit.FromMillimeter(40);
                else img.Height = MigraDoc.DocumentObjectModel.Unit.FromMillimeter(15);
            }
        }

        public static void Space(Section s, double mm) => s.AddParagraph().Format.SpaceAfter = MigraDoc.DocumentObjectModel.Unit.FromMillimeter(mm);

        /// <summary>Строка подписи, поверх которой рисуются подпись и печать: «{lead}{line}   /{who}/».</summary>
        public sealed class SignLine
        {
            public Paragraph Paragraph; public string Lead; public string Line; public double FontSize;
            public string Stamp; public string Sign;
        }

        public static SignLine AddSignLine(Section sec, string lead, string line, string who, double fontSize, string stamp, string sign)
        {
            var p = sec.AddParagraph($"{lead}{line}   /{who}/");
            p.Format.Font.Size = fontSize;
            return new SignLine { Paragraph = p, Lead = lead, Line = line, FontSize = fontSize, Stamp = stamp, Sign = sign };
        }

        /// <summary>Отрисовать документ; подпись — над линией, печать — левым краем на конце линии, центр чуть ниже строки (как в счёте).</summary>
        public static byte[] Render(Document doc, params SignLine[] lines)
        {
            var pdf = new PdfDocumentRenderer { Document = doc };
            pdf.RenderDocument();
            foreach (var sl in lines.Where(l => l != null && (l.Stamp != null || l.Sign != null)))
            {
                for (var page = 1; page <= pdf.DocumentRenderer.FormattedDocument.PageCount; page++)
                {
                    var info = pdf.DocumentRenderer.GetRenderInfoFromPage(page).FirstOrDefault(ri => ri.DocumentObject == sl.Paragraph);
                    if (info == null) continue;
                    var area = info.LayoutInfo.ContentArea;
                    using (var g = XGraphics.FromPdfPage(pdf.PdfDocument.Pages[page - 1]))
                    {
                        var x0 = area.X.Point; var y0 = area.Y.Point;
                        double mm = 72 / 25.4;
                        var font = new XFont(EmbeddedFonts.Family, sl.FontSize);
                        var lead = g.MeasureString(sl.Lead, font).Width;
                        var lineEnd = g.MeasureString(sl.Lead + sl.Line, font).Width;
                        var lineMid = y0 + area.Height.Point / 2;
                        if (sl.Sign != null) Draw(g, sl.Sign, x0 + lead + 6 * mm, y0 - 11 * mm, 40 * mm, 15 * mm);
                        if (sl.Stamp != null) Draw(g, sl.Stamp, x0 + lineEnd - 8 * mm, lineMid + 3 * mm - 20 * mm, 40 * mm, 40 * mm);
                    }
                    break;
                }
            }
            using (var ms = new MemoryStream())
            {
                pdf.PdfDocument.Save(ms, false);
                return ms.ToArray();
            }
        }

        public static void Draw(XGraphics g, string file, double x, double y, double maxW, double maxH)
        {
            using (var img = XImage.FromFile(file))
            {
                var k = Math.Min(maxW / img.PointWidth, maxH / img.PointHeight);
                g.DrawImage(img, x, y, img.PointWidth * k, img.PointHeight * k);
            }
        }

        /// <summary>«Метка: текст» — метка жирным.</summary>
        public static Paragraph Labeled(Section s, string label, string text, double spaceAfterMm = 1.5)
        {
            var p = s.AddParagraph();
            p.Format.SpaceAfter = MigraDoc.DocumentObjectModel.Unit.FromMillimeter(spaceAfterMm);
            p.AddFormattedText(label + " ", TextFormat.Bold);
            p.AddText(text ?? "");
            return p;
        }
    }
}
