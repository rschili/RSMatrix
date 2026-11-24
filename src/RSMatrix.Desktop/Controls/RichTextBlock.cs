using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using RSMatrix.Desktop.Content;

namespace RSMatrix.Desktop.Controls;

/// <summary>
/// Selectable text built from message inlines. Links open on click, spoilers reveal on click.
/// </summary>
public sealed class RichTextBlock : SelectableTextBlock
{
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private IReadOnlyList<MessageInline> _content = [];
    private readonly List<(int Start, int End, TextInline Inline)> _clickableRanges = [];
    private readonly HashSet<TextInline> _revealedSpoilers = [];

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    public RichTextBlock()
    {
        TextWrapping = TextWrapping.Wrap;
    }

    public void SetContent(IReadOnlyList<MessageInline> content)
    {
        _content = content;
        _revealedSpoilers.Clear();
        Rebuild();
    }

    private void Rebuild()
    {
        _clickableRanges.Clear();
        var inlines = new InlineCollection();
        var position = 0;
        foreach (var item in _content)
        {
            switch (item)
            {
                case TextInline text:
                    inlines.Add(CreateRun(text));
                    if (text.Link != null || IsHiddenSpoiler(text))
                        _clickableRanges.Add((position, position + text.Text.Length, text));
                    position += text.Text.Length;
                    break;
                case ImageInline image:
                    inlines.Add(new InlineUIContainer(new MediaImageView
                    {
                        Media = image.Image,
                        MaxImageHeight = 22,
                        Margin = new Thickness(1, 0),
                    })
                    { BaselineAlignment = BaselineAlignment.TextBottom });
                    position += 1; // An embedded control takes one text position.
                    break;
            }
        }
        Inlines = inlines;
    }

    private bool IsHiddenSpoiler(TextInline text)
        => text.Style.HasFlag(InlineStyle.Spoiler) && !_revealedSpoilers.Contains(text);

    private Run CreateRun(TextInline text)
    {
        var run = new Run(text.Text);
        var style = text.Style;
        if (style.HasFlag(InlineStyle.Bold) || style.HasFlag(InlineStyle.Mention))
            run.FontWeight = FontWeight.SemiBold;
        if (style.HasFlag(InlineStyle.Italic))
            run.FontStyle = FontStyle.Italic;

        var decorations = new TextDecorationCollection();
        if (style.HasFlag(InlineStyle.Underline))
            decorations.AddRange(Avalonia.Media.TextDecorations.Underline);
        if (style.HasFlag(InlineStyle.Strikethrough))
            decorations.AddRange(Avalonia.Media.TextDecorations.Strikethrough);
        if (decorations.Count > 0)
            run.TextDecorations = decorations;

        if (style.HasFlag(InlineStyle.Code))
        {
            run.Bind(Run.FontFamilyProperty, this.GetResourceObservable("MonospaceFont"));
            run.Bind(Run.BackgroundProperty, this.GetResourceObservable("CodeBackgroundBrush"));
            run.FontSize = FontSize * 0.92;
        }
        if (style.HasFlag(InlineStyle.Superscript) || style.HasFlag(InlineStyle.Subscript))
        {
            run.BaselineAlignment = style.HasFlag(InlineStyle.Superscript) ? BaselineAlignment.Superscript : BaselineAlignment.Subscript;
            run.FontSize = FontSize * 0.75;
        }

        if (text.Color != null)
            run.Foreground = new SolidColorBrush(Color.Parse(text.Color));
        if (text.BackgroundColor != null)
            run.Background = new SolidColorBrush(Color.Parse(text.BackgroundColor));

        if (style.HasFlag(InlineStyle.Mention))
        {
            run.Bind(Run.ForegroundProperty, this.GetResourceObservable("MentionForegroundBrush"));
            run.Bind(Run.BackgroundProperty, this.GetResourceObservable("MentionBackgroundBrush"));
        }
        else if (text.Link != null)
        {
            run.Bind(Run.ForegroundProperty, this.GetResourceObservable("LinkBrush"));
        }

        if (IsHiddenSpoiler(text))
        {
            run.Bind(Run.ForegroundProperty, this.GetResourceObservable("SpoilerBrush"));
            run.Bind(Run.BackgroundProperty, this.GetResourceObservable("SpoilerBrush"));
        }
        return run;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Cursor = FindClickable(e.GetPosition(this)) != null ? HandCursor : null;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        // A drag selects text and should not open the link it ends on.
        if (e.InitialPressMouseButton != MouseButton.Left || SelectionStart != SelectionEnd)
            return;
        if (FindClickable(e.GetPosition(this)) is not { } inline)
            return;

        if (IsHiddenSpoiler(inline))
        {
            _revealedSpoilers.Add(inline);
            Rebuild();
        }
        else if (inline.Link != null && Uri.TryCreate(inline.Link, UriKind.Absolute, out var uri))
        {
            _ = TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(uri);
        }
    }

    private TextInline? FindClickable(Point position)
    {
        if (_clickableRanges.Count == 0)
            return null;
        var hit = TextLayout.HitTestPoint(position - new Point(Padding.Left, Padding.Top));
        if (!hit.IsInside)
            return null;
        var index = hit.CharacterHit.FirstCharacterIndex;
        foreach (var (start, end, inline) in _clickableRanges)
        {
            if (index >= start && index < end)
                return inline;
        }
        return null;
    }
}
