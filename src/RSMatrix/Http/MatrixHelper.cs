using System.Text.Json.Nodes;
﻿using Microsoft.Extensions.Logging;
using RSMatrix.Models;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RSMatrix.Http;
/// <summary>
/// Low level helper methods for Matrix operations
/// </summary>
internal static class MatrixHelper
{
    /// <summary>
    /// Fetches the well-known URI from the server
    /// This is a low level method, it is implicitly called by LoginAsync
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

    public static async Task<AuthFlowsResponse> FetchSupportedAuthFlowsAsync(HttpClientParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return await HttpClientHelper.SendAsync<AuthFlowsResponse>(parameters, "/_matrix/client/v3/login").ConfigureAwait(false);
    }

    /// <param name="deviceId">Null lets the server create a new device ID.</param>
    /// <param name="deviceDisplayName">Only used when the login creates a new device.</param>
    public static async Task<LoginResponse> PasswordLoginAsync(HttpClientParameters parameters, string userId, string password,
        string? deviceId, string? deviceDisplayName)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrEmpty(userId, nameof(userId));
        ArgumentException.ThrowIfNullOrEmpty(password, nameof(password));
        var request = new LoginRequest
        {
            Identifier = new Identifier
            {
                Type = "m.id.user",
                User = userId
            },
            Password = password,
            DeviceId = deviceId,
            InitialDeviceDisplayName = deviceDisplayName,
            Type = "m.login.password"
        };
        var content = JsonContent.Create(request, options: new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        return await HttpClientHelper.SendAsync<LoginResponse>(parameters, "/_matrix/client/v3/login", HttpMethod.Post, content).ConfigureAwait(false);
    }

    /// <summary>Invalidates the access token and deletes its device.</summary>
    internal static Task LogoutAsync(HttpClientParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return HttpClientHelper.SendAsync(parameters, "/_matrix/client/v3/logout", HttpMethod.Post, JsonContent.Create(new { }));
    }

    internal static async Task<WhoAmIResponse> GetWhoAmIAsync(HttpClientParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return await HttpClientHelper.SendAsync<WhoAmIResponse>(parameters, "/_matrix/client/v3/account/whoami").ConfigureAwait(false);
    }

    public static async Task<CapabilitiesResponse> FetchCapabilitiesAsync(HttpClientParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return await HttpClientHelper.SendAsync<CapabilitiesResponse>(parameters, "/_matrix/client/v3/capabilities").ConfigureAwait(false);
    }

    public static async Task<FilterResponse> PostFilterAsync(HttpClientParameters parameters, MatrixId user, Filter filter)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(user);
        if (user.Kind != IdKind.User)
            throw new ArgumentException("User must be a user ID", nameof(user));
        ArgumentNullException.ThrowIfNull(filter);
        var content = JsonContent.Create(filter, options: new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        string path = $"/_matrix/client/v3/user/{Uri.EscapeDataString(user.Full)}/filter";
        return await HttpClientHelper.SendAsync<FilterResponse>(parameters, path, HttpMethod.Post, content).ConfigureAwait(false);
    }

