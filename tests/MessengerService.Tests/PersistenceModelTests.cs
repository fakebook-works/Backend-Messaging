using MessengerService.Domain.Entities;
using MessengerService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace MessengerService.Tests;

public sealed class PersistenceModelTests
{
    [Fact]
    public void Model_UsesDedicatedMessengerSchemaAndExpectedTables()
    {
        using var context = CreateContext();

        Assert.Equal(MessagingDbContext.Schema, context.Model.GetDefaultSchema());
        AssertEntityTable<MessagingUser>(context, "users");
        AssertEntityTable<Conversation>(context, "conversations");
        AssertEntityTable<ConversationParticipant>(context, "conversation_participants");
        AssertEntityTable<Message>(context, "messages");
        AssertEntityTable<MessageAttachment>(context, "message_attachments");
        AssertEntityTable<MessageReaction>(context, "message_reactions");
        AssertEntityTable<UserPresence>(context, "presence");
        AssertEntityTable<OutboxEvent>(context, "outbox_events");
    }

    [Fact]
    public void DirectConversationPair_HasFilteredUniqueIndex()
    {
        using var context = CreateContext();
        var entity = context.GetService<IDesignTimeModel>()
            .Model
            .FindEntityType(typeof(Conversation))!;
        var index = entity.GetIndexes().Single(candidate =>
            candidate.GetDatabaseName() == "ux_conversations_direct_pair");

        Assert.True(index.IsUnique);
        Assert.Equal(
            [nameof(Conversation.DirectUserLowId), nameof(Conversation.DirectUserHighId)],
            index.Properties.Select(property => property.Name).ToArray());
        Assert.Equal("type = 'Direct'", index.GetFilter());
        Assert.Contains(
            entity.GetCheckConstraints(),
            constraint => constraint.Name == "ck_conversations_direct_pair");
    }

    [Fact]
    public void Messages_EnforceSequenceAndClientIdempotencyIndexes()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(Message))!;

        var sequenceIndex = entity.GetIndexes().Single(candidate =>
            candidate.GetDatabaseName() == "ux_messages_conversation_sequence");
        Assert.True(sequenceIndex.IsUnique);
        Assert.Equal(
            [nameof(Message.ConversationId), nameof(Message.Sequence)],
            sequenceIndex.Properties.Select(property => property.Name).ToArray());

        var clientIndex = entity.GetIndexes().Single(candidate =>
            candidate.GetDatabaseName() == "ux_messages_sender_client_message");
        Assert.True(clientIndex.IsUnique);
        Assert.Equal(
            [nameof(Message.SenderUserId), nameof(Message.ClientMessageId)],
            clientIndex.Properties.Select(property => property.Name).ToArray());
        Assert.Equal(10_000, entity.FindProperty(nameof(Message.Text))!.GetMaxLength());
    }

    [Fact]
    public void AttachmentsAndReceipts_ExposeDatabaseGuardrails()
    {
        using var context = CreateContext();
        var designTimeModel = context.GetService<IDesignTimeModel>().Model;
        var attachment = designTimeModel.FindEntityType(typeof(MessageAttachment))!;
        var participant = designTimeModel.FindEntityType(typeof(ConversationParticipant))!;

        Assert.Equal(
            2048,
            attachment.FindProperty(nameof(MessageAttachment.Url))!.GetMaxLength());
        Assert.Contains(
            attachment.GetCheckConstraints(),
            constraint => constraint.Name == "ck_message_attachments_https_url");
        Assert.Contains(
            participant.GetCheckConstraints(),
            constraint => constraint.Name == "ck_conversation_participants_receipts");
    }

    [Fact]
    public void OutboxPendingIndex_IsPartialAndSupportsRetryScan()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(OutboxEvent))!;
        var index = entity.GetIndexes().Single(candidate =>
            candidate.GetDatabaseName() == "ix_outbox_events_pending");

        Assert.Equal("processed_at IS NULL", index.GetFilter());
        Assert.Equal(
            [nameof(OutboxEvent.NextAttemptAt), nameof(OutboxEvent.CreatedAt)],
            index.Properties.Select(property => property.Name).ToArray());
    }

    [Fact]
    public void OutboxProcessedIndex_SupportsRetentionCleanup()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(OutboxEvent))!;
        var index = entity.GetIndexes().Single(candidate =>
            candidate.GetDatabaseName() == "ix_outbox_events_processed");

        Assert.Equal("processed_at IS NOT NULL", index.GetFilter());
        Assert.Equal(
            [nameof(OutboxEvent.ProcessedAt)],
            index.Properties.Select(property => property.Name).ToArray());
    }

    private static void AssertEntityTable<TEntity>(MessagingDbContext context, string table)
    {
        var entity = context.Model.FindEntityType(typeof(TEntity));
        Assert.NotNull(entity);
        Assert.Equal(table, entity.GetTableName());
        Assert.Equal(MessagingDbContext.Schema, entity.GetSchema());
    }

    private static MessagingDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MessagingDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Database=fake;Username=fake;Password=fake")
            .Options;

        return new MessagingDbContext(options);
    }
}
