# Changelog

All notable changes to RSMatrix will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- `Room.LatestMessage`: the newest message the client holds for a room, for room lists.
- `MatrixClient.LoginAsync`, `GetStoredSession` and `ResumeAsync`. With a store directory, the client keeps its access token, sync position and encryption keys there, one session per user, and a restarted client resumes, including messages sent while it was offline. If the server rejects the stored sync position, the client syncs from scratch. With a store, the server can create the device ID. A new login replaces the stored session and logs out its device.
- Experimental end-to-end encryption, with Olm and Megolm implemented in C#. It is on when the client has a store directory. Messages in encrypted rooms are then decrypted, and messages, replies, edits and reactions are encrypted automatically. Not supported: interactive device verification, key backup, key requests and encrypted attachments.
- Cross-signing: a recovery key signs a new device, `MatrixClient.SetUpCrossSigningAsync` creates the account's identity, `MatrixClient.EncryptionState` shows where the client stands.
- `ReceivedTextMessage.IsEncrypted`.
- `MatrixClient.Issues`: a channel that reports problems the client keeps running through (`DecryptionFailed`, `RoomKeyNotShared`, `AutoJoinFailed`, `DeviceNotCrossSigned`, `IdentityChanged`). It keeps the latest 100 issues.
- `MatrixClient.LogoutAsync`: logs out the device, deletes the stored session and keys, and disposes the client.
- `ConnectionChanged` updates and `MatrixClient.IsOnline`, for when the sync fails and when it works again.
- `OptionalUpdates` on `LoginAsync` and `ResumeAsync`: bots can leave receipts, typing and presence out of the sync.
- `Room.TypingUsers`, reported as `RoomChange.Typing`.
- Presence: `PresenceChanged` updates, `User.StatusMessage`, `User.LastActiveAt`, and `MatrixClient.SetPresenceAsync` and `SetStatusMessageAsync`. The chosen presence goes with every sync, so it stays until changed, and a resumed session starts with it.
- Read state: `Room.NotificationCount`, `Room.MentionCount` and `Room.HasUnread`, reported as `RoomChange.Unread`. `Room.MarkAsReadAsync()` marks the whole room as read.
- After a soft logout, the client logs in again with the same device and keeps its sync position, while running and when resuming. Without a password it reports `PasswordRequiredException`.
- After a hard logout, the store discards the session and the client reports `SessionEndedException`.
- The environment variable `RSMATRIX_DIAGNOSTICS` (`Unhandled`, `Verbose`) logs what the client receives but doesn't handle, and optionally every request and response body.

### Changed
- **Breaking:** `ConnectAsync` is replaced by `LoginAsync` and `ResumeAsync`. The client owns its `HttpClient` instead of taking an `IHttpClientFactory`, and is `IAsyncDisposable`: disposing stops the sync.
- **Breaking:** the low-level HTTP and protocol types are internal.
- **Breaking:** `IsSyncing` is replaced by `IsOnline`.
- **Breaking:** `MatrixClient.CurrentUser` is a `User`, holding our own presence as others see it; the ID is `CurrentUser.UserId`.
- **Breaking:** `Room.SendTypingNotificationAsync` is replaced by `Room.SetTypingAsync(bool)`, which can also stop typing.
- Responses are no longer limited to 2 MB, so a large first sync works.
- Requests no longer wait for a local rate limiter; the client relies on the server's rate limits.
- `Message.MarkAsReadAsync()` on our own message puts the receipt on the newest event from someone else before it, and never moves the receipt backwards.
- **Breaking:** `LoginAsync` and `ResumeAsync` return after the first sync, with `MatrixClient.Rooms` filled. After a login, messages from the first sync aren't reported as updates; they are in the rooms' history.

## [1.4.0] - 2026-09-30

### Added
- `Room.StateEvents`: immutable snapshots of observed room state, keyed by event
  type and state key, including space links and arbitrary custom integrations.

### Fixed
- Reject plaintext messages and edits in rooms with observed encryption, including invites.
- Support domain-less room IDs, Unicode/IPv6 identifiers and case-sensitive ID caches.
- URI-escape identifier path segments correctly.
- Keep responses in their original thread.
- Isolate malformed sync events and preserve cancellation/delivery failures.
- Raw state updates replace previous content, including empty-content updates.

## [1.3.2] - 2026-09-26

### Fixed
- Rate-limited HTTP requests now wait and retry with the original payload and transaction ID,
  honoring `Retry-After` (including HTTP dates) or the legacy `retry_after_ms` fallback.
- Sync and post-login initialization failures retry with the existing in-memory
  session, rather than forcing another password login.
- Dispose HTTP requests/responses and wait on local throttling instead of throwing.

## [1.3.1] - 2026-08-06

### Fixed
- Query string parameters are now percent-encoded with `Uri.EscapeDataString`. Previously keys
  and values were interpolated raw, so `since`, `from` and `filter` values containing `&`, `=`,
  `#`, `+` or spaces produced malformed URLs — parameters got split, the query was truncated at
  `#`, and `+` arrived at the server as a space. Inline JSON filters were affected in particular.
  Thanks to @keryx-io (#86).

### Changed
- Updated dependencies: BouncyCastle.Cryptography 2.6.2 → 2.7.0, Microsoft.Extensions.Http
  10.0.3 → 10.0.10.

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
- **Auto-join on invite**: Set `MatrixClient.AutoJoinOnInvite = true` to automatically
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
