using System.Runtime.CompilerServices;
using RSMatrix.Models;

namespace RSMatrix.IntegrationTests;

/// <summary>Reads <see cref="MatrixClient.Updates"/> in tests. Issues passed while waiting for a message are kept for <see cref="ReadIssues"/>.</summary>
internal static class UpdateReader
{
    private static readonly ConditionalWeakTable<MatrixClient, List<ClientIssue>> PassedIssues = new();

    public static Task<Message> ReceiveAsync(MatrixClient client, string eventId, CancellationToken cancellationToken)
        => ReceiveAsync(client, message => message.Id == eventId, cancellationToken);

    public static async Task<Message> ReceiveAsync(MatrixClient client, Func<Message, bool> match, CancellationToken cancellationToken)
        => (await ReceiveUpdateAsync<NewMessage>(client, update => match(update.Message), cancellationToken)).Message;

    public static async Task<T> ReceiveUpdateAsync<T>(MatrixClient client, Func<T, bool> match, CancellationToken cancellationToken)
    {
        await foreach (var update in client.Updates.ReadAllAsync(cancellationToken))
        {
            if (update.Value is T expected && match(expected))
                return expected;
            if (update.Value is ClientIssue issue)
                PassedIssues.GetOrCreateValue(client).Add(issue);
        }
        throw new InvalidOperationException($"Sync ended before receiving the expected {typeof(T).Name}.");
    }

    /// <summary>Returns the issues reported so far and not read before.</summary>
    public static List<ClientIssue> ReadIssues(MatrixClient client)
    {
        var passed = PassedIssues.GetOrCreateValue(client);
        List<ClientIssue> issues = [.. passed];
        passed.Clear();
        while (client.Updates.TryRead(out var update))
        {
            if (update.Value is ClientIssue issue)
                issues.Add(issue);
        }
        return issues;
    }
}
