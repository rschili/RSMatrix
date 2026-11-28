using System.Globalization;
using RSMatrix.Desktop.Controls;
using RSMatrix.Desktop.ViewModels;

namespace RSMatrix.Desktop.Tests;

public class FileRowTests
{
    [Test]
    [Arguments("report.pdf", "report.pdf")]
    [Arguments("../../.bashrc", "bashrc")]
    [Arguments("..\\..\\windows\\evil.exe", "evil.exe")]
    [Arguments("/etc/passwd", "passwd")]
    [Arguments("a\0b.txt", "a_b.txt")]
    [Arguments("..", "download")]
    [Arguments("", "download")]
    public async Task FileNames_FromOtherUsers_LoseTheirDirectories(string fileName, string expected)
        => await Assert.That(FileRowView.ToSafeFileName(fileName)).IsEqualTo(expected);

    [Test]
    [Arguments(1L, "1 byte")]
    [Arguments(900L, "900 bytes")]
    [Arguments(33699L, "33 KB")]
    [Arguments(1572864L, "1.5 MB")]
    [Arguments(5368709120L, "5 GB")]
    public async Task Sizes_AreShort(long bytes, string expected)
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            await Assert.That(FileRowView.FormatSize(bytes)).IsEqualTo(expected);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Test]
    public async Task ProgressStream_CountsWhatPassesThrough()
    {
        var reported = new List<long>();
        using var inner = new MemoryStream();
        await using var stream = new ProgressStream(inner, new SynchronousProgress(reported.Add));

        await stream.WriteAsync(new byte[100]);
        stream.Write(new byte[50], 0, 50);

        await Assert.That(inner.Length).IsEqualTo(150L);
        await Assert.That(reported).IsEquivalentTo([100L, 150L]);
    }

    private sealed class SynchronousProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
