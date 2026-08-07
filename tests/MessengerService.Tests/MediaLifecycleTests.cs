using System.Net;
using System.Text.Json;
using MessengerService.Application.Abstractions;
using MessengerService.Application.Media;
using MessengerService.Configuration;
using MessengerService.Domain.Entities;
using MessengerService.Infrastructure.Http;
using MessengerService.Infrastructure.Realtime;
using MessengerService.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MessengerService.Tests;

public sealed class MediaLifecycleOutboxTests
{
    [Fact]
    public void Create_KeepsManagedDistinctExactReferences()
    {
        var occurredAt = DateTimeOffset.Parse("2026-07-16T00:00:00Z");
        var conversationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var messageId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var result = MediaLifecycleOutbox.Create(
            MediaLifecycleEventKinds.Finalize,
            [
                new UploadMediaReference(
                    "https://fakebook.example/media/files/a.jpg",
                    "messenger:message:22222222222222222222222222222222:attachment:0:content"),
                new UploadMediaReference(
                    "https://fakebook.example/media/files/a.jpg",
                    "messenger:message:22222222222222222222222222222222:attachment:0:content"),
                new UploadMediaReference(
                    "/media/files/b.png",
                    "messenger:message:22222222222222222222222222222222:attachment:0:thumbnail"),
                new UploadMediaReference(
                    "https://cdn.example/external.jpg",
                    "messenger:message:22222222222222222222222222222222:attachment:1:content")
            ],
            occurredAt,
            conversationId,
            messageId,
            42);

        Assert.NotNull(result);
        Assert.Equal(MediaLifecycleOutbox.Topic, result.Topic);
        Assert.Equal(MediaLifecycleEventKinds.Finalize, result.Kind);
        Assert.Equal(occurredAt, result.CreatedAt);
        var payload = MediaLifecycleOutbox.Deserialize(result.PayloadJson);
        Assert.Null(payload.Urls);
        Assert.Equal(2, payload.References!.Count);
        Assert.Contains(payload.References, reference => reference.Url == "/media/files/b.png" &&
            reference.ReferenceId.EndsWith(":thumbnail", StringComparison.Ordinal));
    }

    [Fact]
    public void MessageAttachment_UsesStableDistinctContentAndThumbnailParents()
    {
        var messageId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        var references = MediaLifecycleOutbox.MessageAttachment(
            messageId,
            3,
            "/media/files/video.mp4",
            "/media/files/video-thumb.jpg");

        Assert.Equal(
            [
                "messenger:message:aaaaaaaabbbbccccddddeeeeeeeeeeee:attachment:3:content",
                "messenger:message:aaaaaaaabbbbccccddddeeeeeeeeeeee:attachment:3:thumbnail"
            ],
            references.Select(reference => reference.ReferenceId));
    }

    [Fact]
    public void Deserialize_AcceptsLegacyUrlOnlyPayload()
    {
        var payload = MediaLifecycleOutbox.Deserialize("{\"urls\":[\"/media/files/legacy.jpg\"]}");

        Assert.Equal(["/media/files/legacy.jpg"], payload.Urls);
        Assert.Null(payload.References);
    }

    [Fact]
    public void Deserialize_RejectsPayloadThatMixesLegacyUrlsAndExactReferences()
    {
        Assert.Throws<JsonException>(() => MediaLifecycleOutbox.Deserialize(
            "{\"urls\":[\"/media/files/legacy.jpg\"],\"references\":[{\"url\":\"/media/files/exact.jpg\",\"referenceId\":\"messenger:message:abc:attachment:0:content\"}]}"));
    }

