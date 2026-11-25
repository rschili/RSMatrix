# RSMatrix.Desktop

A small Matrix text client for Windows, Linux and macOS, built with Avalonia on top of RSMatrix. It connects to one account and lists its rooms on the left.

## Running it

```sh
make desktop-sample                 # built-in sample data, no server needed
make matrix-up && make matrix-seed  # local Synapse with rooms and messages
make desktop                        # signs in as alice on the local server
make desktop DESKTOP_USER=bob       # a second instance, e.g. to try encryption
```

`make matrix-seed` creates rooms for alice, bob and carol, with formatted messages, a direct chat, an encrypted room, an image and a room with 150 messages for scrolling back. All users have the password `rsmatrix-test-password`.

`make desktop` starts with fresh local data each time, because a recreated server no longer knows the old device. Run the project without arguments to use the normal data directory and the sign-in dialog.

Command line options: `--sample`, `--data-dir DIR`, `--user ID`, `--password PW`, `--homeserver URL`, `--recovery-key-file PATH`, `--verbose`. The app keeps the account's recovery key in that file, by default in the account's data directory: RSMatrix saves a new key there when it sets up cross-signing, and reads it when a new device needs it. `make desktop` keeps it next to the data directory, so it survives the fresh start. With a password, the app signs in right away. Logs go to the console.

## Settings

The `⋯` menu next to the user opens the settings: theme, font, text size, and whether the room list shows icons and the latest message. They are saved in `settings.json` in the data directory and apply after OK. `Appearance` puts them into the application resources, where the views read them through `DynamicResource`. Font sizes in the views use the resource keys `FontSizeTitle`, `FontSizeCompact`, `FontSizeCaption`, `FontSizeSmall` and `FontSizeEmoji`, which scale with the chosen size.

## Looking at it without a display

`make desktop-snapshots` renders the app headless with sample data and writes PNG files to `temp/snapshots`: the signed-out window and sign-in dialog, each sample room in light and dark, the message actions, a thread, scrolling back, the composer, a narrow window, the member list in a wide and a narrow window, the settings dialog and a few appearance choices. With `--live USER_ID PASSWORD HOMESERVER` it signs in to a server instead and renders every room.

## How it is built

- `Chat/` holds what the UI needs from a connection: `IChatSession` with app-level rooms, messages and events.
- `Matrix/MatrixChatSession` implements it with `MatrixClient`. Workarounds for things RSMatrix lacks live in this one class.
- `Sample/SampleChatSession` implements it with built-in data, including formatting and images that RSMatrix doesn't deliver yet.
- `Content/` turns message bodies into a `MessageDocument`. The HTML parser interprets only the tags and attributes the spec suggests. Links must be absolute with an allowed scheme, and images load only from `mxc://` URIs. Nothing is rendered by a browser engine.
- `Controls/` renders documents as native Avalonia controls: selectable text with clickable links and spoilers, code blocks, quotes, lists, tables and images.
- `ViewModels/` uses CommunityToolkit.Mvvm. A room and each of its threads are a `ConversationViewModel` with their own `RoomTimeline`, which keeps day separators, sender grouping and duplicate handling out of the views. They share one `RoomMessages`, which attaches reactions, read receipts and reply targets to their message.
- `Views/TimelineView` keeps the reader at the newest message, and keeps the visible messages in place while older ones load above.

## What RSMatrix doesn't offer yet

The app works around these, and each would make it simpler or better:

- **Room list**: no public list of joined rooms or change notifications. Rooms appear once a text message from them arrives.
- **Members**: the sync lazy-loads members, and the full list from `/members` is only used inside the encryption code. The member list shows only the people RSMatrix has seen in the room's state and timeline.
- **Room names**: no computed display name. The app follows the spec's order, but without the member counts from the room summary.
- **Formatted messages**: `ReceivedTextMessage` has `Body` only, so `formatted_body` is lost.
- **Images and other media**: `m.image` events are dropped, and there is no media download.
- **Homeserver URL**: discovery always starts at `https://<user domain>`. A message handler answers the well-known request instead.
- **Resuming**: a stored session still needs the password, so the app can't sign in on start.
- **Sign out**: there is no logout, so disconnecting leaves the device signed in.
- **Connection state**: `IsSyncing` and `LastSyncAt` have no change notifications, so they are polled.
- **Read state**: no unread counts or read markers from the server. Only messages after the initial sync count as unread.
- **Reactions and read receipts**: not delivered. Your own reactions are sent and removed, but only the ones sent in this session can be removed, and other people's reactions only show in the sample.
- **Replies**: received messages don't say what they reply to. Sending replies works.
- **Threads**: received messages carry their thread. Sending to a thread needs the received message object, so replying in threads only works in the sample.
- **Event source**: no raw event JSON, so "View source" only works in the sample.
- **Rate limit**: all requests except sync share one local bucket of 10 requests, refilled at 600 per hour. History pages and quick replies wait several seconds once it is empty.
- **Cancellation**: sending and loading history only stop with the whole client.
