using System.Text.Json;
using MessengerService.Application.Abstractions;
using MessengerService.Configuration;
using MessengerService.Contracts.Internal;
using MessengerService.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MessengerService.Tests;

public sealed class FixedTimeSecretComparerTests
{
    [Fact]
    public void IsStrongEnough_CountsUtf8Bytes()
    {
        Assert.False(FixedTimeSecretComparer.IsStrongEnough(new string('a', 31)));
        Assert.True(FixedTimeSecretComparer.IsStrongEnough(new string('é', 16)));
    }

    [Fact]
    public void Matches_RequiresExactValueAndStrongConfiguredSecret()
    {
        var configured = new string('s', FixedTimeSecretComparer.MinimumSecretBytes);

        Assert.True(FixedTimeSecretComparer.Matches(configured, configured));
        Assert.False(FixedTimeSecretComparer.Matches(configured + "x", configured));
        Assert.False(FixedTimeSecretComparer.Matches(null, configured));
        Assert.False(FixedTimeSecretComparer.Matches("short", "short"));
    }
}

public sealed class SecurityOptionsValidatorTests
{
    private const string StrongSecret = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void GatewayValidator_RejectsWeakSecret()
    {
        var result = new GatewayOptionsValidator().Validate(
            null,
            new GatewayOptions { InternalSharedSecret = "too-short" });

        Assert.True(result.Failed);
        Assert.Contains("at least 32 UTF-8 bytes", Assert.Single(result.Failures));
    }

    [Fact]
    public void InternalServicesValidator_AcceptsSafeConfiguration()
    {
        var result = new InternalServicesOptionsValidator().Validate(
            null,
            new InternalServicesOptions
            {
                MessengerSharedSecret = StrongSecret,
                TimeoutSeconds = 3,
                SocialGraph = new SocialGraphOptions
                {
                    BaseUrl = "https://socialgraph:5012",
                    SharedSecret = StrongSecret
                },
                Upload = new UploadOptions
                {
                    BaseUrl = "https://upload:4001",
                    SharedSecret = StrongSecret
                }
            });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void InternalServicesValidator_ReportsAllUnsafeSettings()
    {
        var result = new InternalServicesOptionsValidator().Validate(
            null,
            new InternalServicesOptions
            {
                MessengerSharedSecret = "weak",
                TimeoutSeconds = 0,
                SocialGraph = new SocialGraphOptions
                {
                    BaseUrl = "file:///tmp/social",
                    SharedSecret = "weak"
                },
                Upload = new UploadOptions
                {
                    BaseUrl = "file:///tmp/upload",
                    SharedSecret = "weak"
                }
            });

        Assert.True(result.Failed);
        Assert.Equal(6, result.Failures.Count());
    }
}

public sealed class GatewayTrustMiddlewareTests
{
    private const string StrongSecret = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task ValidTrustedHeaders_EstablishUserAndContinuePipeline()
    {
        var nextWasCalled = false;
        var middleware = CreateGatewayMiddleware(_ =>
        {
            nextWasCalled = true;
            return Task.CompletedTask;
        });
        var users = new FakeProvisioningService { IsActive = true };
        var context = NewContext("/graphql");
        context.Request.Headers[MessagingHeaders.GatewaySecret] = StrongSecret;
        context.Request.Headers[MessagingHeaders.UserId] = "9007199254740991";

        await middleware.InvokeAsync(context, users);

        Assert.True(nextWasCalled);
        Assert.Equal([9007199254740991L], users.ActiveChecks);
        Assert.Empty(users.ProvisionedUserIds);
        var accessor = new TrustedUserContextAccessor(
            new HttpContextAccessor { HttpContext = context });
        Assert.Equal(9007199254740991L, accessor.RequireUserId());
    }

    [Fact]
    public async Task DuplicateTrustedUserHeader_IsRejectedBeforeUserLookup()
    {
        var nextWasCalled = false;
        var middleware = CreateGatewayMiddleware(_ =>
        {
            nextWasCalled = true;
            return Task.CompletedTask;
        });
        var users = new FakeProvisioningService();
        var context = NewContext("/graphql");
        context.Request.Headers[MessagingHeaders.GatewaySecret] = StrongSecret;
        context.Request.Headers.Append(MessagingHeaders.UserId, "41");
        context.Request.Headers.Append(MessagingHeaders.UserId, "42");

        await middleware.InvokeAsync(context, users);

        Assert.False(nextWasCalled);
        Assert.Empty(users.ActiveChecks);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal("INVALID_TRUSTED_USER", await ReadProblemCodeAsync(context));
    }

