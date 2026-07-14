using System.Net;
using System.Text.Json;
using MessengerService.Application;
using MessengerService.Application.Abstractions;
using MessengerService.Configuration;
using MessengerService.Infrastructure.Http;
using MessengerService.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MessengerService.Tests;

public sealed class SocialGraphPermissionClientTests
{
    private const string StrongSecret = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task CheckAsync_SendsTrustedContractAndReturnsDecisions()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            """
            {
              "results": [
                {
                  "targetUserId": 42,
                  "allowed": true,
                  "isFriend": true,
                  "blockedEitherDirection": false,
                  "reason": null
                },
                {
                  "targetUserId": 43,
                  "allowed": false,
                  "isFriend": false,
                  "blockedEitherDirection": true,
                  "reason": "BLOCKED"
                }
              ]
            }
            """);
        var client = CreateClient(handler, "https://social.example/base/");

        var result = await client.CheckAsync(
            7,
            [42, 43, 42],
            SocialGraphPermissionAction.CreateDirect,
            CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal(
            "https://social.example/internal/messaging/permissions/check",
            handler.LastRequestUri!.AbsoluteUri);
        Assert.Equal(
            StrongSecret,
            Assert.Single(handler.LastHeaders[MessagingHeaders.InternalServiceSecret]));
        Assert.Equal(
            "correlation-test",
            Assert.Single(handler.LastHeaders[MessagingHeaders.CorrelationId]));

        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(7, body.RootElement.GetProperty("actorUserId").GetInt64());
        Assert.Equal(
            "CREATE_DIRECT",
            body.RootElement.GetProperty("action").GetString());
        Assert.Equal(
            [42L, 43L],
            body.RootElement.GetProperty("targetUserIds")
                .EnumerateArray()
                .Select(element => element.GetInt64())
                .ToArray());

        Assert.Collection(
            result.Decisions,
            allowed =>
            {
                Assert.Equal(42, allowed.TargetUserId);
                Assert.True(allowed.Allowed);
            },
            denied =>
            {
                Assert.Equal(43, denied.TargetUserId);
                Assert.False(denied.Allowed);
                Assert.Equal("BLOCKED", denied.Reason);
            });
    }

    [Theory]
    [InlineData("{\"results\":[]}")]
    [InlineData("{\"results\":[{\"targetUserId\":42,\"allowed\":true,\"isFriend\":false,\"blockedEitherDirection\":false}]}")]
    [InlineData("{\"results\":[{\"targetUserId\":42,\"allowed\":true,\"isFriend\":true,\"blockedEitherDirection\":true}]}")]
    [InlineData("not-json")]
    public async Task CheckAsync_InvalidOrUnsafeResponse_FailsClosed(string responseJson)
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, responseJson);
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<MessagingApplicationException>(() =>
            client.CheckAsync(
                7,
                [42],
                SocialGraphPermissionAction.SendDirect,
                CancellationToken.None));

        Assert.Equal(MessagingErrorCodes.SocialGraphUnavailable, exception.Code);
    }

    [Fact]
    public async Task CheckAsync_NonSuccessStatus_FailsClosed()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.ServiceUnavailable,
            "{}");
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<MessagingApplicationException>(() =>
            client.CheckAsync(
                7,
                [42],
                SocialGraphPermissionAction.AddGroupMembers,
                CancellationToken.None));

        Assert.Equal(MessagingErrorCodes.SocialGraphUnavailable, exception.Code);
    }

    [Fact]
    public async Task CheckAsync_UnsafeConfiguration_FailsBeforeSending()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "{}");
        var client = CreateClient(
            handler,
            baseUrl: "file:///social",
            secret: "weak");

        var exception = await Assert.ThrowsAsync<MessagingApplicationException>(() =>
            client.CheckAsync(
                7,
                [42],
                SocialGraphPermissionAction.SendDirect,
                CancellationToken.None));

        Assert.Equal(MessagingErrorCodes.SocialGraphUnavailable, exception.Code);
        Assert.Null(handler.LastMethod);
    }

    [Fact]
    public async Task CheckAsync_RejectsInvalidTargetIdsWithoutSending()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "{}");
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.CheckAsync(
                7,
                [42, 0],
                SocialGraphPermissionAction.SendDirect,
                CancellationToken.None));

        Assert.Null(handler.LastMethod);
    }

    private static SocialGraphPermissionClient CreateClient(
        StubHttpMessageHandler handler,
        string baseUrl = "https://social.example",
        string secret = StrongSecret)
    {
        var context = new DefaultHttpContext
        {
            TraceIdentifier = "correlation-test"
        };

        return new SocialGraphPermissionClient(
            new HttpClient(handler),
            Options.Create(new InternalServicesOptions
            {
                MessengerSharedSecret = secret,
                TimeoutSeconds = 1,
                SocialGraph = new SocialGraphOptions { BaseUrl = baseUrl }
            }),
            new HttpContextAccessor { HttpContext = context },
            NullLogger<SocialGraphPermissionClient>.Instance);
    }
}
