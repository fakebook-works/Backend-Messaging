using MessengerService.Domain.Entities;
using MessengerService.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MessengerService.Infrastructure.Persistence.Configurations;

internal sealed class MessagingUserConfiguration : IEntityTypeConfiguration<MessagingUser>
{
    public void Configure(EntityTypeBuilder<MessagingUser> builder)
    {
        builder.ToTable("users", MessagingDbContext.Schema, table =>
        {
            table.HasCheckConstraint("ck_users_user_id_positive", "user_id > 0");
            table.HasCheckConstraint("ck_users_status", "status IN ('Active', 'Deleted')");
            table.HasCheckConstraint(
                "ck_users_deleted_state",
                "(status = 'Active' AND deleted_at IS NULL) OR " +
                "(status = 'Deleted' AND deleted_at IS NOT NULL)");
        });

        builder.HasKey(user => user.UserId)
            .HasName("pk_users");

        builder.Property(user => user.UserId)
            .HasColumnName("user_id")
            .ValueGeneratedNever();

        builder.Property(user => user.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(user => user.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(user => user.DeletedAt)
            .HasColumnName("deleted_at");

        builder.HasIndex(user => user.Status)
            .HasDatabaseName("ix_users_status");
    }
}

internal sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        builder.ToTable("conversations", MessagingDbContext.Schema, table =>
        {
            table.HasCheckConstraint("ck_conversations_type", "type IN ('Direct', 'Group')");
            table.HasCheckConstraint("ck_conversations_current_sequence", "current_sequence >= 0");
            table.HasCheckConstraint(
                "ck_conversations_direct_pair",
                "(type = 'Direct' AND direct_user_low_id IS NOT NULL " +
                "AND direct_user_high_id IS NOT NULL " +
                "AND direct_user_low_id > 0 " +
                "AND direct_user_low_id < direct_user_high_id) OR " +
                "(type = 'Group' AND direct_user_low_id IS NULL " +
                "AND direct_user_high_id IS NULL)");
            table.HasCheckConstraint(
                "ck_conversations_updated_at",
                "updated_at >= created_at");
        });

        builder.HasKey(conversation => conversation.Id)
            .HasName("pk_conversations");

        builder.Property(conversation => conversation.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(conversation => conversation.Type)
            .HasColumnName("type")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(conversation => conversation.Title)
            .HasColumnName("title")
            .HasMaxLength(200);

        builder.Property(conversation => conversation.AvatarUrl)
            .HasColumnName("avatar_url")
            .HasMaxLength(2048);

        builder.Property(conversation => conversation.DirectUserLowId)
            .HasColumnName("direct_user_low_id");

        builder.Property(conversation => conversation.DirectUserHighId)
            .HasColumnName("direct_user_high_id");

        builder.Property(conversation => conversation.CurrentSequence)
            .HasColumnName("current_sequence")
            .HasDefaultValue(0L)
            .IsConcurrencyToken();

        builder.Property(conversation => conversation.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(conversation => conversation.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.HasIndex(conversation => new
        {
            conversation.DirectUserLowId,
            conversation.DirectUserHighId
        })
            .IsUnique()
            .HasFilter("type = 'Direct'")
            .HasDatabaseName("ux_conversations_direct_pair");

        builder.HasIndex(conversation => conversation.UpdatedAt)
            .IsDescending()
            .HasDatabaseName("ix_conversations_updated_at");
    }
}

internal sealed class ConversationParticipantConfiguration
    : IEntityTypeConfiguration<ConversationParticipant>
{
    public void Configure(EntityTypeBuilder<ConversationParticipant> builder)
    {
        builder.ToTable("conversation_participants", MessagingDbContext.Schema, table =>
        {
            table.HasCheckConstraint("ck_conversation_participants_user_id", "user_id > 0");
            table.HasCheckConstraint(
                "ck_conversation_participants_receipts",
                "last_delivered_sequence >= 0 " +
                "AND last_read_sequence >= 0 " +
                "AND last_read_sequence <= last_delivered_sequence");
            table.HasCheckConstraint(
                "ck_conversation_participants_membership_dates",
                "left_at IS NULL OR left_at >= joined_at");
            table.HasCheckConstraint(
                "ck_conversation_participants_role",
                "role IN ('Admin', 'Member')");
        });

        builder.HasKey(participant => new
        {
            participant.ConversationId,
            participant.UserId
        })
            .HasName("pk_conversation_participants");

        builder.Property(participant => participant.ConversationId)
            .HasColumnName("conversation_id");

        builder.Property(participant => participant.UserId)
            .HasColumnName("user_id");

        builder.Property(participant => participant.Role)
            .HasColumnName("role")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(participant => participant.JoinedAt)
            .HasColumnName("joined_at")
            .IsRequired();

        builder.Property(participant => participant.LeftAt)
            .HasColumnName("left_at");

        builder.Property(participant => participant.LastDeliveredSequence)
            .HasColumnName("last_delivered_sequence")
            .HasDefaultValue(0L);

        builder.Property(participant => participant.LastReadSequence)
            .HasColumnName("last_read_sequence")
            .HasDefaultValue(0L);

        builder.HasOne(participant => participant.Conversation)
            .WithMany(conversation => conversation.Participants)
            .HasForeignKey(participant => participant.ConversationId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_conversation_participants_conversations");

        builder.HasOne(participant => participant.User)
            .WithMany(user => user.ConversationParticipants)
            .HasForeignKey(participant => participant.UserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_conversation_participants_users");

        builder.HasIndex(participant => new
        {
            participant.UserId,
            participant.ConversationId
        })
            .HasFilter("left_at IS NULL")
            .HasDatabaseName("ix_conversation_participants_active_user");

        builder.HasIndex(participant => participant.ConversationId)
            .HasFilter("left_at IS NULL")
            .HasDatabaseName("ix_conversation_participants_active_conversation");
    }
}

internal sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.ToTable("messages", MessagingDbContext.Schema, table =>
        {
            table.HasCheckConstraint("ck_messages_sender_user_id", "sender_user_id > 0");
            table.HasCheckConstraint("ck_messages_sequence", "sequence > 0");
            table.HasCheckConstraint(
                "ck_messages_client_message_id",
                "client_message_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint(
                "ck_messages_edit_date",
                "edited_at IS NULL OR edited_at >= created_at");
            table.HasCheckConstraint(
                "ck_messages_delete_date",
                "deleted_at IS NULL OR deleted_at >= created_at");
        });

        builder.HasKey(message => message.Id)
            .HasName("pk_messages");

        builder.Property(message => message.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(message => message.ConversationId)
            .HasColumnName("conversation_id");

        builder.Property(message => message.SenderUserId)
            .HasColumnName("sender_user_id");

        builder.Property(message => message.Sequence)
            .HasColumnName("sequence");

        builder.Property(message => message.ClientMessageId)
            .HasColumnName("client_message_id")
            .ValueGeneratedNever();

        builder.Property(message => message.Text)
            .HasColumnName("text")
            .HasMaxLength(10_000);

        builder.Property(message => message.ReplyToMessageId)
            .HasColumnName("reply_to_message_id");

        builder.Property(message => message.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(message => message.EditedAt)
            .HasColumnName("edited_at");

        builder.Property(message => message.DeletedAt)
            .HasColumnName("deleted_at");

        builder.HasOne(message => message.Conversation)
            .WithMany(conversation => conversation.Messages)
            .HasForeignKey(message => message.ConversationId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_messages_conversations");

        builder.HasOne(message => message.Sender)
            .WithMany(user => user.SentMessages)
            .HasForeignKey(message => message.SenderUserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_messages_users");

        builder.HasOne(message => message.ReplyToMessage)
            .WithMany(message => message.Replies)
            .HasForeignKey(message => message.ReplyToMessageId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_messages_reply_to_message");

        builder.HasIndex(message => new
        {
            message.ConversationId,
            message.Sequence
        })
            .IsUnique()
            .IsDescending(false, true)
            .HasDatabaseName("ux_messages_conversation_sequence");

        builder.HasIndex(message => new
        {
            message.SenderUserId,
            message.ClientMessageId
        })
            .IsUnique()
            .HasDatabaseName("ux_messages_sender_client_message");

        builder.HasIndex(message => message.ReplyToMessageId)
            .HasDatabaseName("ix_messages_reply_to_message_id");
    }
}

internal sealed class MessageAttachmentConfiguration
    : IEntityTypeConfiguration<MessageAttachment>
{
    public void Configure(EntityTypeBuilder<MessageAttachment> builder)
    {
        builder.ToTable("message_attachments", MessagingDbContext.Schema, table =>
        {
            table.HasCheckConstraint(
                "ck_message_attachments_ordinal",
                "ordinal >= 0 AND ordinal < 10");
            table.HasCheckConstraint(
                "ck_message_attachments_https_url",
                "url LIKE '/media/files/%' OR url LIKE 'https://%'");
            table.HasCheckConstraint(
                "ck_message_attachments_media_type",
                "media_type IS NULL OR media_type IN ('image', 'video', 'audio', 'file')");
            table.HasCheckConstraint(
                "ck_message_attachments_metadata_nonnegative",
                "(size_bytes IS NULL OR size_bytes >= 0) AND " +
                "(width IS NULL OR width >= 0) AND " +
                "(height IS NULL OR height >= 0) AND " +
                "(duration_ms IS NULL OR duration_ms >= 0)");
        });

        builder.HasKey(attachment => new
        {
            attachment.MessageId,
            attachment.Ordinal
        })
            .HasName("pk_message_attachments");

        builder.Property(attachment => attachment.MessageId)
            .HasColumnName("message_id");

        builder.Property(attachment => attachment.Ordinal)
            .HasColumnName("ordinal");

        builder.Property(attachment => attachment.Url)
            .HasColumnName("url")
            .HasMaxLength(2048)
            .IsRequired();

        builder.Property(attachment => attachment.AssetId)
            .HasColumnName("asset_id")
            .HasMaxLength(128);

        builder.Property(attachment => attachment.MediaType)
            .HasColumnName("media_type")
            .HasMaxLength(16);

        builder.Property(attachment => attachment.ContentType)
            .HasColumnName("content_type")
            .HasMaxLength(128);

        builder.Property(attachment => attachment.OriginalName)
            .HasColumnName("original_name")
            .HasMaxLength(255);

        builder.Property(attachment => attachment.SizeBytes)
            .HasColumnName("size_bytes");

        builder.Property(attachment => attachment.Width)
            .HasColumnName("width");

        builder.Property(attachment => attachment.Height)
            .HasColumnName("height");

        builder.Property(attachment => attachment.DurationMs)
            .HasColumnName("duration_ms");

        builder.Property(attachment => attachment.ThumbnailUrl)
            .HasColumnName("thumbnail_url")
            .HasMaxLength(2048);

        builder.HasOne(attachment => attachment.Message)
            .WithMany(message => message.Attachments)
            .HasForeignKey(attachment => attachment.MessageId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_message_attachments_messages");
    }
}

internal sealed class MessageReactionConfiguration
    : IEntityTypeConfiguration<MessageReaction>
{
    public void Configure(EntityTypeBuilder<MessageReaction> builder)
    {
        builder.ToTable("message_reactions", MessagingDbContext.Schema, table =>
        {
            table.HasCheckConstraint("ck_message_reactions_user_id", "user_id > 0");
            table.HasCheckConstraint(
                "ck_message_reactions_emoji",
                "length(btrim(emoji)) > 0");
            table.HasCheckConstraint(
                "ck_message_reactions_updated_at",
                "updated_at >= created_at");
        });

        builder.HasKey(reaction => new
        {
            reaction.MessageId,
            reaction.UserId
        })
            .HasName("pk_message_reactions");

        builder.Property(reaction => reaction.MessageId)
            .HasColumnName("message_id");

        builder.Property(reaction => reaction.UserId)
            .HasColumnName("user_id");

        builder.Property(reaction => reaction.Emoji)
            .HasColumnName("emoji")
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(reaction => reaction.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(reaction => reaction.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.HasOne(reaction => reaction.Message)
            .WithMany(message => message.Reactions)
            .HasForeignKey(reaction => reaction.MessageId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_message_reactions_messages");

        builder.HasOne(reaction => reaction.User)
            .WithMany(user => user.MessageReactions)
            .HasForeignKey(reaction => reaction.UserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_message_reactions_users");

        builder.HasIndex(reaction => reaction.UserId)
            .HasDatabaseName("ix_message_reactions_user_id");
    }
}

internal sealed class UserPresenceConfiguration : IEntityTypeConfiguration<UserPresence>
{
    public void Configure(EntityTypeBuilder<UserPresence> builder)
    {
        builder.ToTable("presence", MessagingDbContext.Schema, table =>
        {
            table.HasCheckConstraint("ck_presence_user_id", "user_id > 0");
        });

        builder.HasKey(presence => presence.UserId)
            .HasName("pk_presence");

        builder.Property(presence => presence.UserId)
            .HasColumnName("user_id")
            .ValueGeneratedNever();

        builder.Property(presence => presence.IsOnline)
            .HasColumnName("is_online")
            .IsRequired();

        builder.Property(presence => presence.ExpiresAt)
            .HasColumnName("expires_at")
            .IsRequired();

        builder.Property(presence => presence.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.HasOne(presence => presence.User)
            .WithOne(user => user.Presence)
            .HasForeignKey<UserPresence>(presence => presence.UserId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_presence_users");

        builder.HasIndex(presence => presence.ExpiresAt)
            .HasFilter("is_online = TRUE")
            .HasDatabaseName("ix_presence_online_expiry");
    }
}

internal sealed class OutboxEventConfiguration : IEntityTypeConfiguration<OutboxEvent>
{
    public void Configure(EntityTypeBuilder<OutboxEvent> builder)
    {
        builder.ToTable("outbox_events", MessagingDbContext.Schema, table =>
        {
            table.HasCheckConstraint("ck_outbox_events_attempt_count", "attempt_count >= 0");
            table.HasCheckConstraint(
                "ck_outbox_events_actor_user_id",
                "actor_user_id IS NULL OR actor_user_id > 0");
            table.HasCheckConstraint(
                "ck_outbox_events_subject_user_id",
                "subject_user_id IS NULL OR subject_user_id > 0");
            table.HasCheckConstraint(
                "ck_outbox_events_sequence",
                "sequence IS NULL OR sequence >= 0");
        });

        builder.HasKey(outboxEvent => outboxEvent.Id)
            .HasName("pk_outbox_events");

        builder.Property(outboxEvent => outboxEvent.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(outboxEvent => outboxEvent.Topic)
            .HasColumnName("topic")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(outboxEvent => outboxEvent.Kind)
            .HasColumnName("kind")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(outboxEvent => outboxEvent.PayloadJson)
            .HasColumnName("payload_json")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(outboxEvent => outboxEvent.ConversationId)
            .HasColumnName("conversation_id");

        builder.Property(outboxEvent => outboxEvent.MessageId)
            .HasColumnName("message_id");

        builder.Property(outboxEvent => outboxEvent.ActorUserId)
            .HasColumnName("actor_user_id");

        builder.Property(outboxEvent => outboxEvent.SubjectUserId)
            .HasColumnName("subject_user_id");

        builder.Property(outboxEvent => outboxEvent.Sequence)
            .HasColumnName("sequence");

        builder.Property(outboxEvent => outboxEvent.OccurredAt)
            .HasColumnName("occurred_at")
            .IsRequired();

        builder.Property(outboxEvent => outboxEvent.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(outboxEvent => outboxEvent.ProcessedAt)
            .HasColumnName("processed_at");

        builder.Property(outboxEvent => outboxEvent.AttemptCount)
            .HasColumnName("attempt_count")
            .HasDefaultValue(0);

        builder.Property(outboxEvent => outboxEvent.NextAttemptAt)
            .HasColumnName("next_attempt_at");

        builder.Property(outboxEvent => outboxEvent.LastError)
            .HasColumnName("last_error")
            .HasMaxLength(4000);

        builder.HasIndex(outboxEvent => new
        {
            outboxEvent.NextAttemptAt,
            outboxEvent.CreatedAt
        })
            .HasFilter("processed_at IS NULL")
            .HasDatabaseName("ix_outbox_events_pending");

        builder.HasIndex(outboxEvent => new
        {
            outboxEvent.Topic,
            outboxEvent.CreatedAt
        })
            .HasFilter("processed_at IS NULL")
            .HasDatabaseName("ix_outbox_events_pending_topic");

        builder.HasIndex(outboxEvent => outboxEvent.ProcessedAt)
            .HasFilter("processed_at IS NOT NULL")
            .HasDatabaseName("ix_outbox_events_processed");
    }
}
