using Narrensicher.Matrix.Models;

namespace Narrensicher.Matrix.Tests;

public class SpecVersionTests
{
    [Test]
    public async Task Constructor_ShouldInitializePropertiesCorrectly()
    {
        var version = new SpecVersion(1, 2, 3, "metadata");

        await Assert.That(version.X).IsEqualTo(1);
        await Assert.That(version.Y).IsEqualTo(2);
        await Assert.That(version.Z).IsEqualTo(3);
        await Assert.That(version.Metadata).IsEqualTo("metadata");
        await Assert.That(version.VersionString).IsEqualTo("r1.2.3-metadata");
    }

    [Test]
    public async Task Constructor_ShouldInitializePropertiesCorrectly_WhenZIsNull()
    {
        var version = new SpecVersion(1, 2, null, "metadata");

        await Assert.That(version.X).IsEqualTo(1);
        await Assert.That(version.Y).IsEqualTo(2);
        await Assert.That(version.Z).IsNull();
        await Assert.That(version.Metadata).IsEqualTo("metadata");
        await Assert.That(version.VersionString).IsEqualTo("v1.2-metadata");
    }

    [Test]
    public async Task TryParse_ShouldReturnFalse_WhenInputIsNullOrWhiteSpace()
    {
        await Assert.That(SpecVersion.TryParse(null!, out var version)).IsFalse();
        await Assert.That(SpecVersion.TryParse(string.Empty, out version)).IsFalse();
        await Assert.That(SpecVersion.TryParse("   ", out version)).IsFalse();
    }

    [Test]
    public async Task TryParse_ShouldReturnFalse_WhenInputIsInvalid()
    {
        await Assert.That(SpecVersion.TryParse("invalid", out var version)).IsFalse();
        await Assert.That(SpecVersion.TryParse("v1.2.3", out version)).IsFalse();
        await Assert.That(SpecVersion.TryParse("r1.2-invalid", out version)).IsFalse();
    }

    [Test]
    public async Task TryParse_ShouldReturnTrue_WhenInputIsValid()
    {
        await Assert.That(SpecVersion.TryParse("v1.2", out var version)).IsTrue();
        await Assert.That(version).IsNotNull();
        await Assert.That(version!.X).IsEqualTo(1);
        await Assert.That(version.Y).IsEqualTo(2);
        await Assert.That(version.Z).IsNull();
        await Assert.That(version.Metadata).IsNull();
        await Assert.That(version.VersionString).IsEqualTo("v1.2");

        await Assert.That(SpecVersion.TryParse("r1.2.3", out version)).IsTrue();
        await Assert.That(version).IsNotNull();
        await Assert.That(version!.X).IsEqualTo(1);
        await Assert.That(version.Y).IsEqualTo(2);
        await Assert.That(version.Z).IsEqualTo(3);
        await Assert.That(version.Metadata).IsNull();
        await Assert.That(version.VersionString).IsEqualTo("r1.2.3");

        await Assert.That(SpecVersion.TryParse("v1.2-metadata", out version)).IsTrue();
        await Assert.That(version).IsNotNull();
        await Assert.That(version!.X).IsEqualTo(1);
        await Assert.That(version.Y).IsEqualTo(2);
        await Assert.That(version.Z).IsNull();
        await Assert.That(version.Metadata).IsEqualTo("metadata");
        await Assert.That(version.VersionString).IsEqualTo("v1.2-metadata");
    }

    [Test]
    public async Task CompareTo_ShouldReturnCorrectComparisonResult()
    {
        var version1 = new SpecVersion(1, 2, 3, null);
        var version2 = new SpecVersion(1, 2, 3, null);
        var version3 = new SpecVersion(1, 2, 4, null);
        var version4 = new SpecVersion(1, 3, null, null);

        await Assert.That(version1.CompareTo(version2)).IsEqualTo(0);
        await Assert.That(version1.CompareTo(version3)).IsEqualTo(-1);
        await Assert.That(version1.CompareTo(version4)).IsEqualTo(-1);
        await Assert.That(version4.CompareTo(version1)).IsEqualTo(1);
    }

    [Test]
    public async Task Equals_ShouldReturnCorrectEqualityResult()
    {
        var version1 = new SpecVersion(1, 2, 3, "metadata");
        var version2 = new SpecVersion(1, 2, 3, "metadata");
        var version3 = new SpecVersion(1, 2, 4, "metadata");
        var version4 = new SpecVersion(1, 2, 3, "other");

        await Assert.That(version1.Equals(version2)).IsTrue();
        await Assert.That(version1.Equals(version3)).IsFalse();
        await Assert.That(version1.Equals(version4)).IsFalse();
    }

    [Test]
    public async Task GetHashCode_ShouldReturnSameHashCodeForEqualObjects()
    {
        var version1 = new SpecVersion(1, 2, 3, "metadata");
        var version2 = new SpecVersion(1, 2, 3, "metadata");

        await Assert.That(version1.GetHashCode()).IsEqualTo(version2.GetHashCode());
    }

    [Test]
    public async Task SortListOfStringsByVersionComparer()
    {
        var versions = new List<string> { "r0.2.3", "v2.1", "v1.2-alpha", "r0.2.4", "v1.3", "v1.2", "v1.2-beta" };
        var parsedVersions = versions.Select(v => SpecVersion.TryParse(v, out var version) ? version : null).ToList();
        var sortedVersions = parsedVersions.OrderBy(v => v, SpecVersion.Comparer.Instance).Select(v => v!.VersionString).ToList();
        await Assert.That(sortedVersions).IsEquivalentTo(new List<string> { "r0.2.3", "r0.2.4", "v1.2-alpha", "v1.2-beta", "v1.2", "v1.3", "v2.1" });
    }
}