    [Fact]
    public void Create_ExternalUrlsOnly_DoesNotCreateEvent()
    {
        Assert.Null(MediaLifecycleOutbox.Create(
            MediaLifecycleEventKinds.Delete,
            [new UploadMediaReference("https://cdn.example/external.jpg", "messenger:message:a:content")],
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Dispatch_StaleFinalize_DoesNotRenewAuthorizationBeforeTombstoneAwareAttach()
    {
        var operationAt = DateTimeOffset.Parse("2026-08-07T10:00:00Z");
        var outboxEvent = MediaLifecycleOutbox.Create(
            MediaLifecycleEventKinds.Finalize,
            [new UploadMediaReference(
                "/media/files/a.jpg",
                "messenger:message:abc:attachment:0:content")],
            operationAt,
            actorUserId: 42)!;
        var upload = new DispatchRecordingUploadClient();

        await MediaLifecycleOutbox.DispatchAsync(
            outboxEvent,
            upload,
            CancellationToken.None);

        Assert.Equal(0, upload.AuthorizationCalls);
        var attach = Assert.Single(upload.FinalizeCalls);
        Assert.Equal(42, attach.OwnerUserId);
        Assert.Equal(operationAt, attach.OperationAt);
        Assert.Equal(1, attach.ReferenceCount);
    }

    [Fact]
    public async Task Dispatch_RetriedFinalize_RenewsWithOriginalOperationTimeThenStillFinalizes()
    {
        var operationAt = DateTimeOffset.Parse("2026-08-07T10:00:00Z");
        var outboxEvent = MediaLifecycleOutbox.Create(
            MediaLifecycleEventKinds.Finalize,
            [new UploadMediaReference(
                "/media/files/a.jpg",
                "messenger:message:abc:attachment:0:content")],
            operationAt,
            actorUserId: 42)!;
        outboxEvent.AttemptCount = 1;
        var upload = new DispatchRecordingUploadClient { Authorized = false };

        await MediaLifecycleOutbox.DispatchAsync(
            outboxEvent,
            upload,
            CancellationToken.None);

        Assert.Equal(1, upload.AuthorizationCalls);
        var authorization = Assert.Single(upload.ReferenceAuthorizationCalls);
        Assert.Equal(42, authorization.OwnerUserId);
        Assert.Equal(operationAt, authorization.OperationAt);
        Assert.Single(upload.FinalizeCalls);
    }

    [Fact]
    public async Task Dispatch_LegacyOwnerlessRepairPayload_FailsClosed()
    {
        var operationAt = DateTimeOffset.Parse("2026-08-07T10:00:00Z");
        var outboxEvent = new OutboxEvent
        {
            Id = Guid.NewGuid(),
            Topic = MediaLifecycleOutbox.Topic,
            Kind = MediaLifecycleEventKinds.Finalize,
            PayloadJson = "{\"references\":[{\"url\":\"/media/files/avatar.avif\",\"referenceId\":\"messenger:conversation:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:avatar\"}],\"repair\":true}",
            OccurredAt = operationAt,
            CreatedAt = operationAt
        };
        var upload = new DispatchRecordingUploadClient();

        await Assert.ThrowsAsync<JsonException>(() =>
            MediaLifecycleOutbox.DispatchAsync(outboxEvent, upload, CancellationToken.None));
        Assert.Empty(upload.FinalizeCalls);
    }

    [Fact]
    public async Task Dispatch_OwnerlessAttachWithoutRepairFlag_FailsClosed()
    {
        var outboxEvent = MediaLifecycleOutbox.Create(
            MediaLifecycleEventKinds.Finalize,
            [new UploadMediaReference(
                "/media/files/avatar.avif",
                "messenger:conversation:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:avatar")],
            DateTimeOffset.UtcNow)!;

        await Assert.ThrowsAsync<JsonException>(() => MediaLifecycleOutbox.DispatchAsync(
            outboxEvent,
            new DispatchRecordingUploadClient(),
            CancellationToken.None));
    }

    [Theory]
    [InlineData(MediaLifecycleEventKinds.Finalize)]
    [InlineData(MediaLifecycleEventKinds.Delete)]
    public void DurableMediaFailurePolicy_NeverRetiresOrPurgesUnprocessedLifecycleRows(string kind)
    {
        Assert.Equal(
            OutboxRetryPolicy.MaxDurableAttemptCount,
            OutboxRetryPolicy.NextAttemptCount(int.MaxValue, durable: true));
        var oldUnprocessed = new OutboxEvent
        {
            Id = Guid.NewGuid(),
            Topic = MediaLifecycleOutbox.Topic,
            Kind = kind,
            PayloadJson = "{}",
            OccurredAt = DateTimeOffset.UtcNow.AddDays(-90),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-90),
            AttemptCount = int.MaxValue,
            LastError = "retired by an older deployment"
        };

        Assert.False(OutboxRetryPolicy
            .ExpiredDeadLetterPredicate(DateTimeOffset.UtcNow.AddDays(-30))
            .Compile()(oldUnprocessed));
    }

    private sealed class DispatchRecordingUploadClient : IUploadMediaClient
    {
        public bool Authorized { get; init; } = true;
        public int AuthorizationCalls { get; private set; }
        public List<(long OwnerUserId, DateTimeOffset OperationAt)> ReferenceAuthorizationCalls { get; } = [];
        public List<(long? OwnerUserId, DateTimeOffset OperationAt, int ReferenceCount)> FinalizeCalls { get; } = [];

        public Task<UploadMediaAuthorizationResult> AuthorizeOwnedAsync(
            IReadOnlyCollection<string> urls,
            long ownerUserId,
            CancellationToken cancellationToken)
        {
            AuthorizationCalls++;
            return Task.FromResult(new UploadMediaAuthorizationResult(
                Authorized,
                Authorized
                    ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    : urls.ToHashSet(StringComparer.OrdinalIgnoreCase)));
        }

        public Task<UploadMediaAuthorizationResult> AuthorizeReferencesAsync(
            IReadOnlyCollection<UploadMediaReference> references,
            long ownerUserId,
            DateTimeOffset operationAt,
            CancellationToken cancellationToken)
        {
            AuthorizationCalls++;
            ReferenceAuthorizationCalls.Add((ownerUserId, operationAt));
            return Task.FromResult(new UploadMediaAuthorizationResult(
                Authorized,
                Authorized
                    ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    : references.Select(reference => reference.Url)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase)));
        }

        public Task FinalizeReferencesAsync(
            IReadOnlyCollection<UploadMediaReference> references,
            long? ownerUserId,
            DateTimeOffset operationAt,
            CancellationToken cancellationToken)
        {
            FinalizeCalls.Add((ownerUserId, operationAt, references.Count));
            return Task.CompletedTask;
        }

        public Task DeleteReferencesAsync(
            IReadOnlyCollection<UploadMediaReference> references,
            long? ownerUserId,
            DateTimeOffset operationAt,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task FinalizeAsync(
            IReadOnlyCollection<string> urls,
            long? ownerUserId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAsync(
            IReadOnlyCollection<string> urls,
            long? ownerUserId,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

public sealed class UploadMediaClientTests
{
    private const string Secret = "upload-internal-secret-0123456789ab";

    [Theory]
    [InlineData(true, "/internal/media/finalize")]
    [InlineData(false, "/internal/media/delete")]
    public async Task LegacyLifecycleRequest_SendsTrustedContract(bool finalize, string expectedPath)
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            finalize ? "{\"finalized\":1}" : "{\"scheduled\":1}");
        var client = CreateClient(handler);
        var urls = new[] { "https://fakebook.example/media/files/a.jpg" };

        if (finalize)
        {
            await client.FinalizeAsync(urls, 42, CancellationToken.None);
        }
        else
        {
            await client.DeleteAsync(urls, 42, CancellationToken.None);
        }

        Assert.Equal(expectedPath, handler.LastRequestUri!.AbsolutePath);
        Assert.Equal(Secret, Assert.Single(handler.LastHeaders[MessagingHeaders.UploadServiceSecret]));
        Assert.Equal("media-correlation", Assert.Single(handler.LastHeaders[MessagingHeaders.CorrelationId]));
        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(urls, body.RootElement.GetProperty("urls").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(42, body.RootElement.GetProperty("ownerUserId").GetInt64());
    }

    [Theory]
    [InlineData(true, "/internal/media/finalize", "finalized")]
    [InlineData(false, "/internal/media/delete", "detached")]
    public async Task ReferenceLifecycleRequest_SendsExactParentsAndOperationTime(
        bool finalize,
        string expectedPath,
        string acknowledgement)
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, $"{{\"{acknowledgement}\":2,\"stale\":0}}");
        var client = CreateClient(handler);
        var operationAt = DateTimeOffset.Parse("2026-08-07T12:34:56Z");
        UploadMediaReference[] references =
        [
            new("/media/files/a.mp4", "messenger:message:abc:attachment:0:content"),
            new("/media/files/a-thumb.jpg", "messenger:message:abc:attachment:0:thumbnail")
        ];

        if (finalize)
        {
            await client.FinalizeReferencesAsync(references, 42, operationAt, CancellationToken.None);
        }
        else
        {
            await client.DeleteReferencesAsync(references, 42, operationAt, CancellationToken.None);
        }

        Assert.Equal(expectedPath, handler.LastRequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(2, body.RootElement.GetProperty("references").GetArrayLength());
        Assert.Equal(
            references[1].ReferenceId,
            body.RootElement.GetProperty("references")[1].GetProperty("referenceId").GetString());
        Assert.Equal(operationAt, body.RootElement.GetProperty("operationAt").GetDateTimeOffset());
        Assert.Equal(42, body.RootElement.GetProperty("ownerUserId").GetInt64());
    }

    [Fact]
    public async Task AuthorizeOwnedAsync_RequiresExplicitCompleteAuthorization()
    {
        var successHandler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            "{\"authorized\":true,\"unauthorizedUrls\":[]}");
        var successClient = CreateClient(successHandler);

        var authorized = await successClient.AuthorizeOwnedAsync(
            ["/media/files/a.jpg", "/media/files/a-thumb.jpg"],
            42,
            CancellationToken.None);

        Assert.True(authorized.Authorized);
        Assert.Equal("/internal/media/authorize", successHandler.LastRequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(successHandler.LastBody!);
        Assert.Equal(2, body.RootElement.GetProperty("urls").GetArrayLength());
        Assert.Equal(42, body.RootElement.GetProperty("ownerUserId").GetInt64());

        var deniedClient = CreateClient(new StubHttpMessageHandler(
            HttpStatusCode.OK,
            "{\"authorized\":false,\"unauthorizedUrls\":[\"/media/files/a.jpg\"]}"));
        var denied = await deniedClient.AuthorizeOwnedAsync(
            ["/media/files/a.jpg"],
            42,
            CancellationToken.None);
        Assert.False(denied.Authorized);
        Assert.Contains("/media/files/a.jpg", denied.UnauthorizedUrls);

        var incompleteClient = CreateClient(new StubHttpMessageHandler(
            HttpStatusCode.OK,
            "{\"authorized\":true}"));
        await Assert.ThrowsAsync<HttpRequestException>(() => incompleteClient.AuthorizeOwnedAsync(
            ["/media/files/a.jpg"],
            42,
            CancellationToken.None));
    }

    [Fact]
    public async Task AuthorizeReferencesAsync_SendsExactParentAndOperationTime()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            "{\"authorized\":true,\"unauthorizedUrls\":[],\"exactReferences\":true,\"lifecycleVersion\":3,\"referenceCount\":1}");
        var client = CreateClient(handler);
        var operationAt = DateTimeOffset.Parse("2026-08-07T12:34:56Z");
        var reference = new UploadMediaReference(
            "/media/files/a.avif",
            "messenger:message:abc:attachment:0:content");

        var result = await client.AuthorizeReferencesAsync(
            [reference],
            42,
            operationAt,
            CancellationToken.None);

        Assert.True(result.Authorized);
        Assert.Equal("/internal/media/authorize", handler.LastRequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.True(
            !body.RootElement.TryGetProperty("urls", out var urlsElement) ||
            urlsElement.ValueKind == JsonValueKind.Null);
        Assert.Equal(42, body.RootElement.GetProperty("ownerUserId").GetInt64());
        Assert.Equal(operationAt, body.RootElement.GetProperty("operationAt").GetDateTimeOffset());
        Assert.Equal(reference.ReferenceId,
            body.RootElement.GetProperty("references")[0].GetProperty("referenceId").GetString());
    }

    [Fact]
    public async Task AuthorizeReferencesAsync_OldUploadResponse_FailsClosed()
    {
        var client = CreateClient(new StubHttpMessageHandler(
            HttpStatusCode.OK,
            "{\"authorized\":true,\"unauthorizedUrls\":[]}"));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.AuthorizeReferencesAsync(
            [new UploadMediaReference(
                "/media/files/a.avif",
                "messenger:message:abc:attachment:0:content")],
            42,
            DateTimeOffset.UtcNow,
            CancellationToken.None));
    }

    [Fact]
    public async Task ReferenceLifecycleRequest_IncompleteAcknowledgement_IsRetriableFailure()
    {
        var client = CreateClient(new StubHttpMessageHandler(HttpStatusCode.OK, "{\"finalized\":0}"));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.FinalizeReferencesAsync(
            [new UploadMediaReference("/media/files/a.jpg", "messenger:message:abc:attachment:0:content")],
            42,
            DateTimeOffset.UtcNow,
            CancellationToken.None));
    }

    [Fact]
    public async Task LifecycleRequest_NonSuccess_IsRetriableFailure()
    {
        var client = CreateClient(new StubHttpMessageHandler(HttpStatusCode.ServiceUnavailable, "{}"));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.FinalizeAsync(
            ["/media/files/a.jpg"],
            42,
            CancellationToken.None));
    }

