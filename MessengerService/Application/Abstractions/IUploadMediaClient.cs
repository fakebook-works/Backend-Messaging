namespace MessengerService.Application.Abstractions;

public interface IUploadMediaClient
{
    Task FinalizeAsync(
        IReadOnlyCollection<string> urls,
        long? ownerUserId,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        IReadOnlyCollection<string> urls,
        long? ownerUserId,
        CancellationToken cancellationToken);
}
