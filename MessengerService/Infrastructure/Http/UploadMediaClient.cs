using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using MessengerService.Application.Abstractions;
using MessengerService.Configuration;
using MessengerService.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace MessengerService.Infrastructure.Http;

public sealed class UploadMediaPermanentException(string message) : HttpRequestException(message);

public sealed class UploadMediaClient(
    HttpClient httpClient,
    IOptions<InternalServicesOptions> options,
    IHttpContextAccessor httpContextAccessor,
    ILogger<UploadMediaClient> logger) : IUploadMediaClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<UploadMediaAuthorizationResult> AuthorizeOwnedAsync(
        IReadOnlyCollection<string> urls,
        long ownerUserId,
        CancellationToken cancellationToken)
    {
        if (urls.Count == 0)
        {
            return new UploadMediaAuthorizationResult(
                true,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        using var response = await SendRequestAsync(
            "/internal/media/authorize",
            new MediaUrlsRequest(urls, ownerUserId),
            cancellationToken);
        using var document = await ReadResponseAsync(response, cancellationToken);
        if (!document.RootElement.TryGetProperty("authorized", out var authorizedElement) ||
            authorizedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !document.RootElement.TryGetProperty("unauthorizedUrls", out var unauthorizedElement) ||
            unauthorizedElement.ValueKind != JsonValueKind.Array)
        {
            throw new HttpRequestException("Upload media authorization returned a malformed response.");
        }

        var requested = urls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unauthorized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in unauthorizedElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(element.GetString()) ||
                !requested.Contains(element.GetString()!))
            {
                throw new HttpRequestException("Upload media authorization returned an invalid URL set.");
            }
            unauthorized.Add(element.GetString()!);
        }

        var authorized = authorizedElement.GetBoolean();
        if (authorized != (unauthorized.Count == 0))
        {
            throw new HttpRequestException("Upload media authorization returned an inconsistent decision.");
        }

        return new UploadMediaAuthorizationResult(authorized, unauthorized);
    }

    public async Task<UploadMediaAuthorizationResult> AuthorizeReferencesAsync(
        IReadOnlyCollection<UploadMediaReference> references,
        long ownerUserId,
        DateTimeOffset operationAt,
        CancellationToken cancellationToken)
    {
        if (references.Count == 0)
        {
            return new UploadMediaAuthorizationResult(
                true,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        using var response = await SendRequestAsync(
            "/internal/media/authorize",
            new MediaUrlsRequest(
                OwnerUserId: ownerUserId,
                References: references,
                OperationAt: operationAt),
            cancellationToken);
        return await ReadAuthorizationResultAsync(
            response,
            references.Select(reference => reference.Url),
            references.Count,
            cancellationToken);
    }

    public Task FinalizeReferencesAsync(
        IReadOnlyCollection<UploadMediaReference> references,
        long? ownerUserId,
        DateTimeOffset operationAt,
        CancellationToken cancellationToken) =>
        SendReferenceLifecycleAsync(
            "/internal/media/finalize",
            "finalized",
            references,
            ownerUserId,
            operationAt,
            cancellationToken);

    public Task DeleteReferencesAsync(
        IReadOnlyCollection<UploadMediaReference> references,
        long? ownerUserId,
        DateTimeOffset operationAt,
        CancellationToken cancellationToken) =>
        SendReferenceLifecycleAsync(
            "/internal/media/delete",
            "detached",
            references,
            ownerUserId,
            operationAt,
            cancellationToken);

    private static async Task<UploadMediaAuthorizationResult> ReadAuthorizationResultAsync(
        HttpResponseMessage response,
        IEnumerable<string> requestedUrls,
        int expectedReferenceCount,
        CancellationToken cancellationToken)
    {
        using var document = await ReadResponseAsync(response, cancellationToken);
        if (!document.RootElement.TryGetProperty("authorized", out var authorizedElement) ||
            authorizedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !document.RootElement.TryGetProperty("unauthorizedUrls", out var unauthorizedElement) ||
            unauthorizedElement.ValueKind != JsonValueKind.Array)
        {
            throw new HttpRequestException("Upload media authorization returned a malformed response.");
        }
        if (!document.RootElement.TryGetProperty("exactReferences", out var exactElement) ||
            exactElement.ValueKind != JsonValueKind.True ||
            !document.RootElement.TryGetProperty("lifecycleVersion", out var versionElement) ||
            !versionElement.TryGetInt32(out var lifecycleVersion) ||
            lifecycleVersion < 3 ||
            !document.RootElement.TryGetProperty("referenceCount", out var countElement) ||
            !countElement.TryGetInt32(out var referenceCount) ||
            referenceCount != expectedReferenceCount)
        {
            // An older Upload deployment may ignore the new JSON fields and answer the
            // legacy empty-URL authorization with `authorized: true`. Treat the missing
            // exact-protocol acknowledgement as unavailable and fail before parent commit.
            throw new HttpRequestException("Upload media authorization does not support exact references.");
        }

        var requested = requestedUrls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unauthorized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in unauthorizedElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(element.GetString()) ||
                !requested.Contains(element.GetString()!))
            {
                throw new HttpRequestException("Upload media authorization returned an invalid URL set.");
            }
            unauthorized.Add(element.GetString()!);
        }

        var authorized = authorizedElement.GetBoolean();
        if (authorized != (unauthorized.Count == 0))
        {
            throw new HttpRequestException("Upload media authorization returned an inconsistent decision.");
        }

        return new UploadMediaAuthorizationResult(authorized, unauthorized);
    }

    public Task FinalizeAsync(
        IReadOnlyCollection<string> urls,
        long? ownerUserId,
        CancellationToken cancellationToken) =>
        SendLegacyLifecycleAsync(
            "/internal/media/finalize",
            urls,
            ownerUserId,
            cancellationToken,
            requireCompleteFinalize: true);

    public Task DeleteAsync(
        IReadOnlyCollection<string> urls,
        long? ownerUserId,
        CancellationToken cancellationToken) =>
        SendLegacyLifecycleAsync(
            "/internal/media/delete",
            urls,
            ownerUserId,
            cancellationToken,
            requireCompleteDelete: true);

    private async Task SendReferenceLifecycleAsync(
        string path,
        string acknowledgedProperty,
        IReadOnlyCollection<UploadMediaReference> references,
        long? ownerUserId,
        DateTimeOffset operationAt,
        CancellationToken cancellationToken)
    {
        if (references.Count == 0)
        {
            return;
        }

        using var response = await SendRequestAsync(
            path,
            new MediaUrlsRequest(
                OwnerUserId: ownerUserId,
                References: references,
                OperationAt: operationAt),
            cancellationToken);
        using var document = await ReadResponseAsync(response, cancellationToken);
        var expected = references
            .DistinctBy(
                reference => reference.Url.ToUpperInvariant() + "\n" + reference.ReferenceId,
                StringComparer.Ordinal)
            .Count();
        if (!document.RootElement.TryGetProperty(acknowledgedProperty, out var countElement) ||
            !countElement.TryGetInt32(out var acknowledged) ||
            acknowledged != expected)
        {
            throw new HttpRequestException("Upload media lifecycle acknowledged an incomplete reference batch.");
        }
    }

    private async Task SendLegacyLifecycleAsync(
        string path,
        IReadOnlyCollection<string> urls,
        long? ownerUserId,
        CancellationToken cancellationToken,
        bool requireCompleteFinalize = false,
        bool requireCompleteDelete = false)
    {
        if (urls.Count == 0)
        {
            return;
        }

        using var response = await SendRequestAsync(
            path,
            new MediaUrlsRequest(urls, ownerUserId),
            cancellationToken);
        if (!requireCompleteFinalize && !requireCompleteDelete)
        {
            return;
        }

        using var document = await ReadResponseAsync(response, cancellationToken);
        var expected = urls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var acknowledgement = requireCompleteFinalize ? "finalized" : "scheduled";
        if (!document.RootElement.TryGetProperty(acknowledgement, out var countElement) ||
            !countElement.TryGetInt32(out var acknowledged) ||
            acknowledged != expected)
        {
            throw new HttpRequestException(
                $"Upload media legacy {acknowledgement} acknowledged an incomplete batch.");
        }
    }

    private async Task<HttpResponseMessage> SendRequestAsync(
        string path,
        MediaUrlsRequest body,
        CancellationToken cancellationToken)
    {
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
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        request.Headers.TryAddWithoutValidation(MessagingHeaders.UploadServiceSecret, current.Upload.SharedSecret);
        request.Headers.TryAddWithoutValidation(
            MessagingHeaders.CorrelationId,
            httpContextAccessor.HttpContext?.TraceIdentifier ??
            Activity.Current?.TraceId.ToString() ??
            Guid.NewGuid().ToString("N"));

        try
        {
            var response = await httpClient.SendAsync(
                request,
                // Buffer the tiny signed lifecycle response before the linked timeout
                // is disposed. Returning a ResponseHeadersRead result here would leave
                // body parsing outside the configured dependency deadline.
                HttpCompletionOption.ResponseContentRead,
                timeout.Token);
            try
            {
                if (response.StatusCode is System.Net.HttpStatusCode.BadRequest or
                    System.Net.HttpStatusCode.UnprocessableEntity ||
                    response.StatusCode == System.Net.HttpStatusCode.Conflict &&
                    body.References is { Count: > 0 })
                {
                    throw new UploadMediaPermanentException(
                        "Upload media lifecycle rejected the signed request.");
                }
                response.EnsureSuccessStatusCode();
            }
            catch
            {
                response.Dispose();
                throw;
            }
            return response;
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

    private static async Task<JsonDocument> ReadResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                new JsonDocumentOptions { MaxDepth = 4 },
                cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new HttpRequestException("Upload media lifecycle returned malformed JSON.", exception);
        }
    }

    private sealed record MediaUrlsRequest(
        IReadOnlyCollection<string>? Urls = null,
        long? OwnerUserId = null,
        IReadOnlyCollection<UploadMediaReference>? References = null,
        DateTimeOffset? OperationAt = null);
}
