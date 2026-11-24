using RSMatrix.Desktop.Content;

namespace RSMatrix.Desktop.Tests;

public class MatrixHtmlParserTests
{
    [Test]
    public async Task Scripts_And_Event_Handlers_Are_Dropped()
    {
        var document = MatrixHtmlParser.Parse("<script>alert('x')</script><p onclick=\"steal()\">Hi <style>p{}</style>there</p>");

        await Assert.That(document.Blocks.Count).IsEqualTo(1);
        await Assert.That(TextOf(document.Blocks[0])).IsEqualTo("Hi there");
    }

    [Test]
    [Arguments("javascript:alert(1)")]
    [Arguments("data:text/html,<b>x</b>")]
    [Arguments("/relative/path")]
    [Arguments("file:///etc/passwd")]
    public async Task Links_With_Disallowed_Targets_Stay_Plain_Text(string href)
    {
        var document = MatrixHtmlParser.Parse($"<a href=\"{href}\">click</a>");

        var inline = (TextInline)((ParagraphBlock)document.Blocks[0]).Inlines.Single();
        await Assert.That(inline.Text).IsEqualTo("click");
        await Assert.That(inline.Link).IsNull();
    }

    [Test]
    public async Task User_Links_To_Matrix_To_Are_Mentions()
    {
        var document = MatrixHtmlParser.Parse("<a href=\"https://matrix.to/#/@alice:example.org\">Alice</a>: hi");

        var mention = (TextInline)((ParagraphBlock)document.Blocks[0]).Inlines[0];
        await Assert.That(mention.Style.HasFlag(InlineStyle.Mention)).IsTrue();
        await Assert.That(mention.Link).IsEqualTo("https://matrix.to/#/@alice:example.org");
    }

    [Test]
    public async Task Images_Load_Only_From_The_Homeserver()
    {
        var document = MatrixHtmlParser.Parse(
            "<p>a <img src=\"https://tracker.example/p.png\" alt=\"[pixel]\"> b <img src=\"mxc://example.org/emoji\" height=\"24\" alt=\":)\"></p>" +
            "<img src=\"mxc://example.org/photo\" width=\"800\" height=\"600\">");

        var paragraph = (ParagraphBlock)document.Blocks[0];
        await Assert.That(TextOf(paragraph)).IsEqualTo("a [pixel] b ");
        await Assert.That(paragraph.Inlines.OfType<ImageInline>().Single().Image.MxcUri).IsEqualTo("mxc://example.org/emoji");
        var image = (ImageBlock)document.Blocks[1];
        await Assert.That(image.Image).IsEqualTo(new MediaReference("mxc://example.org/photo", null, 800, 600));
    }

    [Test]
    public async Task Reply_Fallbacks_Are_Stripped()
    {
        var document = MatrixHtmlParser.Parse("<mx-reply><blockquote>quoted original</blockquote></mx-reply>The answer");

        await Assert.That(document.Blocks.Count).IsEqualTo(1);
        await Assert.That(TextOf(document.Blocks[0])).IsEqualTo("The answer");
    }

    [Test]
    public async Task Whitespace_Collapses_Outside_Of_Code()
    {
        var document = MatrixHtmlParser.Parse("<p>one\n   two</p>\n<p>  three<br>four </p><pre><code class=\"language-cs\">x\n    y\n</code></pre>");

        await Assert.That(document.Blocks.Count).IsEqualTo(3);
        await Assert.That(TextOf(document.Blocks[0])).IsEqualTo("one two");
        await Assert.That(TextOf(document.Blocks[1])).IsEqualTo("three\nfour");
        await Assert.That(document.Blocks[2]).IsEqualTo(new CodeBlock("x\n    y", "cs"));
    }

    [Test]
    public async Task Nested_Formatting_Combines_Styles()
    {
        var document = MatrixHtmlParser.Parse("<b>bold <i>both</i></b> <code>c</code>");

        var inlines = ((ParagraphBlock)document.Blocks[0]).Inlines.Cast<TextInline>().ToList();
        await Assert.That(inlines[0]).IsEqualTo(new TextInline("bold ", InlineStyle.Bold));
        await Assert.That(inlines[1]).IsEqualTo(new TextInline("both", InlineStyle.Bold | InlineStyle.Italic));
        await Assert.That(inlines[3]).IsEqualTo(new TextInline("c", InlineStyle.Code));
    }

    [Test]
    public async Task Ordered_Lists_Keep_Their_Start()
    {
        var document = MatrixHtmlParser.Parse("<ol start=\"3\"><li>three</li><li>four</li></ol>");

        var list = (ListBlock)document.Blocks[0];
        await Assert.That(list.IsOrdered).IsTrue();
        await Assert.That(list.Start).IsEqualTo(3);
        await Assert.That(list.Items.Select(i => TextOf(i.Blocks[0])).ToList()).IsEquivalentTo(["three", "four"]);
    }

    [Test]
    public async Task Only_Hex_Colors_Are_Applied()
    {
        var document = MatrixHtmlParser.Parse("<span data-mx-color=\"#00ff00\">green</span><span data-mx-color=\"red;font-size:99px\">red</span>");

        var inlines = ((ParagraphBlock)document.Blocks[0]).Inlines.Cast<TextInline>().ToList();
        await Assert.That(inlines[0].Color).IsEqualTo("#00ff00");
        await Assert.That(inlines[1].Color).IsNull();
    }

    [Test]
    public async Task Deep_Nesting_Is_Cut_Off()
    {
        var html = string.Concat(Enumerable.Repeat("<div>", 150)) + "deep" + string.Concat(Enumerable.Repeat("</div>", 150));

        var document = MatrixHtmlParser.Parse(html);

        await Assert.That(document.Blocks.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Urls_In_Text_Become_Links()
    {
        var document = MatrixHtmlParser.Parse("See https://example.org/docs. <code>https://not.a/link</code>");

        var inlines = ((ParagraphBlock)document.Blocks[0]).Inlines.Cast<TextInline>().ToList();
        await Assert.That(inlines[1]).IsEqualTo(new TextInline("https://example.org/docs", Link: "https://example.org/docs"));
        await Assert.That(inlines.Last().Link).IsNull();
    }

    internal static string TextOf(MessageBlock block)
        => string.Concat(((ParagraphBlock)block).Inlines.OfType<TextInline>().Select(i => i.Text));
}
