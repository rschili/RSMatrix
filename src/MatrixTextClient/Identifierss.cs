using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace MatrixTextClient
{
    public abstract class BaseId
    {
        public string Localpart { get; private set; }
        public string Domain { get; private set; }

        public abstract char Sigil { get; }

        public string FullId => $"{Sigil}{Localpart}:{Domain}";

        protected BaseId(string localpart, string domain)
        {
            Localpart = localpart;
            Domain = domain;
        }

        public bool IsValid => !string.IsNullOrWhiteSpace(Localpart) && !string.IsNullOrWhiteSpace(Domain);

        protected static bool TryParse(string? input, char sigil, out string localPart, out string domain)
        {
            localPart = string.Empty;
            domain = string.Empty;
            if (string.IsNullOrWhiteSpace(input))
            {
                return false;
            }

            var parts = input.Split(':', 2);
            if (parts.Length != 2 || !parts[0].StartsWith(sigil))
            {
                return false;
            }

            localPart = parts[0].Substring(1);
            domain = parts[1];

            if (string.IsNullOrWhiteSpace(localPart) || string.IsNullOrWhiteSpace(domain))
            {
                return false;
            }

            return true;
        }

        public override string ToString() => FullId;
    }

    public class UserId : BaseId
    {
        private const char SIGIL = '@';
        public override char Sigil => SIGIL;

        private UserId(string name, string server) : base(name, server) { }

        public static bool TryParse(string? input, out UserId? userId)
        {
            if (TryParse(input, SIGIL, out var name, out var server))
            {
                userId = new UserId(name, server);
                return true;
            }

            userId = null;
            return false;
        }
    }

    public class RoomId : BaseId
    {
        private const char SIGIL = '!';
        public override char Sigil => SIGIL;

        private RoomId(string name, string server) : base(name, server) { }

        public static bool TryParse(string? input, out RoomId? roomId)
        {
            if (TryParse(input, SIGIL, out var name, out var server))
            {
                roomId = new RoomId(name, server);
                return true;
            }

            roomId = null;
            return false;
        }
    }

    public class EventId : BaseId
    {
        private const char SIGIL = '$';
        public override char Sigil => SIGIL;

        private EventId(string name, string server) : base(name, server) { }

        public static bool TryParse(string? input, out EventId? eventId)
        {
            if (TryParse(input, SIGIL, out var name, out var server))
            {
                eventId = new EventId(name, server);
                return true;
            }

            eventId = null;
            return false;
        }
    }

    public class RoomAlias : BaseId
    {
        private const char SIGIL = '#';
        public override char Sigil => SIGIL;

        private RoomAlias(string name, string server) : base(name, server) { }

        public static bool TryParse(string? input, out RoomAlias? roomAlias)
        {
            if (TryParse(input, SIGIL, out var name, out var server))
            {
                roomAlias = new RoomAlias(name, server);
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

}
