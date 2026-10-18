using System;
using System.Globalization;
using System.Linq;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;

namespace Bilem.Agent.Core.Print
{
    /// <summary>
    /// Акт сверки взаимных расчётов — по образцу Славы: левая половина — наши данные из 1С, правая — пустая (контрагент заполняет сам)
    /// или зеркальная (настройка). Подпись и печать — поверх нашей строки подписи.
    /// </summary>
    public static class ReconciliationPdf
    {
        private static string D(DateTime d) => d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        private static string M(decimal v) => v == 0 ? "" : Words.Money(v);

        public static byte[] Render(ActData a, PrintSettings ps, string logo, string stamp, string sign)
        {
            ps = ps ?? new PrintSettings();
            var org = a.Org.ShortName ?? a.Org.Name;
            var cp = a.CounterpartyShort ?? a.Counterparty;
            var (doc, sec) = PdfKit.NewDoc($"Акт сверки — {cp} — {ReconciliationSource.Period(a.From, a.To)}", "Акт сверки взаимных расчётов", 8.5);

            // --- Шапка: логотип и заголовок
            var head = sec.AddTable();
            head.AddColumn(Unit.FromMillimeter(40));
            head.AddColumn(Unit.FromMillimeter(PdfKit.Body - 80));
            head.AddColumn(Unit.FromMillimeter(40));
            var hr = head.AddRow();
            hr.VerticalAlignment = VerticalAlignment.Center;
            PdfKit.Logo(hr.Cells[0], logo);
            var c = hr.Cells[1];
            var t = c.AddParagraph("АКТ СВЕРКИ ВЗАИМНЫХ РАСЧЁТОВ");
            t.Format.Font.Size = 13; t.Format.Font.Bold = true; t.Format.Alignment = ParagraphAlignment.Center; t.Format.SpaceAfter = Unit.FromMillimeter(1.5);
            Center(c, $"за период с {D(a.From)} по {D(a.To)}");
            Center(c, $"между {org} и {cp}");
            if (!string.IsNullOrWhiteSpace(a.Contract)) Center(c, "по договору " + a.Contract);
            PdfKit.Space(sec, 3);

            var (director, title) = ps.DirectorFor(a.Org.Key);
            var intro = Intro(ps.ActIntro ?? PrintSettings.DefaultActIntro, title ?? "Руководитель", org, director ?? "____________", cp);
            var ip = sec.AddParagraph(intro);
            ip.Format.Font.Size = 8;
            ip.Format.SpaceAfter = Unit.FromMillimeter(3);

            // --- Таблица: 8 колонок, по 4 на сторону
            var tb = sec.AddTable();
            tb.Borders.Width = 0.6;
            double[] half = { 18, 30, 21, 21 };
            var k = PdfKit.Body / 2 / half.Sum();
            foreach (var w in half.Concat(half)) tb.AddColumn(Unit.FromMillimeter(w * k));
            var cap = tb.AddRow();
            cap.Borders.Visible = false;
            cap.Format.Font.Bold = true;
            cap.Format.Font.Size = 9;
            cap.Cells[0].MergeRight = 3; cap.Cells[4].MergeRight = 3;
            cap.Cells[0].AddParagraph($"По данным {org}, KZT").Format.Alignment = ParagraphAlignment.Center;
            cap.Cells[4].AddParagraph($"По данным {cp}, KZT").Format.Alignment = ParagraphAlignment.Center;
            var th = tb.AddRow();
            th.HeadingFormat = true;
            th.Format.Font.Bold = true;
            string[] heads = { "Дата", "Документ", "Дебет", "Кредит" };
            for (var i = 0; i < 8; i++) th.Cells[i].AddParagraph(heads[i % 4]).Format.Alignment = i % 4 >= 2 ? ParagraphAlignment.Right : ParagraphAlignment.Left;

            var mirror = ps.ActMirror;
            foreach (var s in a.Sections)
            {
                if (a.ByContracts)
                {
                    var cr = tb.AddRow();
                    cr.Format.Font.Bold = true;
                    cr.Cells[0].MergeRight = 7;
                    cr.Cells[0].AddParagraph("Договор: " + (s.Contract ?? ""));
                }
                Saldo(tb, $"Сальдо на {D(a.From)}", s.Open, mirror, false);
                foreach (var r in s.Rows)
                {
                    var row = tb.AddRow();
                    Cells(row, 0, D(r.Date), r.Doc, M(r.Dr), M(r.Cr));
                    if (mirror) Cells(row, 4, D(r.Date), r.MirrorDoc, M(r.Cr), M(r.Dr));
                }
                var turn = tb.AddRow();
                turn.Format.Font.Bold = true;
                Cells(turn, 0, "", "Обороты за период", Words.Money(s.TurnDr), Words.Money(s.TurnCr));
                if (mirror) Cells(turn, 4, "", "Обороты за период", Words.Money(s.TurnCr), Words.Money(s.TurnDr));
                Saldo(tb, $"Сальдо на {D(a.To)}", s.Close, mirror, true);
            }
            if (a.ByContracts && a.Sections.Count > 1)
            {
                var tot = tb.AddRow();
                tot.Format.Font.Bold = true;
                Cells(tot, 0, "", "Итого обороты", Words.Money(a.TurnDr), Words.Money(a.TurnCr));
                if (mirror) Cells(tot, 4, "", "Итого обороты", Words.Money(a.TurnCr), Words.Money(a.TurnDr));
                Saldo(tb, $"Итого сальдо на {D(a.To)}", a.Close, mirror, true);
            }
            PdfKit.Space(sec, 4);

            // --- Итог прописью
            var close = a.Close;
            var res = close == 0
                ? $"По данным {org} на {D(a.To)} взаимная задолженность отсутствует."
                : $"По данным {org} на {D(a.To)} задолженность в пользу {(close > 0 ? org : cp)} составляет {Words.Money(Math.Abs(close))} тенге ({Words.Tenge(Math.Abs(close))}).";
            var rp = sec.AddParagraph(res);
            rp.Format.Font.Bold = true;
            rp.Format.Font.Size = 9;
            PdfKit.Space(sec, 10);

            // --- Подписи: наша (с подписью и печатью) и контрагента (мы его не знаем)
            var tab = Unit.FromMillimeter(PdfKit.Body / 2 + 2);
            var from = sec.AddParagraph($"От {org}\tОт {cp}");
            from.Format.TabStops.AddTabStop(tab);
            from.Format.Font.Size = 9.5;
            PdfKit.Space(sec, 6);
            var lead = (title ?? "Руководитель") + " ";
            var sl = PdfKit.AddSignLine(sec, lead, "____________", (director ?? "____________") + "/\t____________ ____________ /____________", 9.5, stamp, sign);
            sl.Paragraph.Format.TabStops.AddTabStop(tab);
            PdfKit.Space(sec, 5);
            var mp = sec.AddParagraph("М.П.\tМ.П.");
            mp.Format.TabStops.AddTabStop(tab);
            mp.Format.Font.Size = 9.5;
            return PdfKit.Render(doc, sl);
        }

