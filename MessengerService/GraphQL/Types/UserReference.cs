using HotChocolate.Types.Composite;

namespace MessengerService.GraphQL.Types;

[GraphQLName("User")]
[EntityKey("id")]
public sealed record UserReference(long Id);
