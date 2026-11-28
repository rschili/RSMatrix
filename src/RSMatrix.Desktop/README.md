# RSMatrix.Desktop

A small Matrix text client for Windows, Linux and macOS, built with Avalonia on top of RSMatrix. It connects to one account and lists its rooms on the left.

## Running it

```sh
make desktop-sample                 # built-in sample data, no server needed
make matrix-up && make matrix-seed  # local Synapse with rooms and messages
make desktop                        # signs in as alice on the local server
make desktop-bob                    # a second instance as bob, e.g. to chat with alice
make desktop DESKTOP_USER=carol     # any other seeded user
```

`make matrix-seed` creates rooms for alice, bob and carol, with formatted messages, a direct chat, an encrypted room, an image and a room with 150 messages for scrolling back. All users have the password `rsmatrix-test-password`.

`make desktop` starts with fresh local data each time, because a recreated server no longer knows the old device. Run the project without arguments to use the normal data directory and the sign-in dialog.

Command line options: `--sample`, `--data-dir DIR`, `--user ID`, `--password PW`, `--homeserver URL`, `--recovery-key-file PATH`, `--verbose`. The app reads the account's recovery key from that file, by default in the account's data directory, when a new device needs it to be cross-signed. `make desktop` keeps it next to the data directory, so it survives the fresh start. The app doesn't set up cross-signing for an account without it yet. With a password, the app signs in right away. Without one, it resumes the stored session of the last account and only asks for the password if the server ended the session. Logs go to the console.

## Settings

The `⋯` menu next to the user opens the settings, with a page per category: appearance (theme and message layout), text (font and size) and the room list (icons and the latest message). They are saved in `settings.json` in the data directory and apply after "Save changes". `Appearance` puts them into the application resources, where the views read them through `DynamicResource`. Font sizes in the views use the resource keys `FontSizeTitle`, `FontSizeCompact`, `FontSizeCaption`, `FontSizeSmall` and `FontSizeEmoji`, which scale with the chosen size.

The message layouts are "Comfortable", with avatars and grouped messages, and "Classic", with time, name and text in columns. The dialog shows a small picture of each, in `Assets/LayoutPreviews`. `make desktop-layout-previews` renders them again from the sample data; run it after changing how messages look.

## Presence

The line below the user's name in the bottom left shows the presence and status text; clicking it opens a menu with Online, Away and Invisible and a status field (Enter saves). Online means online while the user is active in the app and idle after the time from the settings; any mouse or keyboard input in the window, or bringing it to the front, counts as activity. Away and Invisible stay until changed. The chosen mode and idle time are saved in `settings.json`. When the server reports something else for a few seconds, usually because another device of the user is online, the line says what others see. The member list shows a presence dot on each avatar, the status text below the name, and presence with a rough last-seen time in the tooltip.

## Looking at it without a display

`make desktop-snapshots` renders the app headless with sample data and writes PNG files to `temp/snapshots`: the signed-out window and sign-in dialog, each sample room in light and dark, the message actions, a thread, scrolling back, the composer, a narrow window, the member list in a wide and a narrow window, the presence menu, the settings dialog, the classic message layout and a few appearance choices. With `--live USER_ID PASSWORD HOMESERVER` it signs in to a server instead and renders every room.

## How it is built

- `Chat/` holds what the UI needs from a connection: `IChatSession` with app-level rooms, messages and events.
- `Matrix/MatrixChatSession` implements it with `MatrixClient`. Workarounds for things RSMatrix lacks live in this one class.
- `Sample/SampleChatSession` implements it with built-in data, including formatting and images that RSMatrix doesn't deliver yet.
- `Content/` turns message bodies into a `MessageDocument`. The HTML parser interprets only the tags and attributes the spec suggests. Links must be absolute with an allowed scheme, and images load only from `mxc://` URIs. Nothing is rendered by a browser engine.
- `Controls/` renders documents as native Avalonia controls: selectable text with clickable links and spoilers, code blocks, quotes, lists, tables and images.
- `ViewModels/` uses CommunityToolkit.Mvvm. A room and each of its threads are a `ConversationViewModel` with their own `RoomTimeline`, which keeps day separators, sender grouping and duplicate handling out of the views. They share one `RoomMessages`, which attaches reactions, read receipts and reply targets to their message.
- `Views/TimelineView` keeps the reader at the newest message, and keeps the visible messages in place while older ones load above. Mouse wheel steps glide instead of jumping, because Avalonia has no smooth wheel scrolling.

## What RSMatrix doesn't offer yet

The app works around these, and each would make it simpler or better:

- **Formatted messages**: RSMatrix exposes `Message.Html`, but the app currently renders only the plain-text body.
- **Images and other media**: image and other media messages arrive, but there is no media download.
- **Sign out**: RSMatrix has `LogoutAsync`, but the app only disconnects, which leaves the device signed in.
- **Read state**: RSMatrix offers the server's counts and `HasUnread`, but the app still counts unread messages itself, only from the start of the session, and doesn't mark rooms as read yet.
- **Reactions and read receipts**: reactions arrive and are shown, but read receipts are not delivered. Only reactions sent in this session can be removed.
- **Replies**: received messages expose their reply target, but the app does not display reply previews yet. Sending replies works.
- **Threads**: received messages carry their thread, and sending to a thread works. There is no list of a room's threads yet.
- **Event source**: no raw event JSON, so "View source" only works in the sample.
- **Cancellation**: sending only stops with the whole client.
