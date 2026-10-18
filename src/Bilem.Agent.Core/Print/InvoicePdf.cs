using System;
using System.IO;
using System.Linq;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using PdfSharp.Fonts;

namespace Bilem.Agent.Core.Print
{
    /// <summary>Шрифт PT Sans (OFL) из ресурсов сборки: кириллица и казахские буквы, без зависимости от шрифтов Windows.</summary>
    internal sealed class EmbeddedFonts : IFontResolver
    {
        public const string Family = "PT Sans";
        private static readonly object Gate = new object();
        private static bool _set;

        public static void Ensure()
        {
            lock (Gate)
            {
                if (_set) return;
                GlobalFontSettings.FontResolver = new EmbeddedFonts();
                _set = true;
            }
        }

        public FontResolverInfo ResolveTypeface(string familyName, bool bold, bool italic) =>
            new FontResolverInfo(bold ? "PTSans-Bold" : "PTSans-Regular");

        public byte[] GetFont(string faceName)
        {
            using (var s = typeof(EmbeddedFonts).Assembly.GetManifestResourceStream(faceName + ".ttf"))
            using (var ms = new MemoryStream())
            {
                if (s == null) throw new InvalidOperationException("нет шрифта " + faceName);
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }
    }

    /// <summary>
    /// Счёт на оплату в PDF — макет печатной формы 1С (образец Славы), без её огрехов: «Ед.» не переносится по буквам,
    /// длинное имя бенефициара переносится внутри ячейки, «Кбе»/«КНП» — в своих ячейках. Подпись и печать — поверх строки подписи.
    /// </summary>
    public static class InvoicePdf
    {
        private const double Body = PdfKit.Body;

        public static byte[] Render(InvoiceData inv, PrintSettings ps, string logo, string stamp, string sign)
        {
            ps = ps ?? new PrintSettings();
            var (doc, sec) = PdfKit.NewDoc($"Счёт на оплату № {inv.PrintNumber} от {Words.Date(inv.Date)}", "Счёт на оплату покупателю");

            // --- Шапка: логотип + текст
            var head = sec.AddTable();
            head.AddColumn(Unit.FromMillimeter(45));
            head.AddColumn(Unit.FromMillimeter(Body - 45));
            var hr = head.AddRow();
            hr.VerticalAlignment = VerticalAlignment.Center;
            PdfKit.Logo(hr.Cells[0], logo);
            if (!string.IsNullOrWhiteSpace(ps.HeaderText))
            {
                var p = hr.Cells[1].AddParagraph(ps.HeaderText.Trim());
                p.Format.Font.Size = 7.5;
                p.Format.Alignment = ParagraphAlignment.Center;
            }
            Space(sec, 5);

            // --- Образец платёжного поручения (галочка на подвкладке «Счёт»)
            if (ps.InvoicePaymentSample) PaymentSample(sec, inv);

            // --- Заголовок
            var title = sec.AddParagraph($"Счёт на оплату № {inv.PrintNumber} от {Words.Date(inv.Date)}");
            title.Format.Font.Size = 15;
            title.Format.Font.Bold = true;
            title.Format.Borders.Bottom.Width = 1.2;
            title.Format.Borders.DistanceFromBottom = Unit.FromMillimeter(1.5);
            title.Format.SpaceAfter = Unit.FromMillimeter(3);

            Party(sec, "Поставщик:", Join($"БИН / ИИН {inv.SupplierBin}", inv.SupplierName, inv.SupplierAddress));
            Party(sec, "Покупатель:", Join($"БИН / ИИН {inv.BuyerBin}", inv.BuyerName));
            Party(sec, "Договор:", inv.Contract);
            Space(sec, 2);

            // --- Таблица товаров и услуг
            var t = sec.AddTable();
            t.Borders.Top.Width = 0.6;
            double[] w = { 8, 24, 0, 16, 17, 25, 27 };
            w[2] = Body - w.Sum();
            foreach (var x in w) t.AddColumn(Unit.FromMillimeter(x));
            var th = t.AddRow();
            th.HeadingFormat = true;
            th.Format.Font.Bold = true;
            th.Borders.Bottom.Width = 0.6;
            string[] heads = { "№", "Код", "Наименование", "Кол-во", "Ед.", "Цена", "Сумма" };
            for (var i = 0; i < heads.Length; i++) th.Cells[i].AddParagraph(heads[i]);
            Align(th, ParagraphAlignment.Center, 0, 3, 4); Align(th, ParagraphAlignment.Right, 5, 6);
            foreach (var l in inv.Lines)
            {
                var r = t.AddRow();
                r.TopPadding = r.BottomPadding = Unit.FromMillimeter(0.5);
                r.Cells[0].AddParagraph(l.N.ToString());
                r.Cells[1].AddParagraph(l.Code ?? "");
                r.Cells[2].AddParagraph(l.Name ?? "");
                r.Cells[3].AddParagraph(Words.Qty(l.Qty));
                // Единица — короткая, в одну строку: длинная ужимается кеглем, а не ломается по буквам
                var u = r.Cells[4].AddParagraph(l.Unit ?? "");
                if ((l.Unit ?? "").Length > 7) u.Format.Font.Size = 7;
                r.Cells[5].AddParagraph(Words.Money(l.Price));
                r.Cells[6].AddParagraph(Words.Money(l.Sum));
                Align(r, ParagraphAlignment.Center, 0, 3, 4); Align(r, ParagraphAlignment.Right, 5, 6);
            }
            var last = t.Rows[t.Rows.Count - 1];
            last.Borders.Bottom.Width = 0.6;

            // --- Итоги
            var tot = sec.AddTable();
            tot.AddColumn(Unit.FromMillimeter(Body - 27));
            tot.AddColumn(Unit.FromMillimeter(27));
            Total(tot, "Итого:", Words.Money(inv.LinesTotal), true);
            if (inv.AccountVat && inv.VatTotal != 0)
            {
                var rate = inv.VatRate.HasValue ? $" {inv.VatRate}%" : "";
                if (inv.PriceIncludesVat) Total(tot, $"В том числе НДС{rate}:", Words.Money(inv.VatTotal), false);
                else { Total(tot, $"Сумма НДС{rate}:", Words.Money(inv.VatTotal), false); Total(tot, "Всего с НДС:", Words.Money(inv.ToPay), true); }
            }
            else Total(tot, "Без НДС", "", false);
            Space(sec, 2);
            sec.AddParagraph($"Всего наименований {inv.Lines.Count}, на сумму {Words.Money(inv.ToPay)} тенге");
            var words = sec.AddParagraph($"Всего к оплате: {Words.Tenge(inv.ToPay)}");
            words.Format.Font.Bold = true;
            words.Format.Borders.Bottom.Width = 1.2;
            words.Format.Borders.DistanceFromBottom = Unit.FromMillimeter(2);
            if (!string.IsNullOrWhiteSpace(ps.InvoiceFooter))
            {
                var foot = sec.AddParagraph(ps.InvoiceFooter.Trim());
                foot.Format.Font.Size = 8;
                foot.Format.SpaceBefore = Unit.FromMillimeter(3);
            }
            Space(sec, 16);

            // --- Подпись: подпись и печать — поверх строки подписи, по её месту на странице
            var sl = PdfKit.AddSignLine(sec, "Исполнитель   ", "______________________________", ps.IssuedByEffective, 10, stamp, sign);
            return PdfKit.Render(doc, sl);
        }

