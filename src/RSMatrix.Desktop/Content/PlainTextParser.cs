using System.Text;
using System.Text.RegularExpressions;

namespace RSMatrix.Desktop.Content;

/// <summary>Finds bare http(s) URLs in plain text.</summary>
internal static partial class Linkifier
{
    public static IEnumerable<(string Text, string? Link)> Split(string text)
    {
        var position = 0;
        foreach (Match match in UrlPattern().Matches(text))
        {
            var url = TrimTrailingPunctuation(match.Value);
            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
                continue;
            if (match.Index > position)
                yield return (text[position..match.Index], null);
            yield return (url, url);
            position = match.Index + url.Length;
        }
        if (position < text.Length)
            yield return (text[position..], null);
    }

    private static string TrimTrailingPunctuation(string url)
    {
        while (url.Length > 0)
        {
            var last = url[^1];
            if (last is '.' or ',' or ';' or ':' or '!' or '?' or '\'' or '"')
                url = url[..^1];
            // Keep closing parentheses that belong to the URL, like Wikipedia links.
            else if (last == ')' && url.Count(c => c == '(') < url.Count(c => c == ')'))
                url = url[..^1];
            else
                break;
        }
        return url;
    }

    [GeneratedRegex(@"\bhttps?://[^\s<>""]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();
}

public static class PlainTextParser
{
    /// <summary>Keeps line breaks as they are and turns bare URLs into links.</summary>
    public static MessageDocument Parse(string? body, InlineStyle style = InlineStyle.None)
    {
        if (string.IsNullOrEmpty(body))
            return MessageDocument.Empty;

        var builder = new BlockBuilder();
        var text = body.ReplaceLineEndings("\n").Trim('\n');
        builder.AddText(text, new InlineFormat(style, null, null, null), preserveWhitespace: true);
        return new MessageDocument(builder.Build());
    }

    /// <summary>One line of text for previews like the room list.</summary>
    public static string ToPreview(string? body, int maxLength = 140)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "";
        var builder = new StringBuilder(Math.Min(body.Length, maxLength));
        var lastWasSpace = false;
        foreach (var c in body)
        {
            var isSpace = char.IsWhiteSpace(c);
            if (isSpace && (lastWasSpace || builder.Length == 0))
                continue;
            builder.Append(isSpace ? ' ' : c);
            lastWasSpace = isSpace;
            if (builder.Length >= maxLength)
                break;
        }
        return builder.ToString().TrimEnd();
    }
}
