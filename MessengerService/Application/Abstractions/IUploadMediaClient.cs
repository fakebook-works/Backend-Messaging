namespace MessengerService.Application.Abstractions;

public sealed record UploadMediaReference(string Url, string ReferenceId);
public sealed record UploadMediaAuthorizationResult(
    bool Authorized,
    IReadOnlySet<string> UnauthorizedUrls);

public interface IUploadMediaClient
{
    Task<UploadMediaAuthorizationResult> AuthorizeOwnedAsync(
        IReadOnlyCollection<string> urls,
        long ownerUserId,
        CancellationToken cancellationToken);

    Task<UploadMediaAuthorizationResult> AuthorizeReferencesAsync(
        IReadOnlyCollection<UploadMediaReference> references,
        long ownerUserId,
        DateTimeOffset operationAt,
        CancellationToken cancellationToken);

    Task FinalizeReferencesAsync(
        IReadOnlyCollection<UploadMediaReference> references,
        long? ownerUserId,
        DateTimeOffset operationAt,
        CancellationToken cancellationToken);

    Task DeleteReferencesAsync(
        IReadOnlyCollection<UploadMediaReference> references,
        long? ownerUserId,
        DateTimeOffset operationAt,
        CancellationToken cancellationToken);

    // Compatibility methods for outbox rows created before reference-based media
    // lifecycle was introduced. New domain writes must use the methods above.
    Task FinalizeAsync(
        IReadOnlyCollection<string> urls,
        long? ownerUserId,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        IReadOnlyCollection<string> urls,
        long? ownerUserId,
        CancellationToken cancellationToken);
}
