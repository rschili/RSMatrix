using RSMatrix.Desktop.Content;

namespace RSMatrix.Desktop.Tests;

public class PlainTextParserTests
{
    [Test]
    public async Task Line_Breaks_And_Spaces_Are_Kept()
    {
        var document = PlainTextParser.Parse("one\r\n  two\n\nthree\n");

        await Assert.That(MatrixHtmlParserTests.TextOf(document.Blocks.Single())).IsEqualTo("one\n  two\n\nthree");
    }

    [Test]
    [Arguments("Read https://en.wikipedia.org/wiki/Matrix_(protocol).", "https://en.wikipedia.org/wiki/Matrix_(protocol)")]
    [Arguments("(see https://example.org/a)", "https://example.org/a")]
    [Arguments("Is it https://example.org/?", "https://example.org/")]
    public async Task Links_Exclude_Trailing_Punctuation(string text, string expectedLink)
    {
        var document = PlainTextParser.Parse(text);

        var link = ((ParagraphBlock)document.Blocks.Single()).Inlines.Cast<TextInline>().Single(i => i.Link != null);
        await Assert.That(link.Link).IsEqualTo(expectedLink);
        await Assert.That(link.Text).IsEqualTo(expectedLink);
    }

    [Test]
    public async Task Preview_Is_A_Single_Short_Line()
    {
        await Assert.That(PlainTextParser.ToPreview("  first\n\n second  line ", 12)).IsEqualTo("first second");
    }
}
