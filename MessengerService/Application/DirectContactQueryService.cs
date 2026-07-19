using MessengerService.Domain.Enums;
using MessengerService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MessengerService.Application;

public sealed class DirectContactQueryService(MessagingDbContext db)
{
    public async Task<IReadOnlyList<long>> GetContactIdsAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userId));
        }

        var activeUser = await db.Users
            .AsNoTracking()
            .AnyAsync(
                user => user.UserId == userId && user.Status == MessagingUserStatus.Active,
                cancellationToken);
        if (!activeUser)
        {
            return Array.Empty<long>();
        }

        var contactIds = db.Conversations
            .AsNoTracking()
            .Where(conversation =>
                conversation.Type == ConversationType.Direct &&
                (conversation.DirectUserLowId == userId || conversation.DirectUserHighId == userId))
            .Select(conversation =>
                conversation.DirectUserLowId == userId
                    ? conversation.DirectUserHighId
                    : conversation.DirectUserLowId)
            .Where(contactId => contactId.HasValue)
            .Select(contactId => contactId!.Value);

        return await contactIds
            .Join(
                db.Users.AsNoTracking().Where(user => user.Status == MessagingUserStatus.Active),
                contactId => contactId,
                user => user.UserId,
                (contactId, _) => contactId)
            .Where(contactId => contactId != userId)
            .Distinct()
            .OrderBy(contactId => contactId)
            .ToArrayAsync(cancellationToken);
    }
}
