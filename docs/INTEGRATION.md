# Messaging integration contracts

Messaging is complete in this repository, but end-to-end hydration and authorization need
the following companion-service changes.

## SocialGraph

### Permission check called by Messaging

```http
POST /internal/messaging/permissions/check
Content-Type: application/json
X-Internal-SocialGraphService-Secret: <SocialGraph service secret>
X-Correlation-ID: <correlation id>
```

```json
{
  "actorUserId": 1001,
  "targetUserIds": [1002, 1003],
  "action": "ADD_GROUP_MEMBERS"
}
```

Allowed action values are `CREATE_DIRECT`, `SEND_DIRECT`, `ADD_GROUP_MEMBERS`,
`VIEW_PRESENCE` and `INSPECT_BLOCK`.

- `CREATE_DIRECT` and `SEND_DIRECT` allow any existing non-self target when neither block
  direction exists; friendship remains response metadata rather than an authorization gate.
- `ADD_GROUP_MEMBERS` and `VIEW_PRESENCE` require a current friendship and no block.
- `INSPECT_BLOCK` is read-only directional state used to filter reads/realtime events and does
  not authorize a message operation.
The response must contain exactly one unique result for every requested target:

```json
{
  "results": [
    {
      "targetUserId": 1002,
      "allowed": true,
      "isFriend": true,
      "blockedEitherDirection": false,
      "reason": null
    }
  ]
}
```

Messaging validates the response strictly and fails closed with
`SOCIAL_GRAPH_UNAVAILABLE` on timeout, non-2xx status, missing/duplicate targets or an
inconsistent allowed decision.

### Fusion User entity

SocialGraph owns the fields used to hydrate Messaging references:

```graphql
type User @key(fields: "id") {
  id: Long!
  name: String!
  avatar: String!
  isVerified: Boolean!
}

type Query {
  userById(id: Long!): User @lookup
}
```

The lookup must be nullable at runtime. Messaging only contributes the key and exposes
the reference as `participant.user`, `message.sender` and `reaction.user`.

### User projection lifecycle

SocialGraph must deliver these calls at least once, ideally from a durable outbox:

```http
POST /internal/users
X-Internal-MessengerService-Secret: <secret>
Content-Type: application/json

{ "userId": 1001 }
```

```http
DELETE /internal/users/1001
X-Internal-MessengerService-Secret: <secret>
```

Create returns `201` on first provision, `200` when already active and `409` for a
terminal deleted ID. Delete always returns `204` after the ID is tombstoned.

## Gateway

Copy `MessengerService/schema.graphqls` and `schema-settings.json` into
`Gateway/schema/Messaging`, then compose `Messaging` into `gateway.far` with Nitro 16.1.3.

Gateway must continue using named client `fusion`, which injects `X-User-Id` and
`X-Gateway-Secret` into both regular requests and the outbound SSE handshake.

Client-to-Gateway subscriptions use:

```http
POST /graphql
Authorization: Bearer <access token>
Accept: text/event-stream
Content-Type: application/json
```

Fusion then consumes Messaging through `text/event-stream`. The current Gateway
`GraphQlCookieResponseMiddleware` must bypass response buffering before calling `next`
for SSE requests; checking the response content type after `next` is too late for a
long-lived stream.

Subscription events are at-least-once, unordered invalidation hints. Frontend consumers
must deduplicate `eventId`, reconcile messages by `sequence`, and refetch on a gap rather
than treating transport arrival order as canonical.

Required integration checks:

1. Browser-supplied trusted headers remain stripped before Gateway injection.
2. `X-User-Id` and `X-Gateway-Secret` reach the Messaging SSE handshake.
3. Session-invalid/revoked users cannot open new subscriptions.
4. An inbox or conversation event reaches the client without response buffering.

## Upload Server

Messenger uses the registered signed internal client for all three media operations:

- `POST /internal/media/authorize` with `ownerUserId`, exact stable `references`, and the
  database `operationAt` before persistence (for a visible forwarded attachment, candidate
  owners are resolved from canonical source messages and are never accepted from the browser).
  The response must include `exactReferences: true`, `lifecycleVersion >= 3`, and an exact
  `referenceCount`; otherwise Messenger fails closed so an older Upload cannot ignore the
  reference payload and return a legacy empty-URL success;
- `POST /internal/media/finalize` with `references`, `ownerUserId` and `operationAt` after commit;
- `POST /internal/media/delete` with the exact `references`, original owner when known, and
  `operationAt` when a message/avatar/group parent is removed.

Message content and thumbnail slots have separate stable reference IDs. Group avatar
replacement reuses the conversation avatar reference on the new URL and detaches it from the
old URL. A lifecycle batch is acknowledged only when Upload returns the complete
`finalized`/`detached` count; malformed or partial responses remain retryable outbox failures.
Old durable rows containing only `urls` are dispatched through Upload's legacy conservative
path for rolling-deployment compatibility and must not be produced by new mutations.
Reference attach retries remain scheduled with capped backoff beyond the ordinary
realtime-event retry ceiling. They do not call authorize again: the initial mutation already
reserved/verified ownership, while a retry must reach Upload's operation-time tombstone without
renewing a reservation after a newer detach. `next_attempt_at` keeps a failed row out of every
hot outbox batch.
If a parent transaction fails after authorization, Messenger sends a best-effort exact detach
for only that attempt's references using the same operation timestamp; Upload's bounded pending
reservation expiry is the fallback. Online ownerless repair is unsupported; missing legacy
references require offline reconciliation.
