using MessengerService.Application;
using MessengerService.Application.Abstractions;
using MessengerService.Application.Models;
using MessengerService.Domain.Entities;
using MessengerService.Domain.Enums;
using MessengerService.Infrastructure.Persistence;
using MessengerService.Infrastructure.Realtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace MessengerService.Tests;

public sealed class TextInputSanitizerTests
{
    [Fact]
    public void Normalize_PreservesLanguagesAndEmoji_ButCanonicalizesEquivalentText()
    {
        var normalized = TextInputSanitizer.NormalizeRequired(
            "  e\u0301 — مرحبا 👩‍💻  ",
            100,
            "Message text",
            allowLineBreaks: true);

        Assert.Equal("é — مرحبا 👩‍💻".Normalize(), normalized);
    }

    [Fact]
    public void Normalize_CanonicalizesTabsAndLineEndings_AndBoundsBlankLines()
    {
        var normalized = TextInputSanitizer.NormalizeRequired(
            "a\t\r\n\n\n\n\n b",
            100,
            "Message text",
            allowLineBreaks: true);

        Assert.Equal("a \n\n\n\n b", normalized);
    }

    [Theory]
    [InlineData(" https://media.example/file.png")]
    [InlineData("https://media.example/file\tname.png")]
    [InlineData("https://media.example/cafe\u0301.png")]
    public void SafeSyntax_RejectsValuesThatWouldBeRewritten(string value)
    {
        Assert.Throws<MessagingApplicationException>(() =>
            TextInputSanitizer.EnsureSafeSyntax(value, 2_048, "Attachment URL"));
    }

    [Theory]
    [InlineData("A\u0301\u0301\u0301\u0301\u0301\u0301")]
    [InlineData("safe\u202Eevil")]
    [InlineData("safe\u0000evil")]
    public void Normalize_RejectsZalgoBidiAndControlText(string value)
    {
        var error = Assert.Throws<MessagingApplicationException>(() =>
            TextInputSanitizer.NormalizeRequired(value, 100, "Message text"));

        Assert.Equal(MessagingErrorCodes.InvalidInput, error.Code);
    }

    [Fact]
    public void Normalize_RejectsMalformedUtf16AndOversizedValues()
    {
        var malformed = Assert.Throws<MessagingApplicationException>(() =>
            TextInputSanitizer.NormalizeRequired("\uD800", 100, "Message text"));
        var oversized = Assert.Throws<MessagingApplicationException>(() =>
            TextInputSanitizer.NormalizeRequired(new string('x', 20_001), 20_000, "Message text"));

        Assert.Equal(MessagingErrorCodes.InvalidInput, malformed.Code);
        Assert.Equal(MessagingErrorCodes.InvalidInput, oversized.Code);
    }
}

public sealed class MessagingInputBoundaryTests
{
    [Fact]
    public async Task SendMessage_AllowsExactlyTwentyThousandCharacters_AndRejectsTheNextOne()
    {
        await using var db = NewDb();
        var conversation = SeedGroup(db);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = NewService(db);
        var maximum = new string('x', 20_000);

        var sent = await service.SendMessageAsync(
            11,
            new SendMessageCommand(conversation.Id, Guid.NewGuid(), maximum, [], null),
            TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<MessagingApplicationException>(() => service.SendMessageAsync(
            11,
            new SendMessageCommand(conversation.Id, Guid.NewGuid(), maximum + "x", [], null),
            TestContext.Current.CancellationToken));

        Assert.Equal(maximum, sent.Text);
        Assert.Equal(MessagingErrorCodes.InvalidInput, error.Code);
    }

    [Fact]
    public async Task GroupTitleAndAttachmentMetadataRejectFormattingControlsAndNormalizeSafeText()
    {
        await using var db = NewDb();
        var conversation = SeedGroup(db);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = NewService(db);

        var updated = await service.UpdateGroupConversationAsync(
            11,
            new UpdateGroupConversationCommand(
                conversation.Id,
                HasTitle: true,
                Title: "Cafe\u0301",
                HasAvatarUrl: false,
                AvatarUrl: null),
            TestContext.Current.CancellationToken);

        Assert.Equal("Café", updated.Title);

        var unsafeName = await Assert.ThrowsAsync<MessagingApplicationException>(() => service.SendMessageAsync(
            11,
            new SendMessageCommand(
                conversation.Id,
                Guid.NewGuid(),
                null,
                [],
                null,
                [new MessageAttachmentCommand(
                    "https://cdn.example.test/file.mp4",
                    OriginalName: "report\u202Egpj")]),
            TestContext.Current.CancellationToken));

        Assert.Equal(MessagingErrorCodes.InvalidInput, unsafeName.Code);
    }

    [Fact]
    public async Task MessageOperations_DoNotRevealMessagesToNonMembers()
    {
        await using var db = NewDb();
        var conversation = SeedGroup(db);
        db.Users.Add(new MessagingUser { UserId = 33, Status = MessagingUserStatus.Active });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = NewService(db);
        var sent = await service.SendMessageAsync(
            11,
            new SendMessageCommand(conversation.Id, Guid.NewGuid(), "private", [], null),
            TestContext.Current.CancellationToken);

        var get = await Assert.ThrowsAsync<MessagingApplicationException>(() => service.GetMessageAsync(
            33, sent.Id, TestContext.Current.CancellationToken));
        var edit = await Assert.ThrowsAsync<MessagingApplicationException>(() => service.EditMessageAsync(
            33, new EditMessageCommand(sent.Id, "forged"), TestContext.Current.CancellationToken));
        var delete = await Assert.ThrowsAsync<MessagingApplicationException>(() => service.DeleteMessageAsync(
            33, new DeleteMessageCommand(sent.Id), TestContext.Current.CancellationToken));
        var react = await Assert.ThrowsAsync<MessagingApplicationException>(() => service.SetMessageReactionAsync(
            33, new SetMessageReactionCommand(sent.Id, "👍"), TestContext.Current.CancellationToken));

        Assert.All(new[] { get, edit, delete, react }, error =>
            Assert.Equal(MessagingErrorCodes.MessageNotFound, error.Code));
    }

    private static MessagingDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<MessagingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new MessagingDbContext(options);
    }

    private static MessagingApplicationService NewService(MessagingDbContext db) =>
        new(
            db,
            new AllowAllPermissionClient(),
            new FakeProvisioningService(),
            null!,
            new OutboxWakeSignal(),
            TimeProvider.System,
            Options.Create(new MessagingRulesOptions
            {
                AllowedAttachmentHosts = ["cdn.example.test"]
            }));

    private static Conversation SeedGroup(MessagingDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            Type = ConversationType.Group,
            Title = "Initial",
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

    private sealed class AllowAllPermissionClient : ISocialGraphPermissionClient
    {
        public Task<SocialGraphPermissionCheckResult> CheckAsync(
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
}
