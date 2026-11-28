using System.Net.Http.Headers;
using System.Security.Cryptography;
using RSMatrix.Crypto;
using RSMatrix.Http;
using RSMatrix.Models;
using static RSMatrix.IntegrationTests.UpdateReader;

namespace RSMatrix.IntegrationTests;

public class MediaTests
{
    // A 64×48 PNG in one color, so Synapse has something to thumbnail.
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAEAAAAAwCAIAAAAuKetIAAAAU0lEQVR4nO3PQQ3AIADAQEANcpCNrIngcVnSU9DOe/b4s6UDXjWgNaA1oDWgNaA1oDWgNaA1oDWgNaA1oDWgNaA1oDWgNaA1oDWgNaA1oDWgNaB9KlwBoBtb29kAAAAASUVORK5CYII=");

    [Test]
    public async Task Attachments_Download_Plain_Encrypted_AndAsThumbnails()
    {
        await using var server = new LocalHomeserver();
        var alice = await server.LoginAsync("alice");
        var bob = await server.LoginClientAsync("bob");
        var created = await LocalHomeserver.RequestAsync(alice, "/_matrix/client/v3/createRoom", HttpMethod.Post,
            new { name = $"RSMatrix media {Guid.NewGuid():N}", preset = "private_chat", invite = new[] { "@bob:localhost" } });
        var roomId = created.GetProperty("room_id").GetString()!;
        await AcceptInviteAsync(bob, roomId, server.CancellationToken);
        var token = server.CancellationToken;

        // An image with a caption.
        var imageUri = await UploadAsync(alice, Png, "image/png", "dot.png");
        var imageId = await SendAsync(alice, roomId, new
        {
            msgtype = "m.image", body = "A dot", filename = "dot.png", url = imageUri,
            info = new { mimetype = "image/png", w = 64, h = 48, size = Png.Length },
        });
        var image = await ReceiveAsync(bob, imageId, token);
        await Assert.That(image.Body).IsEqualTo("A dot");
        var attachment = image.Attachment!;
        await Assert.That(attachment.FileName).IsEqualTo("dot.png");
        await Assert.That(await attachment.DownloadAsync(token)).IsEquivalentTo(Png);
        using (var stream = new MemoryStream())
        {
            await attachment.DownloadAsync(stream, token);
            await Assert.That(stream.ToArray()).IsEquivalentTo(Png);
        }
        var thumbnail = await attachment.DownloadThumbnailAsync(32, 32, token);
        await Assert.That(thumbnail).IsNotNull();

        // The same file by its URI, as for avatars.
        var file = await bob.DownloadAsync(imageUri, token);
        await Assert.That(file!.Content).IsEquivalentTo(Png);
        await Assert.That(file.ContentType).IsEqualTo("image/png");
        await Assert.That(file.FileName).IsEqualTo("dot.png");
        await Assert.That(await bob.DownloadThumbnailAsync(imageUri, 32, 32, crop: true, token)).IsNotNull();
        await Assert.That(await bob.DownloadAsync("mxc://localhost/doesnotexist", token)).IsNull();
        await Assert.That(await bob.DownloadThumbnailAsync("mxc://localhost/doesnotexist", 32, 32, cancellationToken: token)).IsNull();

        // An encrypted file, the way an encrypting client uploads it.
        var plaintext = RandomNumberGenerator.GetBytes(300_000);
        var key = RandomNumberGenerator.GetBytes(32);
        var iv = new byte[16];
        RandomNumberGenerator.Fill(iv.AsSpan(0, 8));
        var ciphertext = CryptoPrimitives.AesCtr(key, iv, plaintext);
        var secretUri = await UploadAsync(alice, ciphertext, "application/octet-stream", null);
        var secretId = await SendAsync(alice, roomId, new
        {
            msgtype = "m.file", body = "secret.bin",
            file = new
            {
                url = secretUri, v = "v2", iv = CryptoPrimitives.ToBase64(iv),
                key = new { kty = "oct", alg = "A256CTR", ext = true, key_ops = new[] { "encrypt", "decrypt" },
                    k = CryptoPrimitives.ToBase64(key).Replace('+', '-').Replace('/', '_') },
                hashes = new { sha256 = CryptoPrimitives.ToBase64(SHA256.HashData(ciphertext)) },
            },
        });
        var secret = (await ReceiveAsync(bob, secretId, token)).Attachment!;
        await Assert.That(await secret.DownloadAsync(token)).IsEquivalentTo(plaintext);
        await Assert.That(await secret.DownloadThumbnailAsync(32, 32, token)).IsNull();
    }

    private static async Task<string> UploadAsync(HttpClientParameters user, byte[] content, string contentType, string? fileName)
    {
        var upload = new ByteArrayContent(content);
        upload.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        var path = fileName == null ? "/_matrix/media/v3/upload" : $"/_matrix/media/v3/upload?filename={Uri.EscapeDataString(fileName)}";
        var response = await HttpClientHelper.SendAsync<System.Text.Json.JsonElement>(user, path, HttpMethod.Post, upload);
        return response.GetProperty("content_uri").GetString()!;
    }

    private static async Task<string> SendAsync(HttpClientParameters user, string roomId, object content)
    {
        var sent = await LocalHomeserver.RequestAsync(user,
            $"/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomId)}/send/m.room.message/{Guid.NewGuid():N}", HttpMethod.Put, content);
        return sent.GetProperty("event_id").GetString()!;
    }
}
