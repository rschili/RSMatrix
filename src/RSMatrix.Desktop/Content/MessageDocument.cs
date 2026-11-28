namespace RSMatrix.Desktop.Content;

/// <summary>
/// Renderable message content. Parsers turn plain bodies and the HTML subset Matrix allows into this
/// model, so the views never see raw HTML.
/// </summary>
public sealed record MessageDocument(IReadOnlyList<MessageBlock> Blocks)
{
    public static MessageDocument Empty { get; } = new([]);
}

public abstract record MessageBlock;

public sealed record ParagraphBlock(IReadOnlyList<MessageInline> Inlines) : MessageBlock;

public sealed record HeadingBlock(int Level, IReadOnlyList<MessageInline> Inlines) : MessageBlock;

public sealed record CodeBlock(string Code, string? Language) : MessageBlock;

public sealed record QuoteBlock(IReadOnlyList<MessageBlock> Blocks) : MessageBlock;

public sealed record ListBlock(bool IsOrdered, int Start, IReadOnlyList<ListItem> Items) : MessageBlock;

public sealed record ListItem(IReadOnlyList<MessageBlock> Blocks);

public sealed record TableBlock(IReadOnlyList<TableRow> Rows) : MessageBlock;

public sealed record TableRow(bool IsHeader, IReadOnlyList<IReadOnlyList<MessageInline>> Cells);

public sealed record RuleBlock : MessageBlock;

public sealed record ImageBlock(MediaReference Image) : MessageBlock;

/// <summary>A file attached to a message, shown as a row to save it.</summary>
/// <param name="FileName">The name the sender gave. Strip directories before using it as a path.</param>
/// <param name="Size">The size in bytes the sender stated, if any.</param>
public sealed record FileBlock(string MxcUri, string FileName, long? Size) : MessageBlock;

public abstract record MessageInline;

/// <param name="Link">Target for links. Only absolute URLs with an allowed scheme get here.</param>
/// <param name="Color">#RRGGBB from data-mx-color.</param>
/// <param name="BackgroundColor">#RRGGBB from data-mx-bg-color.</param>
public sealed record TextInline(string Text, InlineStyle Style = InlineStyle.None, string? Link = null,
    string? Color = null, string? BackgroundColor = null) : MessageInline;

/// <summary>Small images inside text, mostly custom emoji.</summary>
public sealed record ImageInline(MediaReference Image) : MessageInline;

[Flags]
public enum InlineStyle
{
    None = 0,
    Bold = 1 << 0,
    Italic = 1 << 1,
    Underline = 1 << 2,
    Strikethrough = 1 << 3,
    Code = 1 << 4,
    Superscript = 1 << 5,
    Subscript = 1 << 6,
    Spoiler = 1 << 7,
    Mention = 1 << 8,
}

/// <summary>Points at media on the homeserver.</summary>
public sealed record MediaReference(string MxcUri, string? Description = null, int? Width = null, int? Height = null);