    [Fact]
    public async Task LifecycleRequest_BadContract_IsClassifiedPermanent()
    {
        var client = CreateClient(new StubHttpMessageHandler(HttpStatusCode.BadRequest, "{}"));

        await Assert.ThrowsAsync<UploadMediaPermanentException>(() => client.FinalizeReferencesAsync(
            [new UploadMediaReference("/media/files/a.jpg", "messenger:message:abc:attachment:0:content")],
            42,
            DateTimeOffset.UtcNow,
            CancellationToken.None));
    }

    [Fact]
    public async Task ExactLifecycleRequest_OwnerConflict_IsClassifiedPermanent()
    {
        var client = CreateClient(new StubHttpMessageHandler(HttpStatusCode.Conflict, "{}"));

        await Assert.ThrowsAsync<UploadMediaPermanentException>(() => client.FinalizeReferencesAsync(
            [new UploadMediaReference("/media/files/a.jpg", "messenger:message:abc:attachment:0:content")],
            42,
            DateTimeOffset.UtcNow,
            CancellationToken.None));
    }

    [Fact]
    public async Task ExactLifecycleRequest_FutureClockSkew_RemainsRetriable()
    {
        var client = CreateClient(new StubHttpMessageHandler((HttpStatusCode)425, "{}"));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.FinalizeReferencesAsync(
            [new UploadMediaReference("/media/files/a.jpg", "messenger:message:abc:attachment:0:content")],
            42,
            DateTimeOffset.UtcNow,
            CancellationToken.None));
    }

    [Fact]
    public async Task LegacyDelete_IncompleteScheduledCount_IsRetriableFailure()
    {
        var client = CreateClient(new StubHttpMessageHandler(HttpStatusCode.OK, "{\"scheduled\":0}"));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.DeleteAsync(
            ["/media/files/a.jpg"],
            42,
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
