using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using MessengerService.Application;
using MessengerService.Application.Abstractions;
using MessengerService.Configuration;
using MessengerService.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace MessengerService.Infrastructure.Http;

public sealed class SocialGraphPermissionClient(
    HttpClient httpClient,
    IOptions<InternalServicesOptions> options,
    IHttpContextAccessor httpContextAccessor,
    ILogger<SocialGraphPermissionClient> logger) : ISocialGraphPermissionClient
{
    private const string PermissionCheckPath = "/internal/messaging/permissions/check";
    private const long MaximumResponseBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<SocialGraphPermissionCheckResult> CheckAsync(
        long actorUserId,
        IReadOnlyCollection<long> targetUserIds,
        SocialGraphPermissionAction action,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(actorUserId);
        ArgumentNullException.ThrowIfNull(targetUserIds);

        var targets = targetUserIds.Distinct().ToArray();
        if (targets.Length == 0 || targets.Any(static userId => userId <= 0))
        {
            throw new ArgumentException(
                "At least one positive target user ID is required.",
                nameof(targetUserIds));
        }

        var currentOptions = options.Value;
        if (!TryBuildEndpoint(currentOptions.SocialGraph.BaseUrl, out var endpoint) ||
            !FixedTimeSecretComparer.IsStrongEnough(currentOptions.SocialGraph.SharedSecret) ||
            currentOptions.TimeoutSeconds <= 0)
        {
            logger.LogCritical("The SocialGraph permission client is not configured safely.");
            throw Unavailable();
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(currentOptions.TimeoutSeconds));

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(
                new PermissionCheckRequest(actorUserId, targets, ToWireAction(action)),
                options: SerializerOptions)
        };
        request.Headers.TryAddWithoutValidation(
            MessagingHeaders.SocialGraphServiceSecret,
            currentOptions.SocialGraph.SharedSecret);
        request.Headers.TryAddWithoutValidation(
            MessagingHeaders.CorrelationId,
            ResolveCorrelationId());

        try
        {
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutSource.Token);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "SocialGraph permission check returned HTTP {StatusCode}.",
                    (int)response.StatusCode);
                throw Unavailable();
            }

            if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            {
                logger.LogWarning("SocialGraph permission response exceeded the allowed size.");
                throw Unavailable();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutSource.Token);
            var payload = await JsonSerializer.DeserializeAsync<PermissionCheckResponse>(
                stream,
                SerializerOptions,
                timeoutSource.Token);

            return ValidateResponse(payload, targets);
        }
        catch (MessagingApplicationException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "SocialGraph permission check timed out after {TimeoutSeconds} seconds.",
                currentOptions.TimeoutSeconds);
            throw Unavailable();
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "SocialGraph permission check could not be completed.");
            throw Unavailable();
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "SocialGraph returned an invalid permission response.");
            throw Unavailable();
        }
    }

    private static SocialGraphPermissionCheckResult ValidateResponse(
        PermissionCheckResponse? response,
        IReadOnlyCollection<long> requestedTargets)
    {
        if (response?.Results is null || response.Results.Count != requestedTargets.Count)
        {
            throw Unavailable();
        }

        Dictionary<long, PermissionTargetResponse> byUserId;
        try
        {
            byUserId = response.Results.ToDictionary(static result => result.TargetUserId);
        }
        catch (ArgumentException)
        {
            throw Unavailable();
        }

        var decisions = new List<SocialGraphPermissionDecision>(requestedTargets.Count);
        foreach (var requestedTarget in requestedTargets)
        {
            if (!byUserId.TryGetValue(requestedTarget, out var result) ||
                result.Allowed is null ||
                result.IsFriend is null ||
                result.BlockedEitherDirection is null ||
                (result.Allowed.Value &&
                 (!result.IsFriend.Value || result.BlockedEitherDirection.Value)))
            {
                throw Unavailable();
            }

            decisions.Add(new SocialGraphPermissionDecision(
                requestedTarget,
                result.Allowed.Value,
                result.IsFriend.Value,
                result.BlockedEitherDirection.Value,
                result.Reason));
        }

        return new SocialGraphPermissionCheckResult(decisions);
    }

    private string ResolveCorrelationId() =>
        httpContextAccessor.HttpContext?.TraceIdentifier ??
        Activity.Current?.TraceId.ToString() ??
        Guid.NewGuid().ToString("N");

    private static bool TryBuildEndpoint(string baseUrl, out Uri endpoint)
    {
        endpoint = null!;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        endpoint = new Uri(baseUri, PermissionCheckPath);
        return true;
    }

    private static string ToWireAction(SocialGraphPermissionAction action) => action switch
    {
        SocialGraphPermissionAction.CreateDirect => "CREATE_DIRECT",
        SocialGraphPermissionAction.SendDirect => "SEND_DIRECT",
        SocialGraphPermissionAction.AddGroupMembers => "ADD_GROUP_MEMBERS",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
    };

    private static MessagingApplicationException Unavailable() =>
        new(
            MessagingErrorCodes.SocialGraphUnavailable,
            "SocialGraph permission checks are temporarily unavailable.");

    private sealed record PermissionCheckRequest(
        long ActorUserId,
        IReadOnlyCollection<long> TargetUserIds,
        string Action);

    private sealed record PermissionCheckResponse(
        List<PermissionTargetResponse>? Results);

    private sealed record PermissionTargetResponse(
        long TargetUserId,
        bool? Allowed,
        bool? IsFriend,
        bool? BlockedEitherDirection,
        string? Reason);
}
