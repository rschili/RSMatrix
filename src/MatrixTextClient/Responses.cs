using System;
using System.Net;
using System.Text.Json.Serialization;

namespace MatrixTextClient.Responses
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

    public class SpecVersionsResponse
    {
        [JsonPropertyName("unstable_features")]
        public Dictionary<string, bool>? UnstableFeatures { get; set; }

        [JsonPropertyName("versions")]
        public required List<string> Versions { get; set; }
    }

    public class AuthFlowsResponse
    {
        [JsonPropertyName("flows")]
        public required List<AuthFlow> Flows { get; set; }
    }

    public class AuthFlow
    {
        [JsonPropertyName("type")]
        public required string Type { get; set; }
    }

    public class LoginResponse
    {
        [JsonPropertyName("user_id")]
        public required string UserId { get; set; }
        [JsonPropertyName("access_token")]
        public required string AccessToken { get; set; }
        [JsonPropertyName("device_id")]
        public required string DeviceId { get; set; }
    }
}
