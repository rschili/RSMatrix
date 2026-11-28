using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using RSMatrix.Crypto;
using RSMatrix.Models;
using static RSMatrix.Tests.MessageUpdateTests;

namespace RSMatrix.Tests;

public class AttachmentTests
{
    private const string Download = "/_matrix/client/v1/media/download/";
    private const string Thumbnail = "/_matrix/client/v1/media/thumbnail/";

    private static async Task<Message> ReceiveAsync(MatrixTestContext context, string content)
    {
        await context.Process(Sync(Event("$m", "m.room.message", content)));
        return context.ReadMessages().Single();
    }

    private static HttpResponseMessage Bytes(byte[] content, string? contentType = null, string? disposition = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
        if (contentType != null)
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        if (disposition != null)
            response.Content.Headers.ContentDisposition = ContentDispositionHeaderValue.Parse(disposition);
        return response;
    }

    private static HttpResponseMessage NotFound()
        => MatrixTestContext.Json("""{"errcode":"M_NOT_FOUND","error":"Not found"}""", HttpStatusCode.NotFound);

    /// <summary>Encrypts like a sending client does and returns the bytes on the server and the event's file object.</summary>
    private static (byte[] Ciphertext, string File) Encrypt(byte[] plaintext, string url)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var iv = new byte[16];
        RandomNumberGenerator.Fill(iv.AsSpan(0, 8));
        var ciphertext = CryptoPrimitives.AesCtr(key, iv, plaintext);
        var k = Convert.ToBase64String(key).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var file = $$$"""
            {"url":"{{{url}}}","v":"v2","iv":"{{{CryptoPrimitives.ToBase64(iv)}}}",
             "key":{"kty":"oct","alg":"A256CTR","ext":true,"key_ops":["encrypt","decrypt"],"k":"{{{k}}}"},
             "hashes":{"sha256":"{{{CryptoPrimitives.ToBase64(SHA256.HashData(ciphertext))}}}"}}
            """;
        return (ciphertext, file);
    }

    [Test]
    public async Task Image_HasItsFileInfo_AndNoCaption()
    {
        using var context = new MatrixTestContext();
        var message = await ReceiveAsync(context, """
            {"msgtype":"m.image","body":"holiday.jpg","filename":"holiday.jpg","url":"mxc://example.org/abc",
             "info":{"mimetype":"image/jpeg","size":31037,"w":1280,"h":960}}
            """);

        await Assert.That(message.Kind).IsEqualTo(MessageKind.Image);
        await Assert.That(message.Body).IsEqualTo("");
        await Assert.That(message.Html).IsNull();
        var attachment = message.Attachment!;
        await Assert.That(attachment.FileName).IsEqualTo("holiday.jpg");
        await Assert.That(attachment.MimeType).IsEqualTo("image/jpeg");
        await Assert.That(attachment.Size).IsEqualTo(31037L);
        await Assert.That(attachment.Width).IsEqualTo(1280);
        await Assert.That(attachment.Height).IsEqualTo(960);
        await Assert.That(attachment.Duration).IsNull();
        await Assert.That(attachment.MxcUri).IsEqualTo("mxc://example.org/abc");
    }

    [Test]
    public async Task BodyDifferentFromFileName_IsTheCaption()
    {
        using var context = new MatrixTestContext();
        var message = await ReceiveAsync(context, """
            {"msgtype":"m.image","body":"Look at this","format":"org.matrix.custom.html","formatted_body":"<b>Look</b> at this",
             "filename":"holiday.jpg","url":"mxc://example.org/abc"}
            """);

        await Assert.That(message.Body).IsEqualTo("Look at this");
        await Assert.That(message.Html).IsEqualTo("<b>Look</b> at this");
        await Assert.That(message.Attachment!.FileName).IsEqualTo("holiday.jpg");
    }

    [Test]
    public async Task WithoutFileName_TheBodyIsTheFileName()
    {
        using var context = new MatrixTestContext();
        var message = await ReceiveAsync(context, """
            {"msgtype":"m.video","body":"clip.mp4","url":"mxc://example.org/clip","info":{"duration":61500}}
            """);

        await Assert.That(message.Body).IsEqualTo("");
        await Assert.That(message.Attachment!.FileName).IsEqualTo("clip.mp4");
        await Assert.That(message.Attachment.Duration).IsEqualTo(TimeSpan.FromMilliseconds(61500));
    }

    [Test]
    [Arguments("""{"msgtype":"m.file","body":"report.pdf"}""")]
    [Arguments("""{"msgtype":"m.file","body":"report.pdf","url":"https://example.org/report.pdf"}""")]
    [Arguments("""{"msgtype":"m.file","body":"report.pdf","url":"mxc://example.org/../admin"}""")]
    [Arguments("""{"msgtype":"m.file","body":"report.pdf","file":{"url":"mxc://example.org/abc","v":"v1"}}""")]
    public async Task WithoutUsableFile_KeepsTheBody(string content)
    {
        using var context = new MatrixTestContext();
        var message = await ReceiveAsync(context, content);

        await Assert.That(message.Kind).IsEqualTo(MessageKind.File);
        await Assert.That(message.Attachment).IsNull();
        await Assert.That(message.Body).IsEqualTo("report.pdf");
    }

    [Test]
    public async Task OddNumbers_AreReadLeniently()
    {
        using var context = new MatrixTestContext();
        var message = await ReceiveAsync(context, """
            {"msgtype":"m.image","body":"a.png","url":"mxc://example.org/a","info":{"size":"1234","w":800.0,"h":{"x":1},"duration":-5}}
            """);

        var attachment = message.Attachment!;
        await Assert.That(attachment.Size).IsEqualTo(1234L);
        await Assert.That(attachment.Width).IsEqualTo(800);
        await Assert.That(attachment.Height).IsNull();
        await Assert.That(attachment.Duration).IsNull();
    }

    [Test]
    public async Task Edit_CanReplaceTheFile()
    {
        using var context = new MatrixTestContext();
        await ReceiveAsync(context, """{"msgtype":"m.image","body":"a.png","url":"mxc://example.org/a"}""");

        await context.Process(Sync(Event("$e", "m.room.message", """
            {"msgtype":"m.image","body":"* b.png","url":"mxc://example.org/b",
             "m.new_content":{"msgtype":"m.image","body":"b.png","url":"mxc://example.org/b"},
             "m.relates_to":{"rel_type":"m.replace","event_id":"$m"}}
            """, ts: 1700000001000)));
        var edited = context.ReadUpdates().Select(u => u.Value).OfType<MessageChanged>().Single().Message!;

        await Assert.That(edited.Attachment!.MxcUri).IsEqualTo("mxc://example.org/b");
        await Assert.That(edited.Attachment.FileName).IsEqualTo("b.png");
        await Assert.That(edited.Body).IsEqualTo("");
    }

    [Test]
    public async Task Edit_OfAnAttachment_IsRefused()
    {
        using var context = new MatrixTestContext();
        var message = await ReceiveAsync(context, """{"msgtype":"m.image","body":"a.png","url":"mxc://example.org/a"}""");

        await Assert.ThrowsAsync<NotSupportedException>(() => message.EditAsync("caption"));
    }

    [Test]
    public async Task Download_UsesTheAuthenticatedEndpoint()
    {
        using var context = new MatrixTestContext();
        var message = await ReceiveAsync(context, """{"msgtype":"m.file","body":"a.bin","url":"mxc://example.org/abc"}""");
        context.Send = (_, _) => Task.FromResult(Bytes([1, 2, 3]));

        var content = await message.Attachment!.DownloadAsync();

        await Assert.That(content).IsEquivalentTo(new byte[] { 1, 2, 3 });
        await Assert.That(context.Requests.Single().Path).IsEqualTo(Download + "example.org/abc");
    }

    [Test]
    public async Task EncryptedFile_IsDecrypted_IntoMemoryAndStreams()
    {
        using var context = new MatrixTestContext();
        var plaintext = RandomNumberGenerator.GetBytes(200_000);
        var (ciphertext, file) = Encrypt(plaintext, "mxc://example.org/secret");
        var message = await ReceiveAsync(context, $$"""{"msgtype":"m.file","body":"secret.bin","file":{{file}}}""");
        context.Send = (_, _) => Task.FromResult(Bytes(ciphertext, "application/octet-stream"));

        await Assert.That(message.Attachment!.MxcUri).IsEqualTo("mxc://example.org/secret");
        await Assert.That(await message.Attachment.DownloadAsync()).IsEquivalentTo(plaintext);
        using var destination = new MemoryStream();
        await message.Attachment.DownloadAsync(destination);
        await Assert.That(destination.ToArray()).IsEquivalentTo(plaintext);
    }

    [Test]
    public async Task EncryptedFile_ThatDoesNotMatchItsHash_Throws()
    {
        using var context = new MatrixTestContext();
        var (ciphertext, file) = Encrypt([1, 2, 3, 4], "mxc://example.org/secret");
        ciphertext[0] ^= 1;
        var message = await ReceiveAsync(context, $$"""{"msgtype":"m.file","body":"secret.bin","file":{{file}}}""");
        context.Send = (_, _) => Task.FromResult(Bytes(ciphertext));

        await Assert.ThrowsAsync<InvalidDataException>(() => message.Attachment!.DownloadAsync());
    }

    [Test]
    public async Task Thumbnail_OfAnImage_IsScaledByTheServer()
    {
        using var context = new MatrixTestContext();
        var message = await ReceiveAsync(context, """{"msgtype":"m.image","body":"a.png","url":"mxc://example.org/a"}""");
        context.Send = (_, _) => Task.FromResult(Bytes([7]));

        var thumbnail = await message.Attachment!.DownloadThumbnailAsync(800, 600);

        await Assert.That(thumbnail).IsEquivalentTo(new byte[] { 7 });
        await Assert.That(context.Requests.Single().Path).IsEqualTo(Thumbnail + "example.org/a?width=800&height=600&method=scale");
    }

    [Test]
    public async Task Thumbnail_FallsBackToTheSendersThumbnail()
    {
        using var context = new MatrixTestContext();
        var message = await ReceiveAsync(context, """
            {"msgtype":"m.image","body":"a.svg","url":"mxc://example.org/a","info":{"thumbnail_url":"mxc://example.org/t"}}
            """);
        context.Send = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/a")
            ? MatrixTestContext.Json("""{"errcode":"M_UNKNOWN","error":"Unsupported"}""", HttpStatusCode.BadRequest)
            : Bytes([8]));

        var thumbnail = await message.Attachment!.DownloadThumbnailAsync(320, 240);

        await Assert.That(thumbnail).IsEquivalentTo(new byte[] { 8 });
        await Assert.That(context.Requests.Select(r => r.Path).ToList()).IsEquivalentTo([
            Thumbnail + "example.org/a?width=320&height=240&method=scale",
            Thumbnail + "example.org/t?width=320&height=240&method=scale",
        ]);
    }

    [Test]
    public async Task Thumbnail_OfAFileWithoutOne_IsNull_WithoutARequest()
    {
        using var context = new MatrixTestContext();
        var message = await ReceiveAsync(context, """{"msgtype":"m.file","body":"a.pdf","url":"mxc://example.org/a"}""");

        await Assert.That(await message.Attachment!.DownloadThumbnailAsync(320, 240)).IsNull();
        await Assert.That(context.Requests).IsEmpty();
    }

    [Test]
    public async Task Thumbnail_OfAnEncryptedImage_IsTheSendersDecryptedThumbnail()
    {
        using var context = new MatrixTestContext();
        var thumbnailBytes = RandomNumberGenerator.GetBytes(1000);
        var (image, imageFile) = Encrypt([1, 2, 3], "mxc://example.org/image");
        var (thumbnail, thumbnailFile) = Encrypt(thumbnailBytes, "mxc://example.org/thumb");
        var message = await ReceiveAsync(context,
            $$$"""{"msgtype":"m.image","body":"a.jpg","file":{{{imageFile}}},"info":{"thumbnail_file":{{{thumbnailFile}}}}}""");
        context.Send = (_, _) => Task.FromResult(Bytes(thumbnail));

        await Assert.That(await message.Attachment!.DownloadThumbnailAsync(800, 600)).IsEquivalentTo(thumbnailBytes);
        await Assert.That(context.Requests.Single().Path).IsEqualTo(Download + "example.org/thumb");
    }

    [Test]
    public async Task Thumbnail_OfAnEncryptedImageWithoutOne_IsNull()
    {
        using var context = new MatrixTestContext();
        var (_, file) = Encrypt([1, 2, 3], "mxc://example.org/image");
        var message = await ReceiveAsync(context, $$"""{"msgtype":"m.image","body":"a.jpg","file":{{file}}}""");

        await Assert.That(await message.Attachment!.DownloadThumbnailAsync(800, 600)).IsNull();
        await Assert.That(context.Requests).IsEmpty();
    }

    [Test]
    public async Task ClientThumbnail_CropsForAvatars()
    {
        using var context = new MatrixTestContext();
        context.Send = (_, _) => Task.FromResult(Bytes([9]));

        var avatar = await context.Client.DownloadThumbnailAsync("mxc://example.org/avatar", 96, 96, crop: true);

        await Assert.That(avatar).IsEquivalentTo(new byte[] { 9 });
        await Assert.That(context.Requests.Single().Path).IsEqualTo(Thumbnail + "example.org/avatar?width=96&height=96&method=crop");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("mxc://example.org")]
    [Arguments("mxc://example.org/a/b")]
    [Arguments("mxc://exa mple.org/a")]
    public async Task ClientCalls_WithInvalidUri_ReturnNull_WithoutARequest(string? mxcUri)
    {
        using var context = new MatrixTestContext();

        await Assert.That(await context.Client.DownloadThumbnailAsync(mxcUri, 96, 96)).IsNull();
        await Assert.That(await context.Client.DownloadAsync(mxcUri)).IsNull();
        await Assert.That(context.Requests).IsEmpty();
    }

    [Test]
    public async Task ClientDownload_HasTheServersTypeAndFileName()
    {
        using var context = new MatrixTestContext();
        context.Send = (_, _) => Task.FromResult(Bytes([1], "image/png", "inline; filename=\"cat.png\""));

        var file = (await context.Client.DownloadAsync("mxc://example.org:8448/cat"))!;

        await Assert.That(file.Content).IsEquivalentTo(new byte[] { 1 });
        await Assert.That(file.ContentType).IsEqualTo("image/png");
        await Assert.That(file.FileName).IsEqualTo("cat.png");
        await Assert.That(context.Requests.Single().Path).IsEqualTo(Download + "example.org%3A8448/cat");
    }

    [Test]
    public async Task ClientCalls_ReturnNull_WhenTheServerHasNoFile()
    {
        using var context = new MatrixTestContext();
        context.Send = (_, _) => Task.FromResult(NotFound());

        await Assert.That(await context.Client.DownloadAsync("mxc://example.org/gone")).IsNull();
        await Assert.That(await context.Client.DownloadThumbnailAsync("mxc://example.org/gone", 96, 96)).IsNull();
    }

    [Test]
    public async Task AesCtr_MatchesTheNistVector_AndCarriesOnAcrossPieces()
    {
        // NIST SP 800-38A, F.5.5 CTR-AES256.Encrypt, blocks 1 and 2.
        var key = Convert.FromHexString("603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4");
        var iv = Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff");
        var plaintext = Convert.FromHexString("6bc1bee22e409f96e93d7e117393172aae2d8a571e03ac9c9eb76fac45af8e51");
        await Assert.That(Convert.ToHexStringLower(CryptoPrimitives.AesCtr(key, iv, plaintext)))
            .IsEqualTo("601ec313775789a5b7a7f504bbf3d228f443e3ca4d62b59aca84e990cacaf5c5");

        var data = RandomNumberGenerator.GetBytes(10_000);
        var whole = CryptoPrimitives.AesCtr(key, iv, data);
        using var cipher = new AesCtrCipher(key, iv);
        var pieces = new byte[data.Length];
        for (var offset = 0; offset < data.Length; offset += 777)
        {
            var length = Math.Min(777, data.Length - offset);
            cipher.Transform(data.AsSpan(offset, length), pieces.AsSpan(offset, length));
        }
        await Assert.That(pieces).IsEquivalentTo(whole);
    }
}
