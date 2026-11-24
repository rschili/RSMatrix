#!/usr/bin/env python3
"""Fills a fresh local test server with rooms and messages for trying out clients.

Run after `make matrix-up`. Uses the provisioned users alice, bob and carol. Only talks to
loopback servers, and does nothing if alice already has rooms.
"""
import json
import math
import os
import sys
import urllib.error
import urllib.parse
import urllib.request
import struct
import uuid
import zlib

PORT = os.environ.get("MATRIX_PORT", "8008")
BASE = f"http://127.0.0.1:{PORT}"
PASSWORD = "rsmatrix-test-password"
SERVER = "localhost"


def request(method, path, token=None, body=None, raw=None, content_type="application/json"):
    data = raw if raw is not None else (json.dumps(body).encode() if body is not None else None)
    req = urllib.request.Request(BASE + path, data=data, method=method)
    if data is not None:
        req.add_header("Content-Type", content_type)
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    # The server is local; never send this through a proxy.
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    try:
        with opener.open(req, timeout=30) as response:
            return json.load(response)
    except urllib.error.HTTPError as error:
        sys.exit(f"{method} {path} failed: {error.code} {error.read().decode(errors='replace')}")


class User:
    def __init__(self, name):
        self.name = name
        self.id = f"@{name}:{SERVER}"
        login = request("POST", "/_matrix/client/v3/login", body={
            "type": "m.login.password",
            "identifier": {"type": "m.id.user", "user": name},
            "password": PASSWORD,
            "initial_device_display_name": "seed script",
        })
        self.token = login["access_token"]

    def call(self, method, path, body=None, **kwargs):
        return request(method, path, self.token, body, **kwargs)

    def create_room(self, invite, **options):
        room_id = self.call("POST", "/_matrix/client/v3/createRoom", {"invite": [u.id for u in invite], **options})["room_id"]
        for user in invite:
            user.call("POST", f"/_matrix/client/v3/join/{urllib.parse.quote(room_id)}", {})
        return room_id

    def send(self, room_id, body, html=None, msgtype="m.text", **extra):
        content = {"msgtype": msgtype, "body": body, **extra}
        if html is not None:
            content["format"] = "org.matrix.custom.html"
            content["formatted_body"] = html
        path = f"/_matrix/client/v3/rooms/{urllib.parse.quote(room_id)}/send/m.room.message/{uuid.uuid4().hex}"
        self.call("PUT", path, content)

    def logout(self):
        self.call("POST", "/_matrix/client/v3/logout", {})


def landscape_png(width=480, height=300):
    rows = bytearray()
    for y in range(height):
        rows.append(0)
        t = y / height
        for x in range(width):
            ridge = 0.62 + 0.06 * math.sin(x / width * 9)
            if t > ridge:
                rows.extend((52, 48, 82))
            else:
                rows.extend((int(40 + 200 * t), int(60 + 110 * t), int(110 + 30 * t)))

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    header = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(bytes(rows), 9)) + chunk(b"IEND", b"")


def main():
    alice, bob, carol = User("alice"), User("bob"), User("carol")
    try:
        if alice.call("GET", "/_matrix/client/v3/joined_rooms")["joined_rooms"]:
            print("Alice already has rooms, so the server was seeded before. Recreate it with make matrix-down matrix-up.")
            return

        for user in (alice, bob, carol):
            user.call("PUT", f"/_matrix/client/v3/profile/{urllib.parse.quote(user.id)}/displayname",
                      {"displayname": user.name.capitalize()})

        general = alice.create_room([bob, carol], name="General", topic="Everything and nothing", preset="private_chat")
        alice.send(general, "Welcome to the local test server!")
        bob.send(general, "Hi Alice. Is this the new desktop client?")
        alice.send(general, "Yes, it runs on RSMatrix and Avalonia.",
                   "Yes, it runs on <b>RSMatrix</b> and <em>Avalonia</em>.")
        carol.send(general, "Here is a snippet:\n\nawait client.ConnectAsync();",
                   "<p>Here is a snippet:</p><pre><code class=\"language-csharp\">await client.ConnectAsync();\n</code></pre>")
        bob.send(general, "> Here is a snippet\n\nNice, formatting works.",
                 "<blockquote><p>Here is a snippet</p></blockquote><p>Nice, <i>formatting</i> works.</p>")
        carol.send(general, "Alice: the spec is at https://spec.matrix.org/v1.17/client-server-api/",
                   f"<a href=\"https://matrix.to/#/{alice.id}\">Alice</a>: the spec is at "
                   "<a href=\"https://spec.matrix.org/v1.17/client-server-api/\">spec.matrix.org</a>",
                   **{"m.mentions": {"user_ids": [alice.id]}})
        alice.send(general, "A message\nwith\nline breaks.")
        bob.send(general, "Shopping list:\n1. milk\n2. bread",
                 "<p>Shopping list:</p><ol><li>milk</li><li>bread</li></ol>")

        direct = bob.create_room([alice], preset="trusted_private_chat", is_direct=True)
        for user, other in ((alice, bob), (bob, alice)):
            user.call("PUT", f"/_matrix/client/v3/user/{urllib.parse.quote(user.id)}/account_data/m.direct",
                      {other.id: [direct]})
        bob.send(direct, "Hey Alice, got a minute?")
        alice.send(direct, "Sure, what's up?")

        private = alice.create_room([bob], name="Encrypted", preset="private_chat", initial_state=[{
            "type": "m.room.encryption", "state_key": "", "content": {"algorithm": "m.megolm.v1.aes-sha2"},
        }])
        bob.send(private, "This room is encrypted, but the seed script can't encrypt, so this message is plain. "
                          "Sign in as alice and bob in two app instances to try encrypted messages.")

        builds = carol.create_room([alice], name="Builds", preset="private_chat")
        carol.send(builds, "Build 412 passed\nRSMatrix: 312 tests passed", msgtype="m.notice",
                   html="<h3>Build 412 passed</h3><table><tr><th>Project</th><th>Tests</th></tr>"
                        "<tr><td>RSMatrix</td><td>312 passed</td></tr></table>")

        media = carol.create_room([alice, bob], name="Media", preset="private_chat")
        image = landscape_png()
        upload = carol.call("POST", "/_matrix/media/v3/upload?filename=landscape.png", raw=image, content_type="image/png")
        carol.send(media, "landscape.png", msgtype="m.image", url=upload["content_uri"],
                   info={"mimetype": "image/png", "size": len(image), "w": 480, "h": 300})
        carol.send(media, "The image above is an m.image event.")

        history = bob.create_room([alice, carol], name="History", preset="private_chat")
        for number in range(1, 151):
            (bob if number % 3 else carol).send(history, f"Message {number} of 150")
    finally:
        for user in (alice, bob, carol):
            user.logout()

    print(f"Seeded {BASE}. Sign in as @alice:{SERVER} (or bob, carol) with password {PASSWORD}.")


if __name__ == "__main__":
    main()
