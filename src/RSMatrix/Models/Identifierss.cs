using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace RSMatrix.Models;
public enum IdKind
{
    User,
    Room,
    RoomAlias
}

/// <summary>A Matrix identifier, compared by its complete, case-sensitive value including the sigil.</summary>
/// <remarks>
/// The existing init-only properties are retained for source compatibility. Equality and hashing
/// use <see cref="Full"/> without normalization or a cached hash code.
/// </remarks>
public sealed class MatrixId : IEquatable<MatrixId>
{
    public string Full { get; init; }

    public Range LocalpartRange { get; init; }
    public Range DomainRange { get; init; }

    /// <summary>Gets the localpart without the sigil, for both domain-qualified and domainless identifiers.</summary>
    public ReadOnlySpan<char> Localpart => Full.AsSpan(LocalpartRange);

    /// <summary>Gets the server name, or an empty span for a domainless room ID.</summary>
    public ReadOnlySpan<char> Domain => Full.AsSpan(DomainRange);

    public IdKind Kind { get; init; }

    private static readonly SearchValues<char> s_allowedHostCharacters = SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.-");
    private static readonly SearchValues<char> s_allowedIpv6Characters = SearchValues.Create("abcdefABCDEF0123456789:.");

    private MatrixId(string full, Range localpartRange, Range domainRange, IdKind kind)
    {
        Full = full;
        LocalpartRange = localpartRange;
        DomainRange = domainRange;
        Kind = kind;
    }

    /// <summary>Parses incoming identifiers, including historical user localparts and domainless room IDs.</summary>
    public static bool TryParse(string? input, out MatrixId? result)
    {
        result = null;
        if (string.IsNullOrEmpty(input) || input.Length < 2 || input.Length > 255)
            return false;

        var span = input.AsSpan();
        IdKind? idKind = span[0] switch
        {
            '@' => IdKind.User,
            '!' => IdKind.Room,
            '#' => IdKind.RoomAlias,
            _ => null
        };

        if (idKind == null)
            return false;

        var indexOfSeparator = span.IndexOf(':');
        if (indexOfSeparator == -1 && idKind != IdKind.Room)
            return false;

        // Room IDs are opaque: do not impose a room-version-specific hash length or alphabet.
        var localpartRange = new Range(1, indexOfSeparator == -1 ? span.Length : indexOfSeparator);
        var domainRange = Range.StartAt(indexOfSeparator == -1 ? span.Length : indexOfSeparator + 1);

        // Historical incoming users, aliases and legacy rooms permit empty localparts.
        // Decode explicitly so UTF-8 replacement fallback cannot accept unpaired surrogates.
        var localpart = span[localpartRange];
        while (!localpart.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(localpart, out var rune, out var consumed) != OperationStatus.Done
                || rune.Value is 0 or ':')
                return false;

            localpart = localpart[consumed..];
        }

        if (indexOfSeparator != -1 && !IsValidServerName(span[domainRange]))
            return false;

        if (Encoding.UTF8.GetByteCount(span) > 255)
            return false;

        result = new MatrixId(input, localpartRange, domainRange, idKind.Value);
        return true;
    }

    private static bool IsValidServerName(ReadOnlySpan<char> server)
    {
        if (server.IsEmpty)
            return false;

        ReadOnlySpan<char> suffix;
        if (server[0] == '[')
        {
            var closingBracket = server.IndexOf(']');
            if (closingBracket == -1)
                return false;

            var address = server[1..closingBracket];
            if (address.Length is < 2 or > 45
                || address.ContainsAnyExcept(s_allowedIpv6Characters)
                || !IPAddress.TryParse(address, out var parsedAddress)
                || parsedAddress.AddressFamily != AddressFamily.InterNetworkV6)
                return false;

            suffix = server[(closingBracket + 1)..];
        }
        else
        {
            var portSeparator = server.IndexOf(':');
            var host = portSeparator == -1 ? server : server[..portSeparator];
            if (host.IsEmpty || host.Length > 255 || host.ContainsAnyExcept(s_allowedHostCharacters))
                return false;

            // A dotted-quad IPv4 literal must not use out-of-range octets.
            if (host.Count('.') == 3 && !host.ContainsAnyExcept("0123456789.".AsSpan()) && !IsValidIpv4(host))
                return false;

            suffix = portSeparator == -1 ? ReadOnlySpan<char>.Empty : server[portSeparator..];
        }

        // The Matrix grammar specifies 1-5 decimal digits, not a TCP port range.
        return suffix.IsEmpty || (suffix[0] == ':' && suffix.Length is >= 2 and <= 6
            && !suffix[1..].ContainsAnyExceptInRange('0', '9'));
    }

    private static bool IsValidIpv4(ReadOnlySpan<char> host)
    {
        var digits = 0;
        var octet = 0;
        foreach (var character in host)
        {
            if (character == '.')
            {
                if (digits == 0)
                    return false;
                digits = 0;
                octet = 0;
            }
            else
            {
                octet = octet * 10 + character - '0';
                if (++digits > 3 || octet > 255)
                    return false;
            }
        }
        return digits != 0;
    }

    public bool Equals(MatrixId? other) => other is not null && StringComparer.Ordinal.Equals(Full, other.Full);

    public override bool Equals(object? obj) => obj is MatrixId other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Full);

    public override string ToString() => Full;
}

