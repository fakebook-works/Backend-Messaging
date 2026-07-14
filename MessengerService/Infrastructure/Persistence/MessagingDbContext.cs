using MessengerService.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MessengerService.Infrastructure.Persistence;

public sealed class MessagingDbContext(DbContextOptions<MessagingDbContext> options) : DbContext(options)
{
    public const string Schema = "messenger";

    public DbSet<MessagingUser> Users => Set<MessagingUser>();

    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<ConversationParticipant> ConversationParticipants => Set<ConversationParticipant>();

    public DbSet<Message> Messages => Set<Message>();

    public DbSet<MessageAttachment> MessageAttachments => Set<MessageAttachment>();

    public DbSet<MessageReaction> MessageReactions => Set<MessageReaction>();

    public DbSet<UserPresence> UserPresences => Set<UserPresence>();

    public DbSet<OutboxEvent> OutboxEvents => Set<OutboxEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MessagingDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}
