using MessengerService.Application;
using MessengerService.Application.Abstractions;
using MessengerService.Application.Models;
using MessengerService.GraphQL.Inputs;

namespace MessengerService.GraphQL;

[GraphQLName("Mutation")]
public sealed class MessagingMutation
{
    public Task<ConversationView> CreateDirectConversation(
        CreateDirectConversationInput input,
        [Service] MessagingApplicationService messaging,
        [Service] ITrustedUserContextAccessor userContext,
        CancellationToken cancellationToken) =>
        messaging.CreateDirectConversationAsync(userContext.RequireUserId(),
            new CreateDirectConversationCommand(input.TargetUserId), cancellationToken);

    public Task<ConversationView> CreateGroupConversation(
        CreateGroupConversationInput input,
        [Service] MessagingApplicationService messaging,
        [Service] ITrustedUserContextAccessor userContext,
        CancellationToken cancellationToken) =>
        messaging.CreateGroupConversationAsync(userContext.RequireUserId(),
            new CreateGroupConversationCommand(input.Title, input.MemberUserIds, input.AvatarUrl), cancellationToken);

    public Task<ConversationView> UpdateGroupConversation(
        UpdateGroupConversationInput input,
        [Service] MessagingApplicationService messaging,
        [Service] ITrustedUserContextAccessor userContext,
        CancellationToken cancellationToken) =>
        messaging.UpdateGroupConversationAsync(userContext.RequireUserId(),
            new UpdateGroupConversationCommand(input.ConversationId,
                input.Title.HasValue, input.Title.HasValue ? input.Title.Value : null,
                input.AvatarUrl.HasValue, input.AvatarUrl.HasValue ? input.AvatarUrl.Value : null),
            cancellationToken);

    public Task<ConversationView> AddConversationMembers(
        AddConversationMembersInput input,
        [Service] MessagingApplicationService messaging,
        [Service] ITrustedUserContextAccessor userContext,
        CancellationToken cancellationToken) =>
        messaging.AddConversationMembersAsync(userContext.RequireUserId(),
            new AddConversationMembersCommand(input.ConversationId, input.UserIds), cancellationToken);

    public Task<ConversationView> RemoveConversationMember(
        RemoveConversationMemberInput input,
        [Service] MessagingApplicationService messaging,
        [Service] ITrustedUserContextAccessor userContext,
        CancellationToken cancellationToken) =>
        messaging.RemoveConversationMemberAsync(userContext.RequireUserId(),
            new RemoveConversationMemberCommand(input.ConversationId, input.UserId), cancellationToken);

    public Task<ConversationView> SetConversationMemberRole(
        SetConversationMemberRoleInput input,
        [Service] MessagingApplicationService messaging,
        [Service] ITrustedUserContextAccessor userContext,
        CancellationToken cancellationToken) =>
        messaging.SetConversationMemberRoleAsync(userContext.RequireUserId(),
            new SetConversationMemberRoleCommand(input.ConversationId, input.UserId, input.Role), cancellationToken);

    public Task<ConversationView> LeaveConversation(
        Guid conversationId,
        [Service] MessagingApplicationService messaging,
        [Service] ITrustedUserContextAccessor userContext,
        CancellationToken cancellationToken) =>
        messaging.LeaveConversationAsync(userContext.RequireUserId(), conversationId, cancellationToken);

    public Task<MessageView> SendMessage(
        SendMessageInput input,
        [Service] MessagingApplicationService messaging,
        [Service] ITrustedUserContextAccessor userContext,
        CancellationToken cancellationToken) =>
        messaging.SendMessageAsync(userContext.RequireUserId(),
            new SendMessageCommand(input.ConversationId, input.ClientMessageId, input.Text,
                input.AttachmentUrls ?? [], input.ReplyToMessageId,
                input.Attachments?.Select(attachment => new MessageAttachmentCommand(
                    attachment.Url,
                    attachment.AssetId,
                    attachment.MediaType,
                    attachment.ContentType,
                    attachment.OriginalName,
                    attachment.SizeBytes,
                    attachment.Width,
                    attachment.Height,
                    attachment.DurationMs,
                    attachment.ThumbnailUrl)).ToArray()), cancellationToken);

    public Task<MessageView> EditMessage(
        EditMessageInput input,
        [Service] MessagingApplicationService messaging,
        [Service] ITrustedUserContextAccessor userContext,
        CancellationToken cancellationToken) =>
        messaging.EditMessageAsync(userContext.RequireUserId(),
            new EditMessageCommand(input.MessageId, input.Text), cancellationToken);

    public Task<MessageView> DeleteMessage(
        DeleteMessageInput input,
        [Service] MessagingApplicationService messaging,
        [Service] ITrustedUserContextAccessor userContext,
        CancellationToken cancellationToken) =>
        messaging.DeleteMessageAsync(userContext.RequireUserId(),
            new DeleteMessageCommand(input.MessageId), cancellationToken);

    public Task<MessageView> SetMessageReaction(
        SetMessageReactionInput input,
        [Service] MessagingApplicationService messaging,
        [Service] ITrustedUserContextAccessor userContext,
        CancellationToken cancellationToken) =>
        messaging.SetMessageReactionAsync(userContext.RequireUserId(),
            new SetMessageReactionCommand(input.MessageId, input.Emoji), cancellationToken);

    public Task<ConversationReceiptView> MarkConversationDelivered(
        MarkConversationReceiptInput input,
        [Service] MessagingApplicationService messaging,
        [Service] ITrustedUserContextAccessor userContext,
        CancellationToken cancellationToken) =>
        messaging.MarkDeliveredAsync(userContext.RequireUserId(),
            new MarkConversationReceiptCommand(input.ConversationId, input.Sequence), cancellationToken);

    public Task<ConversationReceiptView> MarkConversationRead(
        MarkConversationReceiptInput input,
        [Service] MessagingApplicationService messaging,
        [Service] ITrustedUserContextAccessor userContext,
        CancellationToken cancellationToken) =>
        messaging.MarkReadAsync(userContext.RequireUserId(),
            new MarkConversationReceiptCommand(input.ConversationId, input.Sequence), cancellationToken);

    public Task<TypingView> SetTyping(
        SetTypingInput input,
        [Service] MessagingApplicationService messaging,
        [Service] ITrustedUserContextAccessor userContext,
        CancellationToken cancellationToken) =>
        messaging.SetTypingAsync(userContext.RequireUserId(),
            new SetTypingCommand(input.ConversationId, input.IsTyping), cancellationToken);

    public Task<UserPresenceView> HeartbeatPresence(
        [Service] MessagingApplicationService messaging,
        [Service] ITrustedUserContextAccessor userContext,
        CancellationToken cancellationToken) =>
        messaging.HeartbeatPresenceAsync(userContext.RequireUserId(), cancellationToken);
}