    public static async Task<PresenceResponse> GetPresenceAsync(HttpClientParameters parameters, MatrixId user)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(user);
        string path = $"/_matrix/client/v3/presence/{Uri.EscapeDataString(user.Full)}/status";
        return await HttpClientHelper.SendAsync<PresenceResponse>(parameters, path).ConfigureAwait(false);
    }

    public static async Task PutPresenceAsync(HttpClientParameters parameters, MatrixId userId, PresenceRequest presence)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentNullException.ThrowIfNull(presence);

        var content = JsonContent.Create(
            presence, options: new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        string path = $"/_matrix/client/v3/presence/{Uri.EscapeDataString(userId.Full)}/status";
        await HttpClientHelper.SendAsync(parameters, path, HttpMethod.Put, content).ConfigureAwait(false);
    }

    public static async Task PostReceiptAsync(HttpClientParameters parameters, MatrixId room, string eventId, string? threadId)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(eventId);
        var receipt = new ReceiptRequest { ThreadId = threadId };
        var content = JsonContent.Create(
            receipt, options: new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        string path = $"/_matrix/client/v3/rooms/{Uri.EscapeDataString(room.Full)}/receipt/m.read/{Uri.EscapeDataString(eventId)}";
        await HttpClientHelper.SendAsync(parameters, path, HttpMethod.Post, content).ConfigureAwait(false);
    }

    public static async Task PostReadMarkersAsync(HttpClientParameters parameters, MatrixId room, string eventId, bool sendReceipt = false)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(eventId);
        var request = new ReadMarkerRequest { FullyRead = eventId };
        if(sendReceipt)
         request.Read = eventId;

        var content = JsonContent.Create(
            request, options: new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        string path = $"/_matrix/client/v3/rooms/{Uri.EscapeDataString(room.Full)}/read_markers";
        await HttpClientHelper.SendAsync(parameters, path, HttpMethod.Post, content).ConfigureAwait(false);
    }



    public static async Task<Filter> GetFilterAsync(HttpClientParameters parameters, MatrixId user, string filterId)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(user);
        if (user.Kind != IdKind.User)
            throw new ArgumentException("User must be a user ID", nameof(user));
        ArgumentException.ThrowIfNullOrEmpty(filterId, nameof(filterId));
        string path = $"/_matrix/client/v3/user/{Uri.EscapeDataString(user.Full)}/filter/{Uri.EscapeDataString(filterId)}";
        return await HttpClientHelper.SendAsync<Filter>(parameters, path).ConfigureAwait(false);
    }

    internal static async Task<SyncResponse> GetSyncAsync(HttpClientParameters httpClientParameters, SyncParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(httpClientParameters, nameof(httpClientParameters));
        ArgumentNullException.ThrowIfNull(parameters, nameof(parameters));

        var path = HttpParameterHelper.AppendParameters("/_matrix/client/v3/sync", parameters.GetAsParameters());

        return await HttpClientHelper.SendAsync<SyncResponse>(httpClientParameters, path, HttpMethod.Get,
            ignoreRateLimit: true).ConfigureAwait(false);
    }

    internal static async Task PutTypingNotificationAsync(HttpClientParameters httpClientParameters, MatrixId roomId, MatrixId userId, uint? timeoutMS = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientParameters, nameof(httpClientParameters));
        ArgumentNullException.ThrowIfNull(roomId, nameof(roomId));
        ArgumentNullException.ThrowIfNull(userId, nameof(userId)); // UserId is provided here, but it only makes sense to provide the current user

        var typingRequest = new TypingRequest { Typing = true, Timeout = timeoutMS ?? 2000 };
        var content = JsonContent.Create(
            typingRequest, options: new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });

        var path = $"/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomId.Full)}/typing/{Uri.EscapeDataString(userId.Full)}";
        await HttpClientHelper.SendAsync(httpClientParameters, path, HttpMethod.Put, content).ConfigureAwait(false);
    }

    internal static async Task<EventIdResponse> PutRoomEventAsync(HttpClientParameters httpClientParameters, MatrixId roomId, string eventType, JsonObject content)
    {
        ArgumentNullException.ThrowIfNull(httpClientParameters, nameof(httpClientParameters));
        ArgumentNullException.ThrowIfNull(roomId, nameof(roomId));
        ArgumentException.ThrowIfNullOrEmpty(eventType, nameof(eventType));
        ArgumentNullException.ThrowIfNull(content, nameof(content));

        var txnId = httpClientParameters.NextTransactionId();
        var path = $"/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomId.Full)}/send/{Uri.EscapeDataString(eventType)}/{txnId}";
        return await HttpClientHelper.SendAsync<EventIdResponse>(httpClientParameters, path, HttpMethod.Put, JsonContent.Create(content)).ConfigureAwait(false);
    }

    internal static async Task<EventIdResponse> PutRedactionAsync(HttpClientParameters httpClientParameters, MatrixId roomId, string eventId, string? reason)
    {
        ArgumentNullException.ThrowIfNull(httpClientParameters, nameof(httpClientParameters));
        ArgumentNullException.ThrowIfNull(roomId, nameof(roomId));
        ArgumentException.ThrowIfNullOrEmpty(eventId, nameof(eventId));

        var txnId = httpClientParameters.NextTransactionId();

        var request = new RedactionRequest { Reason = reason };
        var content = JsonContent.Create(
            request, options: new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });

        var path = $"/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomId.Full)}/redact/{Uri.EscapeDataString(eventId)}/{txnId}";
        return await HttpClientHelper.SendAsync<EventIdResponse>(httpClientParameters, path, HttpMethod.Put, content).ConfigureAwait(false);
    }

    internal static async Task<JoinRoomResponse> PostJoinRoomAsync(HttpClientParameters httpClientParameters, MatrixId roomId, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientParameters, nameof(httpClientParameters));
        ArgumentNullException.ThrowIfNull(roomId, nameof(roomId));

        var request = new JoinRoomRequest { Reason = reason };
        var content = JsonContent.Create(
            request, options: new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });

        var path = $"/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomId.Full)}/join";
        return await HttpClientHelper.SendAsync<JoinRoomResponse>(httpClientParameters, path, HttpMethod.Post, content).ConfigureAwait(false);
    }

    internal static async Task<RoomMessagesResponse> GetMessagesAsync(HttpClientParameters httpClientParameters, MatrixId roomId, MessagesParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(httpClientParameters, nameof(httpClientParameters));
        ArgumentNullException.ThrowIfNull(roomId, nameof(roomId));
        ArgumentNullException.ThrowIfNull(parameters, nameof(parameters));

        var path = HttpParameterHelper.AppendParameters(
            $"/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomId.Full)}/messages",
            parameters.GetAsParameters());

        return await HttpClientHelper.SendAsync<RoomMessagesResponse>(httpClientParameters, path).ConfigureAwait(false);
    }
}
