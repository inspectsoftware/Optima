using System.Globalization;
using System.Text;

namespace Optima.Core.Exports;

/// <summary>
/// A minimal single-font PDF writer for the sessions export: one or more A4 pages of text
/// lines, no dependencies. Enough for a readable digest that opens anywhere; not a general
/// PDF library.
/// </summary>
public static class SimplePdf
{
    private const float PageWidth = 595f;   // A4, points
    private const float PageHeight = 842f;
    private const float Margin = 54f;
    private const float LineHeight = 14f;

    /// <summary>Renders the lines into a minimal, valid PDF document.</summary>
    public static byte[] Render(IReadOnlyList<string> lines, string title)
    {
        var pages = Paginate(lines);
        var pdf = new StringBuilder();
        var objects = new List<string>();
        var pageObjectIds = new List<int>();

        // Object 1: catalog. Object 2: pages tree. Objects 3..: font, then one page + content pair each.
        var fontId = 3;
        var firstPageId = 4;

        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        var kids = string.Join(" ", Enumerable.Range(0, pages.Count).Select(i => $"{firstPageId + i * 2} 0 R"));
        objects.Add($"<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>");

        for (var i = 0; i < pages.Count; i++)
        {
            var pageId = firstPageId + i * 2;
            var contentId = pageId + 1;
            pageObjectIds.Add(pageId);
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidth.ToString(CultureInfo.InvariantCulture)} {PageHeight.ToString(CultureInfo.InvariantCulture)}] /Resources << /Font << /F1 {fontId} 0 R >> >> /Contents {contentId} 0 R >>");
            objects.Add(BuildContentStream(pages[i], title, i + 1, pages.Count));
        }

        pdf.Append("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xrefStart = pdf.Length;
        pdf.Append($"xref\n0 {objects.Count + 1}\n");
        pdf.Append("0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefStart}\n%%EOF");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }

    private static List<List<string>> Paginate(IReadOnlyList<string> lines)
    {
        var perPage = (int)((PageHeight - 2 * Margin - 2 * LineHeight) / LineHeight);
        var pages = new List<List<string>>();
        for (var i = 0; i < lines.Count; i += perPage)
        {
            pages.Add(lines.Skip(i).Take(perPage).ToList());
        }
        if (pages.Count == 0)
        {
            pages.Add([]);
        }
        return pages;
    }

    private static string BuildContentStream(List<string> lines, string title, int pageNumber, int pageCount)
    {
        var content = new StringBuilder();
        content.Append("BT /F1 16 Tf ").Append(Margin).Append(' ').Append(PageHeight - Margin).Append(" Td (")
            .Append(Escape(title)).Append(") Tj ET\n");
        content.Append("BT /F1 9 Tf ").Append(PageWidth - Margin - 60).Append(' ').Append(PageHeight - Margin)
            .Append(" Td (page ").Append(pageNumber).Append('/').Append(pageCount).Append(") Tj ET\n");

        var y = PageHeight - Margin - 2 * LineHeight;
        foreach (var line in lines)
        {
            content.Append("BT /F1 9 Tf ").Append(Margin).Append(' ').Append(y.ToString("F0", CultureInfo.InvariantCulture))
                .Append(" Td (").Append(Escape(line)).Append(") Tj ET\n");
            y -= LineHeight;
        }
        return $"<< /Length {content.Length} >>\nstream\n{content}endstream";
    }

    private static string Escape(string text)
        => text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
}
