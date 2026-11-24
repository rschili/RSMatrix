using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using RSMatrix.Desktop.Content;

namespace RSMatrix.Desktop.Controls;

/// <summary>Builds native controls for a <see cref="MessageDocument"/>.</summary>
public sealed class MessageDocumentView : StackPanel
{
    public static readonly StyledProperty<MessageDocument?> DocumentProperty =
        AvaloniaProperty.Register<MessageDocumentView, MessageDocument?>(nameof(Document));

    private const double BlockSpacing = 6;
    private const double MaxImageWidth = 400;
    private const double MaxImageHeight = 320;

    public MessageDocumentView()
    {
        Spacing = BlockSpacing;
    }

    public MessageDocument? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DocumentProperty)
            Rebuild(Document?.Blocks ?? []);
    }

    private void Rebuild(IReadOnlyList<MessageBlock> blocks)
    {
        Children.Clear();
        foreach (var block in blocks)
            Children.Add(CreateBlock(block));
    }

    private static MessageDocumentView CreateNested(IReadOnlyList<MessageBlock> blocks)
    {
        var view = new MessageDocumentView();
        view.Rebuild(blocks);
        return view;
    }

    private Control CreateBlock(MessageBlock block) => block switch
    {
        ParagraphBlock paragraph => CreateText(paragraph.Inlines),
        HeadingBlock heading => CreateHeading(heading),
        CodeBlock code => CreateCode(code),
        QuoteBlock quote => CreateQuote(quote),
        ListBlock list => CreateList(list),
        TableBlock table => CreateTable(table),
        RuleBlock => CreateRule(),
        ImageBlock image => CreateImage(image),
        _ => new Control(),
    };

    private static RichTextBlock CreateText(IReadOnlyList<MessageInline> inlines)
    {
        var text = new RichTextBlock();
        text.SetContent(inlines);
        return text;
    }

    private RichTextBlock CreateHeading(HeadingBlock heading)
    {
        var text = CreateText(heading.Inlines);
        text.FontWeight = FontWeight.SemiBold;
        BindScaledFontSize(text, heading.Level switch { 1 => 20, 2 => 18, 3 => 16, _ => 15 });
        text.Margin = new Thickness(0, 2, 0, 0);
        return text;
    }

    /// <summary>Scales a size meant for the default text size of 14 along with the chosen text size.</summary>
    private void BindScaledFontSize(TextBlock text, double sizeAtDefault)
        => text.Bind(TextBlock.FontSizeProperty, this.GetResourceObservable("ControlContentThemeFontSize",
            size => size is double baseSize ? sizeAtDefault * baseSize / 14 : sizeAtDefault));

    private Border CreateCode(CodeBlock code)
    {
        var text = new SelectableTextBlock { Text = code.Code, TextWrapping = TextWrapping.NoWrap };
        BindScaledFontSize(text, 13);
        text.Bind(TextBlock.FontFamilyProperty, this.GetResourceObservable("MonospaceFont"));
        var border = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = text,
            },
        };
        border.Bind(Border.BackgroundProperty, this.GetResourceObservable("CodeBackgroundBrush"));
        border.Bind(Border.BorderBrushProperty, this.GetResourceObservable("CodeBorderBrush"));
        return border;
    }

    private Border CreateQuote(QuoteBlock quote)
    {
        var content = CreateNested(quote.Blocks);
        content.Bind(TextElement.ForegroundProperty, this.GetResourceObservable("TextSecondaryBrush"));
        var border = new Border
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 1, 0, 1),
            Child = content,
        };
        border.Bind(Border.BorderBrushProperty, this.GetResourceObservable("QuoteBarBrush"));
        return border;
    }

    private Grid CreateList(ListBlock list)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowSpacing = 2 };
        for (var i = 0; i < list.Items.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var marker = new TextBlock
            {
                Text = list.IsOrdered ? $"{list.Start + i}." : "•",
                Margin = new Thickness(2, 0, 8, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            marker.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("TextSecondaryBrush"));
            var content = CreateNested(list.Items[i].Blocks);
            Grid.SetRow(marker, i);
            Grid.SetRow(content, i);
            Grid.SetColumn(content, 1);
            grid.Children.Add(marker);
            grid.Children.Add(content);
        }
        return grid;
    }

    private Border CreateTable(TableBlock table)
    {
        var columnCount = table.Rows.Max(r => r.Cells.Count);
        var grid = new Grid();
        for (var c = 0; c < columnCount; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (var r = 0; r < table.Rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var row = table.Rows[r];
            for (var c = 0; c < row.Cells.Count; c++)
            {
                var text = CreateText(row.Cells[c]);
                text.TextWrapping = TextWrapping.NoWrap;
                if (row.IsHeader)
                    text.FontWeight = FontWeight.SemiBold;
                var cell = new Border
                {
                    Padding = new Thickness(10, 5),
                    BorderThickness = new Thickness(c == 0 ? 0 : 1, r == 0 ? 0 : 1, 0, 0),
                    Child = text,
                };
                cell.Bind(Border.BorderBrushProperty, this.GetResourceObservable("CodeBorderBrush"));
                if (row.IsHeader)
                    cell.Bind(Border.BackgroundProperty, this.GetResourceObservable("CodeBackgroundBrush"));
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }
        var border = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = grid,
        };
        border.Bind(Border.BorderBrushProperty, this.GetResourceObservable("CodeBorderBrush"));
        return border;
    }

    private Border CreateRule()
    {
        var rule = new Border { Height = 1, Margin = new Thickness(0, 4) };
        rule.Bind(Border.BackgroundProperty, this.GetResourceObservable("DividerBrush"));
        return rule;
    }

    private static MediaImageView CreateImage(ImageBlock image) => new()
    {
        Media = image.Image,
        MaxImageWidth = MaxImageWidth,
        MaxImageHeight = MaxImageHeight,
        CornerRadius = new CornerRadius(8),
        Margin = new Thickness(0, 2),
    };
}
