using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace RSMatrix.Desktop.Content;

/// <summary>
/// Turns org.matrix.custom.html into a <see cref="MessageDocument"/>. Only the tags and attributes the spec
/// suggests are interpreted. Other tags contribute their text, and the content of tags like script is dropped.
/// </summary>
public static partial class MatrixHtmlParser
{
    private const int MaxDepth = 100;

    // Elements whose content is never meant to be shown as message text.
    private static readonly HashSet<string> DroppedElements = new(StringComparer.Ordinal)
    {
        "mx-reply", "script", "style", "head", "title", "iframe", "object", "embed", "noscript",
        "template", "svg", "math", "textarea", "select", "input", "button", "form", "audio", "video",
    };

    private static readonly HashSet<string> AllowedLinkSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "https", "http", "ftp", "mailto", "magnet",
    };

    /// <summary>Custom emoji are sent as img tags; those at most this high are rendered inline with the text.</summary>
    internal const int InlineImageMaxHeight = 48;

    public static MessageDocument Parse(string html)
    {
        var parser = new HtmlParser();
        var document = parser.ParseDocument("");
        var nodes = parser.ParseFragment(html, document.Body!);
        var builder = new BlockBuilder();
        foreach (var node in nodes)
            Walk(node, builder, default, 0);
        return new MessageDocument(builder.Build());
    }

    private static void WalkChildren(INode node, BlockBuilder builder, InlineFormat format, int depth)
    {
        foreach (var child in node.ChildNodes)
            Walk(child, builder, format, depth + 1);
    }

    private static void Walk(INode node, BlockBuilder builder, InlineFormat format, int depth)
    {
        if (depth > MaxDepth)
            return;
        if (node is IText text)
        {
            builder.AddText(text.Data, format);
            return;
        }
        if (node is not IElement element)
            return;

        var tag = element.LocalName;
        if (DroppedElements.Contains(tag))
            return;

        switch (tag)
        {
            case "br":
                builder.AddLineBreak();
                break;
            case "b" or "strong":
                WalkChildren(element, builder, format.With(InlineStyle.Bold), depth);
                break;
            case "i" or "em":
                WalkChildren(element, builder, format.With(InlineStyle.Italic), depth);
                break;
            case "u":
                WalkChildren(element, builder, format.With(InlineStyle.Underline), depth);
                break;
            case "s" or "del" or "strike":
                WalkChildren(element, builder, format.With(InlineStyle.Strikethrough), depth);
                break;
            case "sup":
                WalkChildren(element, builder, format.With(InlineStyle.Superscript), depth);
                break;
            case "sub":
                WalkChildren(element, builder, format.With(InlineStyle.Subscript), depth);
                break;
            case "code":
                builder.AddText(element.TextContent, format.With(InlineStyle.Code));
                break;
            case "a":
                WalkChildren(element, builder, WithLink(format, element.GetAttribute("href")), depth);
                break;
            case "span" or "font":
                WalkChildren(element, builder, WithSpanAttributes(format, element), depth);
                break;
            case "img":
                AddImage(element, builder);
                break;
            case "p" or "div":
                builder.FlushParagraph();
                WalkChildren(element, builder, format, depth);
                builder.FlushParagraph();
                break;
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                AddHeading(element, builder, format, depth);
                break;
            case "blockquote":
                builder.AddBlock(new QuoteBlock(BuildBlocks(element, format, depth)));
                break;
            case "pre":
                builder.AddBlock(CreateCodeBlock(element));
                break;
            case "ul" or "ol":
                builder.AddBlock(CreateList(element, format, depth));
                break;
            case "hr":
                builder.AddBlock(new RuleBlock());
                break;
            case "table":
                AddTable(element, builder, format, depth);
                break;
            case "summary":
                builder.FlushParagraph();
                WalkChildren(element, builder, format.With(InlineStyle.Bold), depth);
                builder.FlushParagraph();
                break;
            default:
                // details, li outside a list, and tags outside the allowed set: keep their text.
                WalkChildren(element, builder, format, depth);
                break;
        }
    }

    private static IReadOnlyList<MessageBlock> BuildBlocks(INode node, InlineFormat format, int depth)
    {
        var builder = new BlockBuilder();
        WalkChildren(node, builder, format, depth);
        return builder.Build();
    }

    private static IReadOnlyList<MessageInline> BuildInlines(INode node, InlineFormat format, int depth)
    {
        // Block content inside inline-only places like headings and table cells is joined with line breaks.
        var inlines = new List<MessageInline>();
        foreach (var block in BuildBlocks(node, format, depth))
        {
            if (block is not ParagraphBlock paragraph)
                continue;
            if (inlines.Count > 0)
                inlines.Add(new TextInline("\n"));
            inlines.AddRange(paragraph.Inlines);
        }
        return inlines;
    }

    private static void AddHeading(IElement element, BlockBuilder builder, InlineFormat format, int depth)
    {
        var inlines = BuildInlines(element, format, depth);
        if (inlines.Count > 0)
            builder.AddBlock(new HeadingBlock(element.LocalName[1] - '0', inlines));
    }

    private static CodeBlock CreateCodeBlock(IElement pre)
    {
        var code = pre.TextContent.ReplaceLineEndings("\n");
        if (code.EndsWith('\n'))
            code = code[..^1];
        var language = pre.QuerySelector("code")?.ClassList
            .FirstOrDefault(c => c.StartsWith("language-", StringComparison.Ordinal))?["language-".Length..];
        return new CodeBlock(code, string.IsNullOrEmpty(language) ? null : language);
    }

    private static ListBlock CreateList(IElement list, InlineFormat format, int depth)
    {
        var isOrdered = list.LocalName == "ol";
        var start = 1;
        if (isOrdered && int.TryParse(list.GetAttribute("start"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedStart))
            start = parsedStart;
        var items = list.Children
            .Where(child => child.LocalName == "li")
            .Select(item => new ListItem(BuildBlocks(item, format, depth + 1)))
            .ToList();
        return new ListBlock(isOrdered, start, items);
    }

    private static void AddTable(IElement table, BlockBuilder builder, InlineFormat format, int depth)
    {
        if (table.QuerySelector("caption") is { } caption)
        {
            builder.FlushParagraph();
            WalkChildren(caption, builder, format.With(InlineStyle.Bold), depth);
            builder.FlushParagraph();
        }

        // Rows of nested tables are not picked up here; they show up as text inside their cell.
        var rows = table.QuerySelectorAll("tr")
            .Where(row => row.Closest("table") == table)
            .Select(row =>
            {
                var cells = row.Children.Where(c => c.LocalName is "td" or "th").ToList();
                var isHeader = cells.Count > 0 && cells.All(c => c.LocalName == "th");
                return new TableRow(isHeader, cells.Select(c => BuildInlines(c, format, depth + 2)).ToList());
            })
            .Where(row => row.Cells.Count > 0)
            .ToList();
        if (rows.Count > 0)
            builder.AddBlock(new TableBlock(rows));
    }

    private static void AddImage(IElement image, BlockBuilder builder)
    {
        var source = image.GetAttribute("src");
        var description = image.GetAttribute("alt") ?? image.GetAttribute("title");
        if (source == null || !source.StartsWith("mxc://", StringComparison.Ordinal))
        {
            // Images from anywhere else could track the reader, so only their description is shown.
            if (!string.IsNullOrWhiteSpace(description))
                builder.AddText(description, default);
            return;
        }

        var width = ParseDimension(image.GetAttribute("width"));
        var height = ParseDimension(image.GetAttribute("height"));
        var media = new MediaReference(source, description, width, height);
        if (height is > 0 and <= InlineImageMaxHeight)
            builder.AddInline(new ImageInline(media));
        else
            builder.AddBlock(new ImageBlock(media));
    }

    private static int? ParseDimension(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && result > 0
            ? Math.Min(result, 4096)
            : null;

    private static InlineFormat WithLink(InlineFormat format, string? href)
    {
        if (href == null || !Uri.TryCreate(href, UriKind.Absolute, out var uri) || !AllowedLinkSchemes.Contains(uri.Scheme))
            return format;

        var result = format with { Link = uri.AbsoluteUri };
        return IsUserMention(uri) ? result.With(InlineStyle.Mention) : result;
    }

    private static bool IsUserMention(Uri uri)
        => uri.Host == "matrix.to" && uri.Fragment.StartsWith("#/@", StringComparison.Ordinal);

    private static InlineFormat WithSpanAttributes(InlineFormat format, IElement span)
    {
        var color = span.GetAttribute("data-mx-color") ?? (span.LocalName == "font" ? span.GetAttribute("color") : null);
        var background = span.GetAttribute("data-mx-bg-color");
        var result = format;
        if (IsHexColor(color))
            result = result with { Color = color };
        if (IsHexColor(background))
            result = result with { BackgroundColor = background };
        if (span.HasAttribute("data-mx-spoiler"))
            result = result.With(InlineStyle.Spoiler);
        return result;
    }

    private static bool IsHexColor(string? value) => value != null && HexColor().IsMatch(value);

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();
}
