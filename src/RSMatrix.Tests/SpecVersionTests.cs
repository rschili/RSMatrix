using RSMatrix.Models;

namespace RSMatrix.Tests;

public class SpecVersionTests
{
    [Test]
    [Arguments("v1.2", 1, 2, null, null)]
    [Arguments("r1.2.3", 1, 2, 3, null)]
    [Arguments("v1.2-metadata", 1, 2, null, "metadata")]
    public async Task TryParse_ReadsValidVersions(string input, int x, int y, int? z, string? metadata)
    {
        await Assert.That(SpecVersion.TryParse(input, out var version)).IsTrue();
        await Assert.That(version!.X).IsEqualTo(x);
        await Assert.That(version.Y).IsEqualTo(y);
        await Assert.That(version.Z).IsEqualTo(z);
        await Assert.That(version.Metadata).IsEqualTo(metadata);
        await Assert.That(version.VersionString).IsEqualTo(input);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("invalid")]
    [Arguments("v1.2.3")]
    [Arguments("r1.2-invalid")]
    public async Task TryParse_RejectsInvalidVersions(string? input)
    {
        await Assert.That(SpecVersion.TryParse(input!, out _)).IsFalse();
    }

    [Test]
    public async Task Comparer_SortsByNumber_WithMetadataBeforeTheRelease()
    {
        var versions = new List<string> { "r0.2.3", "v2.1", "v1.2-alpha", "r0.2.4", "v1.3", "v1.2", "v1.2-beta" };
        var parsedVersions = versions.Select(v => SpecVersion.TryParse(v, out var version) ? version : null).ToList();
        var sortedVersions = parsedVersions.OrderBy(v => v, SpecVersion.Comparer.Instance).Select(v => v!.VersionString).ToList();
        await Assert.That(sortedVersions).IsEquivalentTo(new List<string> { "r0.2.3", "r0.2.4", "v1.2-alpha", "v1.2-beta", "v1.2", "v1.3", "v2.1" });
    }
}