    [Fact]
    public async Task MissingProjection_IsRepairedForAuthenticatedGatewayUser()
    {
        var nextWasCalled = false;
        var middleware = CreateGatewayMiddleware(_ =>
        {
            nextWasCalled = true;
            return Task.CompletedTask;
        });
        var users = new FakeProvisioningService { IsActive = false };
        var context = NewContext("/graphql");
        context.Request.Headers[MessagingHeaders.GatewaySecret] = StrongSecret;
        context.Request.Headers[MessagingHeaders.UserId] = "42";

        await middleware.InvokeAsync(context, users);

        Assert.True(nextWasCalled);
        Assert.Equal([42L], users.ProvisionedUserIds);
    }

    [Fact]
    public async Task DeletedProjection_IsNotRevived()
    {
        var middleware = CreateGatewayMiddleware(_ => Task.CompletedTask);
        var users = new FakeProvisioningService
        {
            IsActive = false,
            ProvisionOutcome = ProvisionUserOutcome.DeletedTombstone
        };
        var context = NewContext("/graphql");
        context.Request.Headers[MessagingHeaders.GatewaySecret] = StrongSecret;
        context.Request.Headers[MessagingHeaders.UserId] = "42";

        await middleware.InvokeAsync(context, users);

        Assert.Equal([42L], users.ProvisionedUserIds);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal("MESSAGING_USER_NOT_ACTIVE", await ReadProblemCodeAsync(context));
    }

    [Fact]
    public async Task UserStoreFailure_FailsClosed()
    {
        var middleware = CreateGatewayMiddleware(_ => Task.CompletedTask);
        var users = new FakeProvisioningService
        {
            IsActiveException = new InvalidOperationException("database offline")
        };
        var context = NewContext("/graphql");
        context.Request.Headers[MessagingHeaders.GatewaySecret] = StrongSecret;
        context.Request.Headers[MessagingHeaders.UserId] = "42";

        await middleware.InvokeAsync(context, users);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("USER_STATE_UNAVAILABLE", await ReadProblemCodeAsync(context));
    }

    private static GatewayTrustMiddleware CreateGatewayMiddleware(RequestDelegate next) =>
        new(
            next,
            Options.Create(new GatewayOptions { InternalSharedSecret = StrongSecret }),
            NullLogger<GatewayTrustMiddleware>.Instance);

    private static DefaultHttpContext NewContext(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string?> ReadProblemCodeAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.GetProperty("code").GetString();
    }
}

public sealed class InternalApiAuthenticationMiddlewareTests
{
    private const string StrongSecret = "abcdef0123456789abcdef0123456789";

    [Fact]
    public async Task CorrectSecret_AllowsInternalRequest()
    {
        var nextWasCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextWasCalled = true;
            return Task.CompletedTask;
        });
        var context = NewContext("/internal/users");
        context.Request.Headers[MessagingHeaders.InternalServiceSecret] = StrongSecret;

        await middleware.InvokeAsync(context);

        Assert.True(nextWasCalled);
    }

    [Fact]
    public async Task MissingSecret_RejectsInternalRequest()
    {
        var nextWasCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextWasCalled = true;
            return Task.CompletedTask;
        });
        var context = NewContext("/internal/users");

        await middleware.InvokeAsync(context);

        Assert.False(nextWasCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task NonInternalPath_BypassesAuthentication()
    {
        var nextWasCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextWasCalled = true;
            return Task.CompletedTask;
        });
        var context = NewContext("/health");

        await middleware.InvokeAsync(context);

        Assert.True(nextWasCalled);
    }

    private static InternalApiAuthenticationMiddleware CreateMiddleware(RequestDelegate next) =>
        new(
            next,
            Options.Create(new InternalServicesOptions
            {
                MessengerSharedSecret = StrongSecret
            }),
            NullLogger<InternalApiAuthenticationMiddleware>.Instance);

    private static DefaultHttpContext NewContext(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }
}
