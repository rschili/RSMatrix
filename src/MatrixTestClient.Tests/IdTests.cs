using TUnit.Core;

namespace MatrixTextClient.Tests;

public class IdTests
{
    [Test]
    [Arguments("@user:server", true, "user", "server")]
    [Arguments("@user:server:extra", true, "user", "server:extra")]
    [Arguments("@user", false, "", "")]
    [Arguments("user:server", false, "", "")]
    [Arguments("", false, "", "")]
    [Arguments("user", false, "", "")]
    [Arguments("user:", false, "", "")]
    [Arguments(":server", false, "", "")]
    [Arguments("!user:server", false, "", "")]
    public async Task TryParse(string input, bool expectedResult, string expectedUser, string expectedServer)
    {
        var result = UserId.TryParse(input, out var userId);
        await Assert.That(result).IsEqualTo(expectedResult);
        if(expectedResult)
        {
            await Assert.That(userId).IsNotEqualTo(null);
            await Assert.That(userId!.Name).IsEqualTo(expectedUser);
            await Assert.That(userId!.Server).IsEqualTo(expectedServer);
            await Assert.That(userId!.FullId).IsEqualTo(input);
        }
    }

}