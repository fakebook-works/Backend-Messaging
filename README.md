# Fakebook Messaging

`Messaging` is the .NET 10 Hot Chocolate subgraph that owns conversations, participants,
messages, reactions, receipts, typing and presence. The public API is GraphQL at
`/graphql`; SocialGraph provisions and removes local user projections through internal
REST endpoints.

Direct conversations can be created and used between any two active users unless either user
blocks the other. SocialGraph remains authoritative for that two-way block check. Creation is
idempotent for the normalized user pair and returns the existing conversation under both normal
and concurrent requests. Friendship is still required to view presence before the first shared
conversation and to add members to a Messenger group.

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
dotnet run --project .\MessengerService\MessengerService.csproj
dotnet test .\MessengerService.sln
```

Database migrations run automatically before the outbox and presence workers start. The
service uses EF migrations, keeps `__EFMigrationsHistory` in the `messenger` schema, and takes
a PostgreSQL session advisory lock on the same open connection for the whole migration. EF's
own migration lock remains in place so startup also coordinates with `dotnet ef database
update`. Any migration failure aborts startup.

`DatabaseMigrations:Enabled` defaults to `true`. Set it to `false` only when a deployment job
runs `dotnet ef database update --project .\MessengerService\MessengerService.csproj` before
starting the service. `ConnectionStrings:PostgreSQLMigration` is optional and falls back to
`ConnectionStrings:PostgreSQL`; shared deployments should use a DDL-capable migration role
and retain a least-privileged runtime role for the latter connection.

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

The transactional outbox also owns staged attachment lifecycle. Before a managed URL is
persisted, Messenger asks Upload Server over the signed internal API to verify and reserve
the trusted actor's ownership. Reusing a URL from another message is allowed only when that
canonical source is currently visible to the actor; Messenger resolves and verifies the
original Upload owner server-side instead of trusting a browser owner ID. A successful message commit attaches stable references for
each message/ordinal content slot and optional thumbnail slot; message deletion detaches
those exact references. Group avatars use one stable conversation reference, including
creation, replacement, removal and group deletion. The outbox includes the domain operation
time so late finalize/delete retries cannot overwrite a newer parent state. Legacy URL-only
outbox rows remain readable through Upload Server's conservative compatibility path, but new
domain writes never create them. Upload calls retry with exponential backoff, so closing a
browser immediately after send cannot leave a persisted message pointing at an expiring
pending asset. Unlike ordinary realtime invalidations, a well-formed media lifecycle row is
not permanently dead-lettered after ten transient failures: it retries on a capped schedule.
The dispatcher does not re-authorize a stale attach, because doing so could renew a reservation
after a newer detach; Upload applies operation-time tombstones atomically. A malformed
stored payload is still retired so one corrupt row cannot hot-loop or block unrelated events.
Exact media references are authorized before a message/avatar transaction commits. Upload must
acknowledge `exactReferences=true`, `lifecycleVersion>=3`, and the exact reference count; a
legacy response fails closed. If the parent transaction fails after authorization, Messenger
best-effort detaches only that attempt's references with the same DB operation timestamp.
Ownerless online repair is unsupported; legacy missing references require offline reconciliation.
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
Every managed message content/thumbnail parent is detached by its exact message/ordinal
reference, so a group administrator is never treated as the owner of another member's upload
and a shared URL cannot be deleted by a stale cascade.
Upload Server deletes the physical asset only after its final exact parent is gone. Updating
a managed group avatar authorizes and attaches the new asset under the acting administrator,
while the old conversation reference is detached without guessing its historical owner.

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
