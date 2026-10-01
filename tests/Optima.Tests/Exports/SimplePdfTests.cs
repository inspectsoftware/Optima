using System.Text;
using Optima.Core.Exports;
using Xunit;

namespace Optima.Tests.Exports;

public sealed class SimplePdfTests
{
    [Fact]
    public void RendersAValidSinglePageDocument()
    {
        var bytes = SimplePdf.Render(["Optima weekly digest", "3 sessions · 2W-1L"], "Optima sessions");

        var text = Encoding.Latin1.GetString(bytes);
        Assert.StartsWith("%PDF-1.4", text);
        Assert.EndsWith("%%EOF", text.TrimEnd());
        Assert.Contains("/Type /Catalog", text);
        Assert.Contains("/Count 1", text);
        Assert.Contains("Optima weekly digest", text);
        Assert.Contains("2W-1L", text);
    }

    [Fact]
    public void ManyLinesProduceMultiplePages()
    {
        // A4 minus margins at 14pt leading fits 50 lines per page; 200 lines means 4 pages.
        var lines = Enumerable.Range(0, 200).Select(i => "session row " + i).ToList();
        var bytes = SimplePdf.Render(lines, "Optima sessions");

        var text = Encoding.Latin1.GetString(bytes);
        Assert.Contains("/Count 4", text);
        Assert.Contains("page 4/4", text);
        Assert.Contains("page 1/4", text);
    }

    [Fact]
    public void ParenthesesInTextAreEscaped()
    {
        var bytes = SimplePdf.Render(["win rate 60% (ranked only)"], "t");
        var text = Encoding.Latin1.GetString(bytes);
        Assert.Contains("\\(ranked only\\)", text);
    }
}