        private static void PaymentSample(Section sec, InvoiceData inv)
        {
            var cap = sec.AddParagraph("Образец платежного поручения");
            cap.Format.Font.Bold = true;
            cap.Format.SpaceAfter = Unit.FromMillimeter(1);
            var pay = sec.AddTable();
            pay.Borders.Width = 0.6;
            pay.AddColumn(Unit.FromMillimeter(Body - 95));
            pay.AddColumn(Unit.FromMillimeter(60));
            pay.AddColumn(Unit.FromMillimeter(35));
            var r1 = pay.AddRow(); var r2 = pay.AddRow(); var r3 = pay.AddRow(); var r4 = pay.AddRow();
            foreach (var r in new[] { r1, r2, r3, r4 }) { r.VerticalAlignment = VerticalAlignment.Center; r.TopPadding = Unit.FromMillimeter(0.6); r.BottomPadding = Unit.FromMillimeter(0.6); }
            var ben = r1.Cells[0];
            ben.MergeDown = 1;
            ben.VerticalAlignment = VerticalAlignment.Top;
            ben.AddParagraph("Бенефициар:");
            Bold(ben.AddParagraph(), inv.SupplierName);
            ben.AddParagraph("БИН: " + inv.SupplierBin);
            Head(r1.Cells[1], "ИИК"); Head(r1.Cells[2], "Кбе");
            Value(r2.Cells[1], inv.Iik); Value(r2.Cells[2], inv.Kbe);
            var bank = r3.Cells[0];
            bank.MergeDown = 1;
            bank.VerticalAlignment = VerticalAlignment.Top;
            bank.AddParagraph("Банк бенефициара:");
            Bold(bank.AddParagraph(), inv.BankName);
            Head(r3.Cells[1], "БИК"); Head(r3.Cells[2], "Код назначения платежа");
            Value(r4.Cells[1], inv.Bik); Value(r4.Cells[2], inv.Knp);
            Space(sec, 5);
        }

        private static void Space(Section s, double mm) => PdfKit.Space(s, mm);

        private static string Join(params string[] parts) => string.Join(", ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()));

        private static void Bold(Paragraph p, string text) { p.AddFormattedText(text ?? "", TextFormat.Bold); }

        private static void Head(Cell c, string text)
        {
            var p = c.AddParagraph(text);
            p.Format.Font.Bold = true;
            p.Format.Font.Size = 8;
            p.Format.Alignment = ParagraphAlignment.Center;
        }

        private static void Value(Cell c, string text)
        {
            var p = c.AddParagraph(text ?? "");
            p.Format.Font.Bold = true;
            p.Format.Font.Size = 10;
            p.Format.Alignment = ParagraphAlignment.Center;
        }

        private static void Party(Section s, string label, string text)
        {
            var p = s.AddParagraph();
            p.Format.SpaceAfter = Unit.FromMillimeter(1.5);
            p.Format.LeftIndent = Unit.FromMillimeter(24);
            p.Format.FirstLineIndent = Unit.FromMillimeter(-24);
            p.Format.TabStops.AddTabStop(Unit.FromMillimeter(24));
            p.AddFormattedText(label, TextFormat.Bold);
            p.AddTab();
            p.AddText(text ?? "");
        }

        private static void Align(Row r, ParagraphAlignment a, params int[] cols) { foreach (var c in cols) r.Cells[c].Format.Alignment = a; }

        private static void Total(Table t, string label, string value, bool bold)
        {
            var r = t.AddRow();
            r.Cells[0].AddParagraph(label).Format.Alignment = ParagraphAlignment.Right;
            r.Cells[1].AddParagraph(value).Format.Alignment = ParagraphAlignment.Right;
            r.Format.Font.Bold = bold;
            r.TopPadding = Unit.FromMillimeter(0.8);
        }
    }
}