public static class UserId
{
    public static bool TryParse(string? input, out MatrixId? userId)
    {
        if (MatrixId.TryParse(input, out var id) && id != null && id.Kind == IdKind.User)
        {
            userId = id;
            return true;
        }

        userId = null;
        return false;
    }
}

public static class RoomId
{
    public static bool TryParse(string? input, out MatrixId? roomId)
    {
        if (MatrixId.TryParse(input, out var id) && id != null && id.Kind == IdKind.Room)
        {
            roomId = id;
            return true;
        }

        roomId = null;
        return false;
    }
}

public static class RoomAlias
{
    public static bool TryParse(string? input, out MatrixId? roomAlias)
    {
        if (MatrixId.TryParse(input, out var id) && id != null && id.Kind == IdKind.RoomAlias)
        {
            roomAlias = id;
            return true;
        }

        roomAlias = null;
        return false;
    }
}


public class SpecVersion : IComparable<SpecVersion>, IEquatable<SpecVersion>
{
    public int X { get; private set; }
    public int Y { get; private set; }
    public int? Z { get; private set; }
    public string? Metadata { get; private set; }

    public string VersionString { get; }

    public SpecVersion(int x, int y, int? z, string? metadata)
    {
        X = x;
        Y = y;
        Z = z;
        Metadata = metadata;
        VersionString = GenerateVersionString();
    }

    private string GenerateVersionString()
    {
        return Z.HasValue
            ? $"r{X}.{Y}.{Z}" + (Metadata != null ? $"-{Metadata}" : string.Empty)
            : $"v{X}.{Y}" + (Metadata != null ? $"-{Metadata}" : string.Empty);
    }

    public static bool TryParse(string input, out SpecVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var regex = new Regex(@"^(v|r)(\d+)\.(\d+)(?:\.(\d+))?(?:-(\w+))?$");
        var match = regex.Match(input);
        if (!match.Success)
        {
            return false;
        }
        var prefix = match.Groups[1].Value;
        var x = int.Parse(match.Groups[2].Value);
        var y = int.Parse(match.Groups[3].Value);
        var z = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : (int?)null;
        var metadata = match.Groups[5].Success ? match.Groups[5].Value : null;
        if (prefix == "r" && z == null)
            return false;
        else if (prefix == "v" && z != null)
            return false;

        version = new SpecVersion(x, y, z, metadata);
        return true;
    }

    public int CompareTo(SpecVersion? other)
    {
        return Comparer.Instance.Compare(this, other);
    }

    public override bool Equals(object? obj)
    {
        if (obj is SpecVersion other)
        {
            return Equals(other);
        }
        return false;
    }

    public bool Equals(SpecVersion? other)
    {
        if (other == null) return false;

        return X == other.X && Y == other.Y && (Z ?? 0) == (other.Z ?? 0) && Metadata == other.Metadata;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(X, Y, Z ?? 0, Metadata?.GetHashCode() ?? 0);
    }

    public override string ToString()
    {
        return VersionString;
    }

    public sealed class Comparer : IComparer<SpecVersion?>
    {
        public static Comparer Instance { get; } = new();
        private Comparer() { }

        public int Compare(SpecVersion? x, SpecVersion? y)
        {
            if (x == null) return y == null ? 0 : -1;
            if (y == null) return 1;

            var xComparison = x.X.CompareTo(y.X);
            if (xComparison != 0) return xComparison;

            var yComparison = x.Y.CompareTo(y.Y);
            if (yComparison != 0) return yComparison;

            var zComparison = (x.Z ?? 0).CompareTo(y.Z ?? 0);
            if (zComparison != 0) return zComparison;

            if (x.Metadata == null && y.Metadata == null) return 0;
            if (x.Metadata == null) return 1;
            if (y.Metadata == null) return -1;

            return string.Compare(x.Metadata, y.Metadata, StringComparison.Ordinal);
        }
    }
}

