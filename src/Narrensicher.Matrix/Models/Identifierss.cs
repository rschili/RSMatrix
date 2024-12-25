using System.Buffers;
using System.Text.RegularExpressions;

namespace Narrensicher.Matrix.Models;
public enum IdKind
{
    User,
    Room,
    Event,
    RoomAlias
}

public sealed class MatrixId
{
    public string Full { get; init; }

    public Range LocalpartRange { get; init; }
    public Range DomainRange { get; init; }

    public ReadOnlySpan<char> Localpart => Full.AsSpan(LocalpartRange);
    public ReadOnlySpan<char> Domain => Full.AsSpan(DomainRange);

    public IdKind Kind { get; init; }

    private static readonly SearchValues<char> s_allowedLocalpartCharacters = SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.-_=+/");
    private static readonly SearchValues<char> s_allowedDomainCharacters = SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.-_=+/:");

    private MatrixId(string full, Range localpartRange, Range domainRange, IdKind kind)
    {
        Full = full;
        LocalpartRange = localpartRange;
        DomainRange = domainRange;
        Kind = kind;
    }

    public static bool TryParse(string? input, out MatrixId? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(input) || !input.Contains(':') || input.Length < 4) // shortest possible id is @a:b
            return false;


        var span = input.AsSpan();
        IdKind? idKind = span[0] switch
        {
            '@' => IdKind.User,
            '!' => IdKind.Room,
            '$' => IdKind.Event,
            '#' => IdKind.RoomAlias,
            _ => null
        };

        if (idKind == null)
            return false;

        var indexOfSeparator = span.IndexOf(':');
        if (indexOfSeparator == -1)
            return false;
        if (indexOfSeparator <= 2) // shortest possible id is @a:b
            return false;
        if (indexOfSeparator >= span.Length - 1) // cannot be the last char
            return false;

        var localpartRange = new Range(1, indexOfSeparator);
        var domainRange = Range.StartAt(indexOfSeparator + 1);

        var localpart = span[localpartRange];
        if (MemoryExtensions.ContainsAnyExcept(localpart, s_allowedLocalpartCharacters))
            return false;

        var domain = span[domainRange];
        if (MemoryExtensions.ContainsAnyExcept(domain, s_allowedDomainCharacters))
            return false;

        result = new MatrixId(input, localpartRange, domainRange, idKind.Value);
        return true;
    }

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

public static class EventId
{
    public static bool TryParse(string? input, out MatrixId? eventId)
    {
        if (MatrixId.TryParse(input, out var id) && id != null && id.Kind == IdKind.Event)
        {
            eventId = id;
            return true;
        }

        eventId = null;
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

