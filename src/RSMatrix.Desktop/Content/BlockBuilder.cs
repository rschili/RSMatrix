using System.Text.RegularExpressions;

namespace RSMatrix.Desktop.Content;

/// <summary>Collects inlines into paragraphs and paragraphs into blocks, with HTML-like whitespace handling.</summary>
internal sealed partial class BlockBuilder
{
    private readonly List<MessageBlock> _blocks = [];
    private List<MessageInline> _inlines = [];

    public void AddText(string text, InlineFormat format, bool preserveWhitespace = false)
    {
        if (!preserveWhitespace)
        {
            text = CollapsibleWhitespace().Replace(text, " ");
            if (text.StartsWith(' ') && EndsWithBreakOrSpace())
                text = text[1..];
        }
        if (text.Length == 0)
            return;

        if (format.Link != null || format.Style.HasFlag(InlineStyle.Code))
        {
            AddInline(format.ToInline(text));
            return;
        }

        foreach (var (segment, link) in Linkifier.Split(text))
            AddInline((format with { Link = link }).ToInline(segment));
    }

    public void AddLineBreak() => AddInline(new TextInline("\n"));

    public void AddInline(MessageInline inline)
    {
        // Merge runs with identical formatting, so renderers get fewer, longer runs.
        if (inline is TextInline text && _inlines.Count > 0 && _inlines[^1] is TextInline previous &&
            previous with { Text = "" } == text with { Text = "" })
        {
            _inlines[^1] = previous with { Text = previous.Text + text.Text };
            return;
        }
        _inlines.Add(inline);
    }

    public void AddBlock(MessageBlock block)
    {
        FlushParagraph();
        _blocks.Add(block);
    }

    public void FlushParagraph()
    {
        TrimTrailingWhitespace();
        if (_inlines.Count > 0)
            _blocks.Add(new ParagraphBlock(_inlines));
        _inlines = [];
    }

    public IReadOnlyList<MessageBlock> Build()
    {
        FlushParagraph();
        return _blocks;
    }

    private bool EndsWithBreakOrSpace()
        => _inlines.Count == 0 || _inlines[^1] is TextInline { Text: var t } && (t.EndsWith(' ') || t.EndsWith('\n'));

    private void TrimTrailingWhitespace()
    {
        while (_inlines.Count > 0 && _inlines[^1] is TextInline last)
        {
            var trimmed = last.Text.TrimEnd(' ', '\n');
            if (trimmed.Length > 0)
            {
                _inlines[^1] = last with { Text = trimmed };
                return;
            }
            _inlines.RemoveAt(_inlines.Count - 1);
        }
    }

    [GeneratedRegex(@"[ \t\r\n\f]+")]
    private static partial Regex CollapsibleWhitespace();
}

/// <summary>Formatting inherited from enclosing HTML elements.</summary>
internal readonly record struct InlineFormat(InlineStyle Style, string? Link, string? Color, string? BackgroundColor)
{
    public InlineFormat With(InlineStyle style) => this with { Style = Style | style };

    public TextInline ToInline(string text) => new(text, Style, Link, Color, BackgroundColor);
}
