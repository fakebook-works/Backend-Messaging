using MessengerService.Application;

namespace MessengerService.Tests;

public sealed class AttachmentUrlPolicyTests
{
    [Theory]
    [InlineData("/media/files/abc123.png", true)]
    [InlineData("/media/files/abc%20123.png", true)]
    [InlineData("/media/files/../secret", false)]
    [InlineData("/media/files/a/b.png", false)]
    [InlineData("/media/files/a.png?token=x", false)]
    [InlineData("http://media.example/a.png", false)]
    [InlineData("https://other.example/a.png", false)]
    [InlineData("https://media.example/a.png", true)]
    public void IsAllowed_EnforcesManagedRelativeOrAllowlistedHttps(string value, bool expected)
    {
        Assert.Equal(expected, AttachmentUrlPolicy.IsAllowed(value, 2048, ["media.example"]));
    }
}
