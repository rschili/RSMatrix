using MatrixTextClient.Requests;
using MatrixTextClient.Responses;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;

namespace MatrixTextClient
{
    /// <summary>
    /// Low level helper methods for Matrix operations
    /// </summary>
    public static class MatrixHelper
    {
        /// <summary>
        /// Fetches the well-known URI from the server
        /// This is a low level method, it is implicitly called by ConnectAsync
        /// </summary>
        public static async Task<WellKnownUriResponse> FetchWellKnownUriAsync(HttpClientParameters parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);

            return await HttpClientHelper.SendAsync<WellKnownUriResponse>(parameters, "/.well-known/matrix/client").ConfigureAwait(false);
        }

        public static async Task<SpecVersionsResponse> FetchSupportedSpecVersionsAsync(HttpClientParameters parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            return await HttpClientHelper.SendAsync<SpecVersionsResponse>(parameters, "/_matrix/client/versions").ConfigureAwait(false);
        }

        public static async Task<List<string>> FetchSupportedAuthFlowsAsync(HttpClientParameters parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            var response = await HttpClientHelper.SendAsync<AuthFlowsResponse>(parameters, "/_matrix/client/v3/login").ConfigureAwait(false);
            if (response != null)
                return response.Flows.Select(f => f.Type).ToList();

            return new List<string>();
        }

        public static async Task<LoginResponse> PasswordLoginAsync(HttpClientParameters parameters, string userId, string password, string deviceId)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            ArgumentException.ThrowIfNullOrEmpty(userId, nameof(userId));
            ArgumentException.ThrowIfNullOrEmpty(password, nameof(password));
            ArgumentException.ThrowIfNullOrEmpty(deviceId, nameof(deviceId));
            var request = new LoginRequest
            {
                Identifier = new Identifier
                {
                    Type = "m.id.user",
                    User = userId
                },
                Password = password,
                DeviceId = deviceId,
                InitialDeviceDisplayName = "MatrixTextClient Device",
                Type = "m.login.password"
            };
            var content = JsonContent.Create(request);
            return await HttpClientHelper.SendAsync<LoginResponse>(parameters, "/_matrix/client/v3/login", HttpMethod.Post, content).ConfigureAwait(false);
        }
    }
}
