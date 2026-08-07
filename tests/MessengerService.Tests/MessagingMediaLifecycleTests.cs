using MessengerService.Application;
using MessengerService.Application.Abstractions;
using MessengerService.Application.Media;
using MessengerService.Application.Models;
using MessengerService.Domain.Entities;
using MessengerService.Domain.Enums;
using MessengerService.Infrastructure.Persistence;
using MessengerService.Infrastructure.Realtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace MessengerService.Tests;

public sealed class MessagingMediaLifecycleTests
{
    [Fact]
    public async Task SendMessage_RejectsForeignManagedMediaBeforePersistingAnything()
    {
        await using var db = NewDb();
        var conversation = SeedGroup(db);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var upload = new RecordingUploadMediaClient { Authorized = false };

        var error = await Assert.ThrowsAsync<MessagingApplicationException>(() =>
            NewService(db, upload).SendMessageAsync(
                11,
                MessageCommand(conversation.Id),
                TestContext.Current.CancellationToken));

        Assert.Equal(MessagingErrorCodes.AttachmentUrlNotAllowed, error.Code);
        Assert.Equal(
            ["/media/files/video.mp4"],
            Assert.Single(upload.AuthorizationCalls).Urls);
        Assert.Empty(await db.Messages.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await db.OutboxEvents.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SendAndDeleteMessage_AttachAndDetachTheSameContentAndThumbnailParents()
    {
        await using var db = NewDb();
        var conversation = SeedGroup(db);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var upload = new RecordingUploadMediaClient();
        var service = NewService(db, upload);

        var sent = await service.SendMessageAsync(
            11,
            MessageCommand(conversation.Id),
            TestContext.Current.CancellationToken);
        await service.DeleteMessageAsync(
            11,
            new DeleteMessageCommand(sent.Id),
            TestContext.Current.CancellationToken);

        var events = await db.OutboxEvents.AsNoTracking()
            .Where(item => item.Kind == MediaLifecycleEventKinds.Finalize ||
                           item.Kind == MediaLifecycleEventKinds.Delete)
            .OrderBy(item => item.Kind)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, events.Count);
        var finalized = MediaLifecycleOutbox.Deserialize(
            events.Single(item => item.Kind == MediaLifecycleEventKinds.Finalize).PayloadJson);
        var deleted = MediaLifecycleOutbox.Deserialize(
            events.Single(item => item.Kind == MediaLifecycleEventKinds.Delete).PayloadJson);
        Assert.Equal(
            finalized.References!.Select(reference => (reference.Url, reference.ReferenceId)).OrderBy(value => value.Url),
            deleted.References!.Select(reference => (reference.Url, reference.ReferenceId)).OrderBy(value => value.Url));
        Assert.Contains(deleted.References!, reference =>
            reference.ReferenceId == $"messenger:message:{sent.Id:N}:attachment:0:thumbnail");
        Assert.Empty(await db.MessageAttachments.AsNoTracking()
            .Where(attachment => attachment.MessageId == sent.Id)
            .ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReplacingGroupAvatar_AuthorizesNewOwnerAndMovesTheExactConversationParent()
    {
        await using var db = NewDb();
        var conversation = SeedGroup(db);
        conversation.AvatarUrl = "/media/files/old-avatar.jpg";
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var upload = new RecordingUploadMediaClient();

        await NewService(db, upload).UpdateGroupConversationAsync(
            11,
            new UpdateGroupConversationCommand(
                conversation.Id,
                HasTitle: false,
                Title: null,
                HasAvatarUrl: true,
                AvatarUrl: "/media/files/new-avatar.jpg"),
            TestContext.Current.CancellationToken);

        var authorization = Assert.Single(upload.AuthorizationCalls);
        Assert.Equal(11, authorization.OwnerUserId);
        Assert.Equal(["/media/files/new-avatar.jpg"], authorization.Urls);
        var events = await db.OutboxEvents.AsNoTracking()
            .Where(item => item.Kind == MediaLifecycleEventKinds.Finalize ||
                           item.Kind == MediaLifecycleEventKinds.Delete)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, events.Count);
        var detached = Assert.Single(MediaLifecycleOutbox.Deserialize(
            events.Single(item => item.Kind == MediaLifecycleEventKinds.Delete).PayloadJson).References!);
        var attached = Assert.Single(MediaLifecycleOutbox.Deserialize(
            events.Single(item => item.Kind == MediaLifecycleEventKinds.Finalize).PayloadJson).References!);
        Assert.Equal("/media/files/old-avatar.jpg", detached.Url);
        Assert.Equal("/media/files/new-avatar.jpg", attached.Url);
        Assert.Equal(detached.ReferenceId, attached.ReferenceId);
        Assert.Equal($"messenger:conversation:{conversation.Id:N}:avatar", attached.ReferenceId);
        Assert.Null(events.Single(item => item.Kind == MediaLifecycleEventKinds.Delete).ActorUserId);
        Assert.Equal(11, events.Single(item => item.Kind == MediaLifecycleEventKinds.Finalize).ActorUserId);
    }

    [Fact]
    public async Task ReapplyingStoredGroupAvatar_DoesNotCreateAnOwnerlessAttach()
    {
        await using var db = NewDb();
        var conversation = SeedGroup(db);
        conversation.AvatarUrl = "/media/files/existing-avatar.avif";
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var upload = new RecordingUploadMediaClient { Authorized = false };

        await NewService(db, upload).UpdateGroupConversationAsync(
            11,
            new UpdateGroupConversationCommand(
                conversation.Id,
                HasTitle: false,
                Title: null,
                HasAvatarUrl: true,
                AvatarUrl: conversation.AvatarUrl),
            TestContext.Current.CancellationToken);

        Assert.Empty(upload.AuthorizationCalls);
        Assert.Empty(await db.OutboxEvents.AsNoTracking()
            .Where(item => item.Kind == MediaLifecycleEventKinds.Finalize)
            .ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SendMessage_CanReuseVisibleMediaOwnedByItsCanonicalUploader()
    {
        await using var db = NewDb();
        var conversation = SeedGroup(db);
        var source = new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            SenderUserId = 22,
            Sequence = 1,
            ClientMessageId = Guid.NewGuid(),
            Text = "source",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        conversation.CurrentSequence = 1;
        db.Messages.Add(source);
        db.MessageAttachments.Add(new MessageAttachment
        {
            MessageId = source.Id,
            Ordinal = 0,
            Url = "https://upload.example/media/files/source.jpg"
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var upload = new RecordingUploadMediaClient
        {
            OwnerPredicate = (owner, _) => owner == 22
        };

        var sent = await NewService(db, upload).SendMessageAsync(
            11,
            new SendMessageCommand(
                conversation.Id,
                Guid.NewGuid(),
                "reuse",
                ["/media/files/source.jpg"],
                null),
            TestContext.Current.CancellationToken);

        Assert.NotEqual(source.Id, sent.Id);
        Assert.Contains(upload.AuthorizationCalls, call => call.OwnerUserId == 22);
        Assert.DoesNotContain(upload.AuthorizationCalls, call => call.OwnerUserId == 11);
        var finalize = Assert.Single(await db.OutboxEvents.AsNoTracking()
            .Where(item => item.Kind == MediaLifecycleEventKinds.Finalize)
            .ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(22, finalize.ActorUserId);
        var reference = Assert.Single(MediaLifecycleOutbox.Deserialize(finalize.PayloadJson).References!);
        Assert.Equal($"messenger:message:{sent.Id:N}:attachment:0:content", reference.ReferenceId);
    }

    [Fact]
    public async Task SendMessage_CannotReuseMediaFromBlockedCanonicalSource()
    {
        await using var db = NewDb();
        var conversation = SeedGroup(db);
        var source = new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            SenderUserId = 22,
            Sequence = 1,
            ClientMessageId = Guid.NewGuid(),
            Text = "blocked source",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        conversation.CurrentSequence = 1;
        db.Messages.Add(source);
        db.MessageAttachments.Add(new MessageAttachment
        {
            MessageId = source.Id,
            Ordinal = 0,
            Url = "/media/files/blocked.jpg"
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var upload = new RecordingUploadMediaClient { OwnerPredicate = (_, _) => false };

        var error = await Assert.ThrowsAsync<MessagingApplicationException>(() =>
            NewService(db, upload, blockedUserId: 22).SendMessageAsync(
                11,
                new SendMessageCommand(
                    conversation.Id,
                    Guid.NewGuid(),
                    "reuse",
                    ["/media/files/blocked.jpg"],
                    null),
                TestContext.Current.CancellationToken));

        Assert.Equal(MessagingErrorCodes.AttachmentUrlNotAllowed, error.Code);
        Assert.DoesNotContain(
            await db.Messages.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken),
            message => message.Text == "reuse");
    }

    private static SendMessageCommand MessageCommand(Guid conversationId) =>
        new(
            conversationId,
            Guid.NewGuid(),
            null,
            [],
            null,
            [new MessageAttachmentCommand(
                "/media/files/video.mp4",
                MediaType: "video",
                ThumbnailUrl: "/media/files/video-thumb.jpg")]);

    private static MessagingApplicationService NewService(
        MessagingDbContext db,
        IUploadMediaClient upload,
        long? blockedUserId = null) =>
        new(
            db,
            blockedUserId is { } blocked
                ? new BlockedSocialGraphPermissionClient(blocked)
                : new AllowAllSocialGraphPermissionClient(),
            new FakeProvisioningService(),
            null!,
            new OutboxWakeSignal(),
            TimeProvider.System,
            Options.Create(new MessagingRulesOptions()),
            upload);

    private static MessagingDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<MessagingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new MessagingDbContext(options);
    }

    private static Conversation SeedGroup(MessagingDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            Type = ConversationType.Group,
            Title = "Media lifecycle group",
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Users.AddRange(
            new MessagingUser { UserId = 11, Status = MessagingUserStatus.Active },
            new MessagingUser { UserId = 22, Status = MessagingUserStatus.Active });
        db.Conversations.Add(conversation);
        db.ConversationParticipants.AddRange(
            new ConversationParticipant
            {
                ConversationId = conversation.Id,
                UserId = 11,
                Role = ParticipantRole.Admin,
                JoinedAt = now
            },
            new ConversationParticipant
            {
                ConversationId = conversation.Id,
                UserId = 22,
                Role = ParticipantRole.Member,
                JoinedAt = now
            });
        return conversation;
    }

    private class AllowAllSocialGraphPermissionClient : ISocialGraphPermissionClient
    {
        public virtual Task<SocialGraphPermissionCheckResult> CheckAsync(
            long actorUserId,
            IReadOnlyCollection<long> targetUserIds,
            SocialGraphPermissionAction action,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SocialGraphPermissionCheckResult(
                targetUserIds.Select(userId => new SocialGraphPermissionDecision(
                    userId,
                    Allowed: true,
                    IsFriend: true,
                    BlockedEitherDirection: false,
                    Reason: null)).ToArray()));
    }

    private sealed class BlockedSocialGraphPermissionClient(long blockedUserId)
        : AllowAllSocialGraphPermissionClient
    {
        public override Task<SocialGraphPermissionCheckResult> CheckAsync(
            long actorUserId,
            IReadOnlyCollection<long> targetUserIds,
            SocialGraphPermissionAction action,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SocialGraphPermissionCheckResult(
                targetUserIds.Select(userId => new SocialGraphPermissionDecision(
                    userId,
                    Allowed: true,
                    IsFriend: true,
                    BlockedEitherDirection: userId == blockedUserId,
                    Reason: userId == blockedUserId ? "BLOCKED" : null,
                    ActorBlockedTarget: false,
                    TargetBlockedActor: userId == blockedUserId)).ToArray()));
    }

    private sealed class RecordingUploadMediaClient : IUploadMediaClient
    {
        public bool Authorized { get; set; } = true;

        public Func<long, string, bool>? OwnerPredicate { get; set; }

        public List<(IReadOnlyCollection<string> Urls, long OwnerUserId)> AuthorizationCalls { get; } = [];

        public Task<UploadMediaAuthorizationResult> AuthorizeOwnedAsync(
            IReadOnlyCollection<string> urls,
            long ownerUserId,
            CancellationToken cancellationToken)
        {
            AuthorizationCalls.Add((urls, ownerUserId));
            var unauthorized = !Authorized
                ? urls.ToHashSet(StringComparer.OrdinalIgnoreCase)
                : urls.Where(url => OwnerPredicate is not null && !OwnerPredicate(ownerUserId, url))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(new UploadMediaAuthorizationResult(
                unauthorized.Count == 0,
                unauthorized));
        }

        public Task<UploadMediaAuthorizationResult> AuthorizeReferencesAsync(
            IReadOnlyCollection<UploadMediaReference> references,
            long ownerUserId,
            DateTimeOffset operationAt,
            CancellationToken cancellationToken)
        {
            AuthorizationCalls.Add((references.Select(reference => reference.Url).ToArray(), ownerUserId));
            var unauthorized = !Authorized
                ? references.Select(reference => reference.Url).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : references.Select(reference => reference.Url)
                    .Where(url => OwnerPredicate is not null && !OwnerPredicate(ownerUserId, url))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(new UploadMediaAuthorizationResult(
                unauthorized.Count == 0,
                unauthorized));
        }

        public Task FinalizeReferencesAsync(
            IReadOnlyCollection<UploadMediaReference> references,
            long? ownerUserId,
            DateTimeOffset operationAt,
            CancellationToken cancellationToken) => Task.CompletedTask;

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
