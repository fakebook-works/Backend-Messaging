using HotChocolate.Execution;
using MessengerService.GraphQL;
using MessengerService.GraphQL.Types;
using Microsoft.Extensions.DependencyInjection;

namespace MessengerService.Tests;

public sealed class GraphQLSchemaTests
{
    [Fact]
    public async Task MessagingSchema_ContainsFusionReferencesAndSubscriptionRoots()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services
            .AddGraphQLServer("Messaging")
            .AddSourceSchemaDefaults()
            .AddQueryType<MessagingQuery>()
            .AddMutationType<MessagingMutation>()
            .AddSubscriptionType<MessagingSubscription>()
            .AddType<UserReference>()
            .AddTypeExtension<ConversationParticipantTypeExtension>()
            .AddTypeExtension<MessageTypeExtension>()
            .AddTypeExtension<MessageReactionTypeExtension>()
            .AddErrorFilter<MessagingErrorFilter>()
            .AddInMemorySubscriptions();

        await using var provider = services.BuildServiceProvider();
        var resolver = provider.GetRequiredService<IRequestExecutorProvider>();
        var executor = await resolver.GetExecutorAsync(
            "Messaging",
            TestContext.Current.CancellationToken);
        var schema = executor.Schema.ToString();

        Assert.Contains("type Query", schema, StringComparison.Ordinal);
        Assert.Contains("message(id: UUID!): MessageView!", schema, StringComparison.Ordinal);
        Assert.Contains("myDirectConversations(first: Int! = 40, after: String): ConversationPage!", schema, StringComparison.Ordinal);
        Assert.Contains("type Mutation", schema, StringComparison.Ordinal);
        Assert.Contains("type Subscription", schema, StringComparison.Ordinal);
        Assert.Contains("conversationEvents(conversationId: UUID!): RealtimeEvent!", schema, StringComparison.Ordinal);
        Assert.Contains("inboxEvents: RealtimeEvent!", schema, StringComparison.Ordinal);
        Assert.Contains("presenceEvents(userIds: [Long!]!): RealtimeEvent!", schema, StringComparison.Ordinal);
        Assert.Contains("type User @key(fields: \"id\")", schema, StringComparison.Ordinal);
        Assert.Contains("user: User", schema, StringComparison.Ordinal);
        Assert.Contains("sender: User", schema, StringComparison.Ordinal);
        Assert.Contains("input SendMessageAttachmentInput", schema, StringComparison.Ordinal);
        Assert.Contains("attachments: [SendMessageAttachmentInput!]", schema, StringComparison.Ordinal);
        Assert.Contains("mediaType: String", schema, StringComparison.Ordinal);
        Assert.Contains("originalName: String", schema, StringComparison.Ordinal);
        Assert.Contains("sizeBytes: Long", schema, StringComparison.Ordinal);
        Assert.Contains("thumbnailUrl: String", schema, StringComparison.Ordinal);
    }
}