        /// <summary>Вступление: {подписант} — должность, {организация}, {фио}, {контрагент}.</summary>
        internal static string Intro(string template, string title, string org, string fio, string cp) =>
            (template ?? "").Replace("{подписант}", title).Replace("{организация}", org).Replace("{фио}", fio).Replace("{контрагент}", cp);

        private static void Center(Cell c, string text)
        {
            var p = c.AddParagraph(text);
            p.Format.Alignment = ParagraphAlignment.Center;
            p.Format.Font.Size = 9;
        }

        private static void Cells(Row r, int from, string date, string doc, string dr, string cr)
        {
            r.Cells[from].AddParagraph(date ?? "");
            r.Cells[from + 1].AddParagraph(doc ?? "");
            r.Cells[from + 2].AddParagraph(dr ?? "").Format.Alignment = ParagraphAlignment.Right;
            r.Cells[from + 3].AddParagraph(cr ?? "").Format.Alignment = ParagraphAlignment.Right;
        }

        /// <summary>Сальдо: &gt; 0 — в дебете (долг контрагента), &lt; 0 — в кредите; у контрагента — наоборот.</summary>
        private static void Saldo(Table tb, string label, decimal v, bool mirror, bool bold)
        {
            var r = tb.AddRow();
            r.Format.Font.Bold = bold;
            var dr = v > 0 ? Words.Money(v) : ""; var cr = v < 0 ? Words.Money(-v) : "";
            if (v == 0) dr = Words.Money(0);
            Cells(r, 0, "", label, dr, cr);
            if (mirror) Cells(r, 4, "", label, v == 0 ? Words.Money(0) : cr, dr);
        }
    }
}
