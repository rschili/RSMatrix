using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
}
