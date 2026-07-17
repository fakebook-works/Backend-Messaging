namespace MessengerService.Application.Abstractions;

public interface IUploadMediaClient
{
    Task FinalizeAsync(IReadOnlyCollection<string> urls, CancellationToken cancellationToken);

    Task DeleteAsync(IReadOnlyCollection<string> urls, CancellationToken cancellationToken);
}
