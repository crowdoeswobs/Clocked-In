using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace WorkHours
{
    /// <summary>Exports a date range to a real .xlsx file or to a Markdown table (pastes into Notion as a table).</summary>
    public static class Exporter
    {
        public class Row
        {
            public DateTime Date;
            public DateTime? First, Last;
            public TimeSpan Worked, Net;
            public int BreakMin;
        }

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static List<Row> Rows(Job s, DateTime from, DateTime to)
        {
            var now = DateTime.Now;
            var list = new List<Row>();
            for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
            {
                var r = new Row { Date = d, Worked = s.Worked(d, now), Net = s.Net(d, now), BreakMin = s.BreakMinutes(d) };
                foreach (var ses in s.SessionsOn(d))
                {
                    var st = ses.Start < d ? d : ses.Start;
                    var en = ses.End ?? now;
                    if (en > d.AddDays(1)) en = d.AddDays(1);
                    if (!r.First.HasValue || st < r.First.Value) r.First = st;
                    if (!r.Last.HasValue || en > r.Last.Value) r.Last = en;
                }
                list.Add(r);
            }
            return list;
        }

        // ------------------------------------------------------------ Notion / Markdown

        public static string Markdown(Job s, DateTime from, DateTime to, string title)
        {
            var rows = Rows(s, from, to);
            bool money = s.HourlyRate > 0;
            var sb = new StringBuilder();
            sb.AppendLine("## " + title);
            sb.AppendLine();
            sb.Append("| Date | Day | Start | End | Worked | Breaks | Total |");
            if (money) sb.Append(" Earnings |");
            sb.AppendLine();
            sb.Append("|---|---|---|---|---|---|---|");
            if (money) sb.Append("---|");
            sb.AppendLine();
            var tot = TimeSpan.Zero; var totW = TimeSpan.Zero; int totB = 0;
            foreach (var r in rows)
            {
                bool any = r.First.HasValue || r.BreakMin > 0;
                sb.Append("| " + r.Date.ToString("yyyy-MM-dd", Inv) + " | " + r.Date.ToString("ddd", Store.En) + " | ");
                sb.Append((r.First.HasValue ? r.First.Value.ToString("HH:mm") : "") + " | ");
                sb.Append((r.Last.HasValue ? r.Last.Value.ToString("HH:mm") : "") + " | ");
                sb.Append((any ? Fmt2.Dur(r.Worked) : "") + " | ");
                sb.Append((r.BreakMin > 0 ? Fmt2.Dur(TimeSpan.FromMinutes(r.BreakMin)) : "") + " | ");
                sb.Append((any ? Fmt2.Dur(r.Net) : "") + " |");
                if (money) sb.Append(" " + (any ? s.Money(r.Net) : "") + " |");
                sb.AppendLine();
                tot += r.Net; totW += r.Worked; totB += r.BreakMin;
            }
            sb.Append("| **Total** | | | | **" + Fmt2.Dur(totW) + "** | **" + Fmt2.Dur(TimeSpan.FromMinutes(totB)) + "** | **" + Fmt2.Dur(tot) + "** |");
            if (money) sb.Append(" **" + s.Money(tot) + "** |");
            sb.AppendLine();
            if (money)
            {
                sb.AppendLine();
                sb.AppendLine("Hourly rate: " + Fmt2.Money(s.HourlyRate, s.Currency));
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------ CSV

        /// <summary>Plain comma-separated data (UTF-8 with BOM so Excel reads symbols correctly). Returns the path.</summary>
        public static string Csv(Job s, DateTime from, DateTime to, string fileBase)
        {
            Directory.CreateDirectory(ExportsDir);
            string path = Path.Combine(ExportsDir, fileBase + ".csv");
            for (int i = 2; File.Exists(path) && IsLocked(path); i++)
                path = Path.Combine(ExportsDir, fileBase + " (" + i + ").csv");

            var rows = Rows(s, from, to);
            bool money = s.HourlyRate > 0;
            var sb = new StringBuilder();
            sb.Append("Job,Date,Day,Week,Start,End,Worked,Breaks (min),Total,Hours");
            if (money) sb.Append(",Rate,Earnings,Currency");
            sb.Append("\r\n");
            double totH = 0;
            foreach (var r in rows)
            {
                bool any = r.First.HasValue || r.BreakMin > 0;
                double hours = Math.Round(Math.Floor(r.Net.TotalMinutes) / 60.0, 2);
                totH += hours;
                sb.Append(Q(s.Name)).Append(',')
                  .Append(r.Date.ToString("yyyy-MM-dd", Inv)).Append(',')
                  .Append(r.Date.ToString("ddd", Store.En)).Append(',')
                  .Append(Fmt2.IsoWeek(r.Date).ToString(Inv)).Append(',')
                  .Append(r.First.HasValue ? r.First.Value.ToString("HH:mm", Inv) : "").Append(',')
                  .Append(r.Last.HasValue ? r.Last.Value.ToString("HH:mm", Inv) : "").Append(',')
                  .Append(any ? HM(r.Worked) : "").Append(',')
                  .Append(r.BreakMin.ToString(Inv)).Append(',')
                  .Append(any ? HM(r.Net) : "").Append(',')
                  .Append(hours.ToString("0.00", Inv));
                if (money)
                    sb.Append(',').Append(s.HourlyRate.ToString("0.00", Inv))
                      .Append(',').Append((hours * s.HourlyRate).ToString("0.00", Inv))
                      .Append(',').Append(Q(s.Currency));
                sb.Append("\r\n");
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return path;
        }

        static string HM(TimeSpan t)
        {
            int m = (int)Math.Floor(t.TotalMinutes);
            return (m / 60).ToString(Inv) + ":" + (m % 60).ToString("00", Inv);
        }

        static string Q(string v)
        {
            v = v ?? "";
            if (v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0) return "\"" + v.Replace("\"", "\"\"") + "\"";
            return v;
        }

        // ------------------------------------------------------------ Excel (.xlsx)

        public static string ExportsDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Clocked In"); }
        }

        /// <summary>Writes the file and returns its path.</summary>
        public static string Xlsx(Job s, DateTime from, DateTime to, string title, string fileBase)
        {
            Directory.CreateDirectory(ExportsDir);
            string path = Path.Combine(ExportsDir, fileBase + ".xlsx");
            for (int i = 2; File.Exists(path) && IsLocked(path); i++)
                path = Path.Combine(ExportsDir, fileBase + " (" + i + ").xlsx");

            var rows = Rows(s, from, to);
            if (File.Exists(path)) File.Delete(path);
            using (var fs = new FileStream(path, FileMode.CreateNew))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                Put(zip, "[Content_Types].xml", ContentTypes);
                Put(zip, "_rels/.rels", RootRels);
                Put(zip, "xl/workbook.xml", Workbook);
                Put(zip, "xl/_rels/workbook.xml.rels", WorkbookRels);
                Put(zip, "xl/styles.xml", Styles);
                Put(zip, "xl/worksheets/sheet1.xml", Sheet(s, rows, title));
            }
            return path;
        }

        static bool IsLocked(string path)
        {
            try { using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } return false; }
            catch { return true; }
        }

        static void Put(ZipArchive zip, string name, string content)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false))) w.Write(content);
        }

        static string X(string s) { return SecurityElement.Escape(s ?? ""); }
        static string N(double v) { return v.ToString("R", Inv); }
        static double Serial(DateTime d) { return (d - new DateTime(1899, 12, 30)).TotalDays; }

        static string Str(string cell, string text, int style)
        {
            return "<c r=\"" + cell + "\" t=\"inlineStr\"" + (style > 0 ? " s=\"" + style + "\"" : "") + "><is><t>" + X(text) + "</t></is></c>";
        }
        static string Num(string cell, double v, int style)
        {
            return "<c r=\"" + cell + "\"" + (style > 0 ? " s=\"" + style + "\"" : "") + "><v>" + N(v) + "</v></c>";
        }
        static string Formula(string cell, string f, int style)
        {
            return "<c r=\"" + cell + "\" s=\"" + style + "\"><f>" + X(f) + "</f></c>";
        }
        static string Empty(string cell, int style) { return "<c r=\"" + cell + "\" s=\"" + style + "\"/>"; }

        // style ids (see Styles below)
        const int SBold = 1, SDate = 2, SDur = 3, SDec = 4, STime = 5, SMoney = 6, SHead = 7,
                  STotDur = 8, STotDec = 9, STotMoney = 10, STitle = 11, STotText = 12;

        static string Sheet(Job s, List<Row> rows, string title)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            sb.Append("<sheetViews><sheetView workbookViewId=\"0\" showGridLines=\"0\"/></sheetViews>");
            sb.Append("<cols>");
            double[] widths = { 12, 7, 7, 8, 8, 10, 10, 10, 10, 13 };
            for (int i = 0; i < widths.Length; i++)
                sb.Append("<col min=\"" + (i + 1) + "\" max=\"" + (i + 1) + "\" width=\"" + N(widths[i]) + "\" customWidth=\"1\"/>");
            sb.Append("</cols><sheetData>");

            sb.Append("<row r=\"1\">" + Str("A1", title, STitle) + "</row>");
            sb.Append("<row r=\"2\">" + Str("A2", "Hourly rate", SBold) + Num("B2", s.HourlyRate, SMoney) + Str("C2", s.Currency, 0) + "</row>");

            string[] heads = { "Date", "Day", "Week", "Start", "End", "Worked", "Breaks", "Total", "Hours", "Earnings" };
            sb.Append("<row r=\"4\">");
            for (int i = 0; i < heads.Length; i++) sb.Append(Str(((char)('A' + i)) + "4", heads[i], SHead));
            sb.Append("</row>");

            int r = 5;
            foreach (var row in rows)
            {
                string n = r.ToString(Inv);
                sb.Append("<row r=\"" + n + "\">");
                sb.Append(Num("A" + n, Serial(row.Date), SDate));
                sb.Append(Str("B" + n, row.Date.ToString("ddd", Store.En), 0));
                sb.Append(Num("C" + n, Fmt2.IsoWeek(row.Date), 0));
                if (row.First.HasValue) sb.Append(Num("D" + n, row.First.Value.TimeOfDay.TotalDays, STime));
                if (row.Last.HasValue) sb.Append(Num("E" + n, row.Last.Value == row.Date.AddDays(1) ? 0.99999 : row.Last.Value.TimeOfDay.TotalDays, STime));
                sb.Append(Num("F" + n, Math.Floor(row.Worked.TotalMinutes) / 1440.0, SDur));
                sb.Append(Num("G" + n, row.BreakMin / 1440.0, SDur));
                sb.Append(Formula("H" + n, "MAX(0,F" + n + "-G" + n + ")", SDur));
                sb.Append(Formula("I" + n, "ROUND(H" + n + "*24,2)", SDec));
                sb.Append(Formula("J" + n, "I" + n + "*$B$2", SMoney));
                sb.Append("</row>");
                r++;
            }
            int last = r - 1;
            string t = r.ToString(Inv);
            sb.Append("<row r=\"" + t + "\">");
            sb.Append(Str("A" + t, "Total", STotText));
            sb.Append(Empty("B" + t, STotText) + Empty("C" + t, STotText) + Empty("D" + t, STotText) + Empty("E" + t, STotText));
            sb.Append(Formula("F" + t, "SUM(F5:F" + last + ")", STotDur));
            sb.Append(Formula("G" + t, "SUM(G5:G" + last + ")", STotDur));
            sb.Append(Formula("H" + t, "SUM(H5:H" + last + ")", STotDur));
            sb.Append(Formula("I" + t, "SUM(I5:I" + last + ")", STotDec));
            sb.Append(Formula("J" + t, "SUM(J5:J" + last + ")", STotMoney));
            sb.Append("</row>");
            sb.Append("</sheetData></worksheet>");
            return sb.ToString();
        }

        const string Head = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";

        const string ContentTypes = Head +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
            "</Types>";

        const string RootRels = Head +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "</Relationships>";

        const string Workbook = Head +
            "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            "<sheets><sheet name=\"Hours\" sheetId=\"1\" r:id=\"rId1\"/></sheets><calcPr fullCalcOnLoad=\"1\"/></workbook>";

        const string WorkbookRels = Head +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
            "</Relationships>";

        const string Styles = Head +
            "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<numFmts count=\"5\">" +
            "<numFmt numFmtId=\"164\" formatCode=\"yyyy-mm-dd\"/>" +
            "<numFmt numFmtId=\"165\" formatCode=\"[h]:mm\"/>" +
            "<numFmt numFmtId=\"166\" formatCode=\"0.00\"/>" +
            "<numFmt numFmtId=\"167\" formatCode=\"hh:mm\"/>" +
            "<numFmt numFmtId=\"168\" formatCode=\"#,##0.00\"/>" +
            "</numFmts>" +
            "<fonts count=\"3\">" +
            "<font><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
            "<font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
            "<font><b/><sz val=\"14\"/><name val=\"Calibri\"/></font>" +
            "</fonts>" +
            "<fills count=\"3\">" +
            "<fill><patternFill patternType=\"none\"/></fill>" +
            "<fill><patternFill patternType=\"gray125\"/></fill>" +
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFE8EEF7\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
            "</fills>" +
            "<borders count=\"3\">" +
            "<border><left/><right/><top/><bottom/><diagonal/></border>" +
            "<border><left/><right/><top/><bottom style=\"thin\"><color rgb=\"FF8A8A8A\"/></bottom><diagonal/></border>" +
            "<border><left/><right/><top style=\"thin\"><color rgb=\"FF8A8A8A\"/></top><bottom/><diagonal/></border>" +
            "</borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"13\">" +
            "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +                                       // 0
            "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +                       // 1 bold
            "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +             // 2 date
            "<xf numFmtId=\"165\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +             // 3 duration
            "<xf numFmtId=\"166\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +             // 4 decimal
            "<xf numFmtId=\"167\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +             // 5 time
            "<xf numFmtId=\"168\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +             // 6 money
            "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\"/>" + // 7 header
            "<xf numFmtId=\"165\" fontId=\"1\" fillId=\"0\" borderId=\"2\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyBorder=\"1\"/>" + // 8
            "<xf numFmtId=\"166\" fontId=\"1\" fillId=\"0\" borderId=\"2\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyBorder=\"1\"/>" + // 9
            "<xf numFmtId=\"168\" fontId=\"1\" fillId=\"0\" borderId=\"2\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyBorder=\"1\"/>" + // 10
            "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +                       // 11 title
            "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"2\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\"/>" +     // 12 total text
            "</cellXfs>" +
            "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
            "</styleSheet>";
    }
}
