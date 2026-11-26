using System.Text.Json;
using Microsoft.Extensions.Logging;
using RSMatrix.Models;

namespace RSMatrix.Tests;

public class MatrixDiagnosticsTests
{
    [Test]
    [Arguments(null, "Off")]
    [Arguments("", "Off")]
    [Arguments("unhandled", "Unhandled")]
    [Arguments(" Verbose ", "Verbose")]
    [Arguments("loud", "Off")]
    [Arguments("7", "Off")]
    public async Task Parse_ReadsModeCaseInsensitive(string? setting, string expected)
    {
        await Assert.That(MatrixDiagnostics.Parse(setting).Mode.ToString()).IsEqualTo(expected);
    }

    [Test]
    public async Task FindUnknownFields_WalksObjectsListsAndDictionaries()
    {
        using var json = JsonDocument.Parse("""
        {"next_batch":"n","new_section":{},"rooms":{"join":{"!room:example.org":{
          "unknown_room_field":1,
          "timeline":{"limited":false,"events":[
            {"type":"m.room.message","sender":"@a:example.org","event_id":"$1","origin_server_ts":1,"content":{"anything":1},
             "unsigned":{"age":1,"m.relations":{}}}]}}}}}
        """);

        var found = MatrixDiagnostics.FindUnknownFields(json.RootElement, typeof(SyncResponse))
            .Select(f => $"{f.Owner}.{f.Field}").ToList();

        await Assert.That(found).IsEquivalentTo(["SyncResponse.new_section", "JoinedRoomEvents.unknown_room_field", "UnsignedData.m.relations"]);
    }

    [Test]
    public async Task ReportUnhandled_WarnsOncePerCase_AndSkipsIgnoredOnes()
    {
        var logger = new RecordingLogger();
        var diagnostics = new MatrixDiagnostics(DiagnosticsMode.Unhandled);

        diagnostics.ReportUnhandled(logger, UnhandledKind.TimelineEvent, "m.reaction", JsonDocument.Parse("""{"type":"m.reaction"}""").RootElement);
        diagnostics.ReportUnhandled(logger, UnhandledKind.TimelineEvent, "m.reaction");
        diagnostics.ReportUnhandled(logger, UnhandledKind.StateEvent, "m.reaction");
        diagnostics.ReportUnhandled(logger, UnhandledKind.TimelineEvent, "m.call.invite");
        diagnostics.ReportUnhandled(logger, UnhandledKind.AccountData, "m.secret_storage.key.abc");

        await Assert.That(logger.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message))
            .IsEquivalentTo(["Unhandled TimelineEvent: m.reaction", "Unhandled StateEvent: m.reaction"]);
        await Assert.That(logger.Entries.Count(e => e.Level == LogLevel.Debug)).IsEqualTo(1);
    }

    [Test]
    public async Task Off_ReportsNothing()
    {
        var logger = new RecordingLogger();
        MatrixDiagnostics.Off.ReportUnhandled(logger, UnhandledKind.TimelineEvent, "m.reaction");
        MatrixDiagnostics.Off.ReportUnknownFields<SyncResponse>(logger, JsonDocument.Parse("""{"next_batch":"n","x":1}""").RootElement);
        await Assert.That(logger.Entries).IsEmpty();
    }

    [Test]
    public async Task RemoveSecrets_ReplacesSecretFieldsAtAnyDepth()
    {
        var text = MatrixDiagnostics.RemoveSecrets("""
            {"type":"m.login.password","password":"hunter2","auth":{"password":"hunter2"},"list":[{"access_token":"syt_x"}],"user":"@a:b"}
            """);

        await Assert.That(text.Contains("hunter2")).IsFalse();
        await Assert.That(text.Contains("syt_x")).IsFalse();
        await Assert.That(text.Contains("@a:b")).IsTrue();
        await Assert.That(MatrixDiagnostics.RemoveSecrets("<html>")).IsEqualTo("(6 characters, not JSON)");
    }

    [Test]
    public async Task Sync_ReportsUnhandledEventsAndFields()
    {
        using var context = new MatrixTestContext(diagnostics: new MatrixDiagnostics(DiagnosticsMode.Unhandled));
        await context.Process("""
        {"next_batch":"next","account_data":{"events":[{"type":"m.push_rules","content":{}}]},
         "rooms":{"join":{"!room:example.org":{"timeline":{"events":[
          {"type":"m.reaction","sender":"@alice:example.org","event_id":"$r","origin_server_ts":1700000000000,"content":{}},
          {"type":"m.room.message","sender":"@alice:example.org","event_id":"$i","origin_server_ts":1700000000000,
           "content":{"msgtype":"m.image","body":"cat.png","url":"mxc://example.org/cat"}},
          {"type":"m.room.message","sender":"@alice:example.org","event_id":"$e","origin_server_ts":1700000000000,
           "content":{"msgtype":"m.text","body":"* hi","m.new_content":{"msgtype":"m.text","body":"hi"},
                      "m.relates_to":{"rel_type":"m.replace","event_id":"$i"}}},
          {"type":"m.room.topic","state_key":"","sender":"@alice:example.org","event_id":"$t","origin_server_ts":1700000000000,
           "content":{"topic":"Cats"}}]}}},
          "leave":{"!old:example.org":{}}}}
        """);

        var warnings = context.Logs.Where(l => l.Level == LogLevel.Warning).Select(l => l.Message).ToList();
        await Assert.That(warnings).IsEquivalentTo([
            "Unhandled AccountData: m.push_rules",
            "Unhandled TimelineEvent: m.reaction",
            "Unhandled MsgType: m.image",
            "Unhandled Field: RoomMessageEvent.url",
            "Unhandled Field: RoomMessageEvent.m.new_content",
            "Unhandled Relation: m.replace",
            "Unhandled StateEvent: m.room.topic",
            "Unhandled SyncSection: rooms.leave",
        ]);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
