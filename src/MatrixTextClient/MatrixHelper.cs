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
using System.Web;

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

        public static async Task<CapabilitiesResponse> FetchCapabilitiesAsync(HttpClientParameters parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            return await HttpClientHelper.SendAsync<CapabilitiesResponse>(parameters, "/_matrix/client/v3/capabilities").ConfigureAwait(false);
        }

        public static async Task<FilterResponse> PostFilterAsync(HttpClientParameters parameters, UserId user, Filter filter)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            ArgumentNullException.ThrowIfNull(user);
            ArgumentNullException.ThrowIfNull(filter);
            var content = JsonContent.Create(filter);
            string path = $"/_matrix/client/v3/user/{HttpUtility.UrlEncode(user.FullId)}/filter";
            return await HttpClientHelper.SendAsync<FilterResponse>(parameters, path, HttpMethod.Post, content).ConfigureAwait(false);
        }

        public static async Task<Filter> GetFilterAsync(HttpClientParameters parameters, UserId user, string filterId)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            ArgumentNullException.ThrowIfNull(user);
            ArgumentException.ThrowIfNullOrEmpty(filterId, nameof(filterId));
            string path = $"/_matrix/client/v3/user/{HttpUtility.UrlEncode(user.FullId)}/filter/{HttpUtility.UrlEncode(filterId)}";
            return await HttpClientHelper.SendAsync<Filter>(parameters, path).ConfigureAwait(false);
        }

        internal static async Task<SyncResponse> GetSyncAsync(HttpClientParameters httpClientParameters, SyncParameters parameters)
        {
            ArgumentNullException.ThrowIfNull(httpClientParameters, nameof(httpClientParameters));
            ArgumentNullException.ThrowIfNull(parameters, nameof(parameters));

            var path = HttpParameterHelper.AppendParameters("/_matrix/client/v3/sync", parameters.GetAsParameters());

            return await HttpClientHelper.SendAsync<SyncResponse>(httpClientParameters, path, HttpMethod.Get).ConfigureAwait(false);
        }
    }
}
