using System.Net;
using System.Text.Json;
using MessengerService.Application.Media;
using MessengerService.Configuration;
using MessengerService.Infrastructure.Http;
using MessengerService.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MessengerService.Tests;

public sealed class MediaLifecycleOutboxTests
{
    [Fact]
    public void Create_KeepsOnlyManagedDistinctMediaUrls()
    {
        var occurredAt = DateTimeOffset.Parse("2026-07-16T00:00:00Z");

        var result = MediaLifecycleOutbox.Create(
            MediaLifecycleEventKinds.Finalize,
            [
                "https://fakebook.example/media/files/a.jpg",
                "https://fakebook.example/media/files/a.jpg",
                "/media/files/b.png",
                "https://cdn.example/external.jpg"
            ],
            occurredAt,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            42);

        Assert.NotNull(result);
        Assert.Equal(MediaLifecycleOutbox.Topic, result.Topic);
        Assert.Equal(MediaLifecycleEventKinds.Finalize, result.Kind);
        Assert.Equal(occurredAt, result.CreatedAt);
        Assert.Equal(
            ["https://fakebook.example/media/files/a.jpg", "/media/files/b.png"],
            MediaLifecycleOutbox.Deserialize(result.PayloadJson).Urls);
    }

    [Fact]
    public void Create_ExternalUrlsOnly_DoesNotCreateEvent()
    {
        Assert.Null(MediaLifecycleOutbox.Create(
            MediaLifecycleEventKinds.Delete,
            ["https://cdn.example/external.jpg"],
            DateTimeOffset.UtcNow));
    }
}

public sealed class UploadMediaClientTests
{
    private const string Secret = "upload-internal-secret-0123456789ab";

    [Theory]
    [InlineData(true, "/internal/media/finalize")]
    [InlineData(false, "/internal/media/delete")]
    public async Task LifecycleRequest_SendsTrustedContract(bool finalize, string expectedPath)
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "{}");
        var client = CreateClient(handler);
        var urls = new[] { "https://fakebook.example/media/files/a.jpg" };

        if (finalize)
        {
            await client.FinalizeAsync(urls, CancellationToken.None);
        }
        else
        {
            await client.DeleteAsync(urls, CancellationToken.None);
        }

        Assert.Equal(expectedPath, handler.LastRequestUri!.AbsolutePath);
        Assert.Equal(Secret, Assert.Single(handler.LastHeaders[MessagingHeaders.UploadServiceSecret]));
        Assert.Equal("media-correlation", Assert.Single(handler.LastHeaders[MessagingHeaders.CorrelationId]));
        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(urls, body.RootElement.GetProperty("urls").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public async Task LifecycleRequest_NonSuccess_IsRetriableFailure()
    {
        var client = CreateClient(new StubHttpMessageHandler(HttpStatusCode.ServiceUnavailable, "{}"));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.FinalizeAsync(
            ["/media/files/a.jpg"],
            CancellationToken.None));
    }

    private static UploadMediaClient CreateClient(StubHttpMessageHandler handler)
    {
        var context = new DefaultHttpContext { TraceIdentifier = "media-correlation" };
        return new UploadMediaClient(
            new HttpClient(handler),
            Options.Create(new InternalServicesOptions
            {
                TimeoutSeconds = 1,
                Upload = new UploadOptions
                {
                    BaseUrl = "https://upload.example/base/",
                    SharedSecret = Secret
                }
            }),
            new HttpContextAccessor { HttpContext = context },
            NullLogger<UploadMediaClient>.Instance);
    }
}
