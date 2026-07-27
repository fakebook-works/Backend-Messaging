# Messenger service agent rules

When embedded in the Fakebook workspace, also read the root API security contract.

- Derive the actor from ITrustedUserContextAccessor; never authorize from input senderId.
- Check current conversation membership and SocialGraph friend/block permission for every
  operation that needs it.
- A message/media/reaction/reply/pin/read operation must be scoped to its conversation and actor.
- Internal APIs require signed HMAC requests and Redis nonce replay protection.
- Upload media lifecycle calls remain owner-scoped and signed.
- Outbox dispatch releases DB locks before network calls and uses bounded retry/dead-letter.
- Subscription filters must not leak another conversation; close streams on cancellation.
- Runtime DB access uses the messenger-scoped role; parameterize SQL and bound pages.
- Do not record message/attachment bodies in logs or telemetry.

Run dotnet test MessengerService.sln and add wrong-member/block/spoof/replay tests.
