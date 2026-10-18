using System.Linq;
using MigraDoc.DocumentObjectModel;

namespace Bilem.Agent.Core.Print
{
    /// <summary>
    /// Доверенность, форма Д-1 (приложение 6 к приказу Министра финансов РК от 20.12.2012 № 562) — по образцу Славы.
    /// Подпись и печать — поверх строки «Руководитель организации», как в счёте.
    /// </summary>
    public static class PoaPdf
    {
        public static byte[] Render(PoaData p, PrintSettings ps, string logo, string stamp, string sign)
        {
            ps = ps ?? new PrintSettings();
            var title = $"Доверенность № {p.PrintNumber}";
            var (doc, sec) = PdfKit.NewDoc($"{title} от {Words.Date(p.Date)}", "Доверенность (форма Д-1)", 9.5);

            // --- Шапка: логотип слева, реквизиты приказа справа
            var head = sec.AddTable();
            head.AddColumn(Unit.FromMillimeter(60));
            head.AddColumn(Unit.FromMillimeter(PdfKit.Body - 60));
            var hr = head.AddRow();
            PdfKit.Logo(hr.Cells[0], logo);
            var order = hr.Cells[1].AddParagraph("Приложение 6 к приказу Министра финансов Республики Казахстан от 20 декабря 2012 года № 562");
            order.Format.Font.Size = 7.5;
            order.Format.Alignment = ParagraphAlignment.Right;
            var form = hr.Cells[1].AddParagraph("Форма Д-1");
            form.Format.Font.Size = 8;
            form.Format.Font.Bold = true;
            form.Format.Alignment = ParagraphAlignment.Right;
            form.Format.SpaceBefore = Unit.FromMillimeter(1.5);
            PdfKit.Space(sec, 4);

            // --- Организация
            Line(sec, "Организация: " + p.Org.Name);
            Line(sec, "ИИН/БИН: " + p.Org.Bin);
            if (!string.IsNullOrWhiteSpace(p.Org.Address)) Line(sec, "Адрес: " + p.Org.Address);
            if (!string.IsNullOrWhiteSpace(p.Org.Iik)) Line(sec, "Счёт: " + p.Org.Iik + (string.IsNullOrWhiteSpace(p.Org.BankName) ? "" : " в " + p.Org.BankName));
            PdfKit.Space(sec, 3);

            var t = sec.AddParagraph(title.ToUpperInvariant());
            t.Format.Font.Size = 14;
            t.Format.Font.Bold = true;
            t.Format.Alignment = ParagraphAlignment.Center;
            t.Format.SpaceAfter = Unit.FromMillimeter(2);
            var dates = sec.AddParagraph($"Дата выдачи: {Words.Date(p.Date)}          Доверенность действительна по: {Words.Date(p.ValidTo)}");
            dates.Format.Alignment = ParagraphAlignment.Center;
            dates.Format.SpaceAfter = Unit.FromMillimeter(5);

            PdfKit.Labeled(sec, "Выдана:", string.Join(", ", new[] { p.PersonName ?? "____________________", p.PersonTitle }.Where(x => !string.IsNullOrWhiteSpace(x))), 2);
            if (ps.PoaIdentity) PdfKit.Labeled(sec, (p.IdKind ?? "Удостоверение личности") + ":", p.IdText ?? "№ ____________ от __________, выдано ____________________", 2);
            PdfKit.Labeled(sec, "На получение от:", p.From ?? "____________________", 2);
            PdfKit.Labeled(sec, "Активов по:", p.ByDocument ?? "____________________", 2);
            PdfKit.Space(sec, 2);

            var cap = sec.AddParagraph("Перечень активов, подлежащих получению");
            cap.Format.Font.Bold = true;
            cap.Format.Alignment = ParagraphAlignment.Center;
            cap.Format.SpaceAfter = Unit.FromMillimeter(1.5);
            var tb = sec.AddTable();
            tb.Borders.Width = 0.6;
            double[] w = { 10, 0, 22, 48 };
            w[1] = PdfKit.Body - w[0] - w[2] - w[3];
            foreach (var x in w) tb.AddColumn(Unit.FromMillimeter(x));
            var th = tb.AddRow();
            th.HeadingFormat = true;
            th.Format.Font.Bold = true;
            th.Format.Font.Size = 8.5;
            string[] heads = { "№", "Наименование активов", "Ед. изм.", "Количество (прописью)" };
            for (var i = 0; i < heads.Length; i++) th.Cells[i].AddParagraph(heads[i]);
            th.Cells[0].Format.Alignment = th.Cells[2].Format.Alignment = ParagraphAlignment.Center;
            foreach (var l in p.Lines)
            {
                var r = tb.AddRow();
                r.Format.Font.Size = 8.5;
                r.TopPadding = r.BottomPadding = Unit.FromMillimeter(0.6);
                r.Cells[0].AddParagraph(l.N.ToString());
                r.Cells[1].AddParagraph(l.Name ?? "");
                r.Cells[2].AddParagraph(l.Unit ?? "");
                r.Cells[3].AddParagraph(Words.QtyWords(l.Qty));
                r.Cells[0].Format.Alignment = r.Cells[2].Format.Alignment = ParagraphAlignment.Center;
            }
            if (p.Lines.Count == 0) { var r = tb.AddRow(); r.Cells[1].AddParagraph(" "); }
            PdfKit.Space(sec, 10);

            var holder = sec.AddParagraph("Подпись лица, получившего доверенность ____________________ удостоверяем.");
            holder.Format.Font.Size = 10;
            PdfKit.Space(sec, 9);
            var (director, _) = ps.DirectorFor(p.Org.Key);
            var sl = PdfKit.AddSignLine(sec, "Руководитель организации  ", "____________________", director ?? "________________", 10, stamp, sign);
            // Печать поверх строки руководителя — до строки главбуха места с запасом
            PdfKit.Space(sec, 14);
            var chief = sec.AddParagraph($"Главный бухгалтер  ____________________   /{ps.ChiefAccountantFor(p.Org.Key) ?? "________________"}/");
            chief.Format.Font.Size = 10;
            PdfKit.Space(sec, 5);
            sec.AddParagraph("М.П.").Format.Font.Size = 10;
            return PdfKit.Render(doc, sl);
        }

        private static void Line(Section s, string text) => s.AddParagraph(text ?? "").Format.SpaceAfter = Unit.FromMillimeter(1.2);
    }
}
