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
- Outbound attachment lifecycle calls use `InternalServices:Upload:BaseUrl` and
  `InternalServices:Upload:SharedSecret` in `X-Internal-UploadService-Secret`.
- Secrets and the PostgreSQL connection string are required configuration and are never
  committed. Start from `.env.example` or use .NET User Secrets.
- Attachment bodies are not accepted. Messaging stores either safe same-origin
  `/media/files/{leaf}` URLs or absolute HTTPS URLs whose hosts are present in
  `Messaging:AllowedAttachmentHosts`.

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

The transactional outbox also owns staged attachment lifecycle. A successful message
commit queues `media.finalize.v1`; deleting the final non-deleted message that references
a URL queues `media.delete.v1`. Upload calls retry with exponential backoff, so closing a
browser immediately after send cannot leave a persisted message pointing at an expiring
pending asset. Frontend owner-finalization remains safe but is no longer the only guard.
When the outbox is empty, polling backs off from `Messaging:OutboxPollMilliseconds` to
`Messaging:OutboxMaxIdlePollMilliseconds` and resets after the next dispatched event,
reducing idle traffic to an external PostgreSQL server.

## Group administration

Group title/avatar updates, member additions/removals, role changes, leaving and deletion
are Gateway-only GraphQL mutations. The caller is always derived from the trusted Gateway
context. Current database membership is checked for every operation; title/avatar/member
changes require an active `Admin`, while a member can only leave as themselves. The final
administrator cannot leave or be demoted until another administrator is assigned.

`deleteGroupConversation` is restricted to a current administrator and only accepts a
`Group` conversation. Deletion removes the conversation and its cascaded messages,
participants, attachments and reactions in one transaction. Before the row is removed,
the service queues `CONVERSATION_DELETED` to each active participant's private inbox.
Managed message media with no surviving active reference is queued for deletion under
the original sender's identity, so a group administrator is never treated as the owner
of another member's upload and forwarded/shared media is not broken. Updating a managed
group avatar queues `media.finalize.v1` under the acting administrator's identity.

Group title/photo/member/role changes emit realtime invalidation events and append a
durable structured system message in the same transaction. The existing message sender is
the trusted actor; `kind`, `systemEvent` and the optional `systemSubjectUserId` tell clients
how to render the centered activity line. Browser inputs cannot set those fields, and
system messages cannot be edited, recalled, reacted to or replied to.

User-message edits are atomic and retain a bounded history of the ten newest prior
versions. The history is encoded in the existing message text column with a reserved,
versioned server-only envelope. GraphQL decodes it into the current `text` and
`editHistory`, and send/edit inputs cannot inject that reserved storage prefix. This keeps
reply previews and clients independent from the persistence format.

## Internal user lifecycle

```text
POST   /internal/users             { "userId": 123 }
DELETE /internal/users/{userId}
GET    /internal/users/{userId}/direct-contact-ids   -> { "userIds": [ ... ] }
```

Create is idempotent for an active user. Delete is terminal and idempotent; deleting an
unknown ID creates a tombstone so a delayed create event cannot reactivate it. Deletion
atomically marks presence offline, leaves active conversations, promotes the oldest
remaining group member when the final admin is removed, and wakes open SSE streams so
they can terminate after reauthorization. The read endpoint returns the distinct, sorted
IDs of active users who share a direct conversation with the requested active user.

## Companion-service integration

SocialGraph exposes a batch messaging-permission REST endpoint and a Fusion `User`
lookup (`id`, `name`, `avatar`, `isVerified`). Gateway composes this source schema as
`Messaging`, advertises SSE support, forwards trusted headers on the downstream SSE request,
and bypasses its JSON response-buffer middleware for streaming responses.

The exact wire shapes and Gateway handoff are documented in
[`docs/INTEGRATION.md`](docs/INTEGRATION.md).
