using System.Runtime.Versioning;
using CigerTool.Infrastructure.Environment;
using Xunit;

namespace CigerTool.Infrastructure.Tests.Basics;

[SupportedOSPlatform("windows")]
public sealed class DesktopEnvironmentProfileServiceTests
{
    [Fact]
    public void GetCurrentProfile_ShouldReturnProfile()
    {
        var service = new DesktopEnvironmentProfileService();

        var profile = service.GetCurrentProfile();

        Assert.False(string.IsNullOrWhiteSpace(profile.ProfileName));
    }
}
