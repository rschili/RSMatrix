using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace MatrixTextClient
{
    public class UserId
    {
        public string Name { get; private set; }
        public string Server { get; private set; }

        public string FullId => $"@{Name}:{Server}";

        private UserId(string name, string server)
        {
            Name = name;
            Server = server;
        }

        public bool IsValid => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Server);

        public static bool TryParse(string input, out UserId? userId)
        {
            userId = null;
            if (string.IsNullOrWhiteSpace(input))
            {
                return false;
            }

            var parts = input.Split(':', 2);
            if (parts.Length != 2 || !parts[0].StartsWith("@"))
            {
                return false;
            }

            var name = parts[0].Substring(1);
            var server = parts[1];

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(server))
            {
                return false;
            }

            userId = new UserId(name, server);
            return true;
        }
    }

    public class ClientVersion : IComparable<ClientVersion>, IEquatable<ClientVersion>
    {
        public int X { get; private set; }
        public int Y { get; private set; }
        public int? Z { get; private set; }
        public string? Metadata { get; private set; }

        public string VersionString { get; }

        public ClientVersion(int x, int y, int? z, string? metadata)
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

        public static bool TryParse(string input, out ClientVersion? version)
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
            if(prefix == "r" && z == null)
                return false;
            else if(prefix == "v" && z != null)
                return false;

            version = new ClientVersion(x, y, z, metadata);
            return true;
        }

        public int CompareTo(ClientVersion? other)
        {
            return Comparer.Instance.Compare(this, other);
        }

        public override bool Equals(object? obj)
        {
            if (obj is ClientVersion other)
            {
                return Equals(other);
            }
            return false;
        }

        public bool Equals(ClientVersion? other)
        {
            if (other == null) return false;

            return X == other.X && Y == other.Y && (Z ?? 0) == (other.Z ?? 0) && Metadata == other.Metadata;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(X, Y, Z ?? 0, Metadata?.GetHashCode() ?? 0);
        }

        public sealed class Comparer : IComparer<ClientVersion?>
        {
            public static Comparer Instance { get; } = new();
            private Comparer() { }

            public int Compare(ClientVersion? x, ClientVersion? y)
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
