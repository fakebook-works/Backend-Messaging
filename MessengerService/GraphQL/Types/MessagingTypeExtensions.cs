using HotChocolate.Types;
using MessengerService.Application.Models;

namespace MessengerService.GraphQL.Types;

public sealed class ConversationParticipantTypeExtension
    : ObjectTypeExtension<ConversationParticipantView>
{
    protected override void Configure(
        IObjectTypeDescriptor<ConversationParticipantView> descriptor)
    {
        descriptor
            .Field("user")
            .Type<ObjectType<UserReference>>()
            .Resolve(context =>
                new UserReference(context.Parent<ConversationParticipantView>().UserId));
    }
}

public sealed class MessageTypeExtension : ObjectTypeExtension<MessageView>
{
    protected override void Configure(IObjectTypeDescriptor<MessageView> descriptor)
    {
        descriptor
            .Field("sender")
            .Type<ObjectType<UserReference>>()
            .Resolve(context => new UserReference(context.Parent<MessageView>().SenderUserId));

        descriptor
            .Field("systemSubject")
            .Type<ObjectType<UserReference>>()
            .Resolve(context => context.Parent<MessageView>().SystemSubjectUserId is { } userId
                ? new UserReference(userId)
                : null);
    }
}

public sealed class MessageReactionTypeExtension
    : ObjectTypeExtension<MessageReactionView>
{
    protected override void Configure(IObjectTypeDescriptor<MessageReactionView> descriptor)
    {
        descriptor
            .Field("user")
            .Type<ObjectType<UserReference>>()
            .Resolve(context =>
                new UserReference(context.Parent<MessageReactionView>().UserId));
    }
}
