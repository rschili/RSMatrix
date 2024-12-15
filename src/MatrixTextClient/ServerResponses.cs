using System;
using System.Net;
using System.Text.Json.Serialization;

namespace MatrixTextClient
{


    public class MatrixErrorResponse
    {
        [JsonPropertyName("errcode")]
        public required string ErrorCode { get; set; }

        [JsonPropertyName("error")]
        public required string ErrorMessage { get; set; }
    }


    public class WellKnownUriResponse
    {
        [JsonPropertyName("m.homeserver")]
        public required HomeServer HomeServer { get; set; }

        [JsonPropertyName("m.identity_server")]
        public IdentityServer? IdentityServer { get; set; }
    }

    public class HomeServer
    {
        [JsonPropertyName("base_url")]
        public required string BaseUrl { get; set; }
    }
    public class IdentityServer
    {
        [JsonPropertyName("base_url")]
        public required string BaseUrl { get; set; }
    }

    public class ClientVersionsResponse
    {
        [JsonPropertyName("unstable_features")]
        public Dictionary<string, bool>? UnstableFeatures { get; set; }

        [JsonPropertyName("versions")]
        public required List<string> Versions { get; set; }
    }
}
