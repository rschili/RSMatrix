# Changelog

All notable changes to RSMatrix will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.3.0] - 2026-03-16

### Added
- **m.notice support**: `Room.SendNoticeAsync` / `SendHtmlNoticeAsync` and corresponding
  `ReceivedTextMessage.SendNoticeResponseAsync` / `SendHtmlNoticeResponseAsync` for bot-appropriate
  messages that won't trigger notification loops.
- `ReceivedTextMessage.MsgType` property to distinguish `m.text` from `m.notice`.
- Incoming `m.notice` messages are now delivered through the `Messages` channel.
- **Message editing**: `Room.EditMessageAsync` / `EditHtmlMessageAsync` and
  `ReceivedTextMessage.EditAsync` / `EditHtmlAsync` using `m.replace`.
- **Redaction**: `Room.RedactEventAsync` and `ReceivedTextMessage.RedactAsync`.
- **Auto-join on invite**: Set `MatrixTextClient.AutoJoinOnInvite = true` to automatically
  accept room invitations. Invite `is_direct` flag is detected and exposed via `Room.IsDirect`.
- **Message history**: `Room.FetchMessagesAsync` fetches previous messages with pagination
  support via `MessageHistoryResult.NextToken`.

### Changed
- `ReceivedTextMessage.SendResponseAsync` and `SendHtmlResponseAsync` now return `Task<string>`
  (the event ID) instead of `Task`.

## [1.2.0] - 2026-03-16

### Added
- **Reaction support**: Send `m.reaction` events (emoji annotations) per Matrix spec v1.7+.
  - `Room.SendReactionAsync(eventId, key)` to react to any event in a room.
  - `ReceivedTextMessage.SendReactionAsync(key)` as a convenience method.
  - New models: `ReactionRelatesTo`, `ReactionRequest`.
  - New HTTP helper: `MatrixHelper.PutReactionAsync`.

### Changed
- Reduced log noise from known but unhandled event types. The following state events
  are now silently ignored instead of producing warnings:
  - `m.room.create`, `m.room.pinned_events`, `m.room.tombstone`, `m.room.retention`,
    `m.room.related_groups`, `m.room.history_visibility`, `m.room.guest_access`.
- Vendor-specific state events (`io.element.*`, `org.matrix.*`, `net.nordeck.*`,
  `im.vector.*`) are now logged at Debug level instead of Warning.
- Timeline events `m.room.canonical_alias` and `m.room.name` are now routed through
  the state event handler, since they are state events appearing in the timeline.
- Known but unprocessed timeline events (`m.room.power_levels`, `m.room.topic`,
  `m.room.tombstone`, `m.room.pinned_events`, `m.room.create`, `m.reaction`) are now
  logged at Debug instead of Warning.

## [1.1.1] - 2025-01-01

Initial tracked version. Basic Matrix client with:
- Password-based authentication
- Sync loop with long polling
- Text message sending and receiving (plain text and HTML)
- Room member tracking with lazy loading
- Presence management
- Read receipts and read markers
- Typing notifications
- Server-side filtering
- Basic E2E encryption detection (no decryption)
- Thread support (reading thread IDs)
- Mention support
