using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using MessengerService.Application.Abstractions;
using MessengerService.Configuration;
using MessengerService.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace MessengerService.Infrastructure.Http;

public sealed class UploadMediaClient(
    HttpClient httpClient,
    IOptions<InternalServicesOptions> options,
    IHttpContextAccessor httpContextAccessor,
    ILogger<UploadMediaClient> logger) : IUploadMediaClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task FinalizeAsync(
        IReadOnlyCollection<string> urls,
        long? ownerUserId,
        CancellationToken cancellationToken) =>
        SendAsync("/internal/media/finalize", urls, ownerUserId, cancellationToken, requireCompleteFinalize: true);

    public Task DeleteAsync(
        IReadOnlyCollection<string> urls,
        long? ownerUserId,
        CancellationToken cancellationToken) =>
        SendAsync("/internal/media/delete", urls, ownerUserId, cancellationToken);

    private async Task SendAsync(
        string path,
        IReadOnlyCollection<string> urls,
        long? ownerUserId,
        CancellationToken cancellationToken,
        bool requireCompleteFinalize = false)
    {
        if (urls.Count == 0)
        {
            return;
        }

        var current = options.Value;
        if (!Uri.TryCreate(current.Upload.BaseUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps) ||
            !FixedTimeSecretComparer.IsStrongEnough(current.Upload.SharedSecret) ||
            current.TimeoutSeconds <= 0)
        {
            throw new InvalidOperationException("The Upload media client is not configured safely.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(current.TimeoutSeconds));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, path))
        {
            Content = JsonContent.Create(new MediaUrlsRequest(urls, ownerUserId), options: JsonOptions)
        };
        request.Headers.TryAddWithoutValidation(MessagingHeaders.UploadServiceSecret, current.Upload.SharedSecret);
        request.Headers.TryAddWithoutValidation(
            MessagingHeaders.CorrelationId,
            httpContextAccessor.HttpContext?.TraceIdentifier ??
            Activity.Current?.TraceId.ToString() ??
            Guid.NewGuid().ToString("N"));

        try
        {
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            response.EnsureSuccessStatusCode();
            if (requireCompleteFinalize)
            {
                using var document = JsonDocument.Parse(
                    await response.Content.ReadAsStreamAsync(timeout.Token),
                    new JsonDocumentOptions { MaxDepth = 4 });
                var expected = urls
                    .Where(url => !string.IsNullOrWhiteSpace(url))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                if (!document.RootElement.TryGetProperty("finalized", out var finalizedElement) ||
                    !finalizedElement.TryGetInt32(out var finalized) ||
                    finalized != expected)
                {
                    throw new HttpRequestException("Upload media finalize acknowledged an incomplete batch.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            logger.LogWarning(exception, "Upload media lifecycle request to {Path} failed.", path);
            throw;
        }
    }

    private sealed record MediaUrlsRequest(IReadOnlyCollection<string> Urls, long? OwnerUserId);
}
