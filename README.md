# Fakebook Messaging

`Messaging` is the .NET 8 Hot Chocolate subgraph that owns conversations, participants,
messages, reactions, receipts, typing and presence. The public API is GraphQL at
`/graphql`; SocialGraph provisions and removes local user projections through internal
REST endpoints.

## Trust boundaries

- Gateway GraphQL calls must include trusted `X-Gateway-Secret` and `X-User-Id` headers.
- Inbound SocialGraph provisioning calls must include `X-Internal-MessengerService-Secret`.
- Outbound permission checks to SocialGraph use the separate
  `InternalServices:SocialGraph:SharedSecret` in `X-Internal-SocialGraphService-Secret`.
- Secrets and the PostgreSQL connection string are required configuration and are never
  committed. Start from `.env.example` or use .NET User Secrets.
- Attachment bodies are not accepted. Messaging only stores absolute HTTPS URLs whose
  hosts are present in `Messaging:AllowedAttachmentHosts`.

## Local commands

```powershell
dotnet restore .\MessengerService.sln
dotnet ef database update --project .\MessengerService\MessengerService.csproj
dotnet run --project .\MessengerService\MessengerService.csproj
dotnet test .\MessengerService.sln
```

Export the Fusion v16 source schema:

```powershell
dotnet run --project .\MessengerService\MessengerService.csproj --no-build -- `
  schema export --schema-name Messaging --output .\schema.graphqls
```

Subscriptions use GraphQL over SSE. Send `Accept: text/event-stream` to `/graphql`.
PostgreSQL `LISTEN/NOTIFY` is the subscription provider, so no Redis or WebSocket
middleware is required.

Realtime events are at-least-once invalidation hints. A retry or multiple service
replicas can produce duplicates or deliver later conversation events first. Clients must
deduplicate by `eventId`, apply message `sequence` monotonically, and refetch the
conversation/messages when a sequence gap is observed.

## Internal user lifecycle

```text
POST   /internal/users             { "userId": 123 }
DELETE /internal/users/{userId}
```

Create is idempotent for an active user. Delete is terminal and idempotent; deleting an
unknown ID creates a tombstone so a delayed create event cannot reactivate it. Deletion
atomically marks presence offline, leaves active conversations, promotes the oldest
remaining group member when the final admin is removed, and wakes open SSE streams so
they can terminate after reauthorization.

## Required companion-service work

SocialGraph must expose a batch messaging-permission REST endpoint and a Fusion `User`
lookup (`id`, `name`, `avatar`, `isVerified`). Gateway must compose this source schema as
`Messaging`, advertise SSE support, forward trusted headers on the downstream SSE request,
and bypass its JSON response-buffer middleware for streaming responses.

The exact wire shapes and Gateway handoff are documented in
[`docs/INTEGRATION.md`](docs/INTEGRATION.md).
