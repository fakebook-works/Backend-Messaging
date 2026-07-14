using System.Net;
using MessengerService.Contracts.Internal;

namespace MessengerService.Tests;

internal sealed class FakeProvisioningService : IMessagingUserProvisioningService
{
    public ProvisionUserOutcome ProvisionOutcome { get; set; } = ProvisionUserOutcome.Created;

    public bool IsActive { get; set; } = true;

    public Exception? IsActiveException { get; set; }

    public List<long> ProvisionedUserIds { get; } = [];

    public List<long> TombstonedUserIds { get; } = [];

    public List<long> ActiveChecks { get; } = [];

    public Task<ProvisionUserOutcome> ProvisionAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        ProvisionedUserIds.Add(userId);
        return Task.FromResult(ProvisionOutcome);
    }

    public Task TombstoneAsync(long userId, CancellationToken cancellationToken = default)
    {
        TombstonedUserIds.Add(userId);
        return Task.CompletedTask;
    }

    public Task<bool> IsActiveAsync(long userId, CancellationToken cancellationToken = default)
    {
        ActiveChecks.Add(userId);
        return IsActiveException is null
            ? Task.FromResult(IsActive)
            : Task.FromException<bool>(IsActiveException);
    }
}

internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _response;

    public StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response)
    {
        _response = response;
    }

    public StubHttpMessageHandler(HttpStatusCode statusCode, string json)
        : this((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        }))
    {
    }

    public HttpMethod? LastMethod { get; private set; }

    public Uri? LastRequestUri { get; private set; }

    public string? LastBody { get; private set; }

    public IReadOnlyDictionary<string, string[]> LastHeaders { get; private set; } =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastMethod = request.Method;
        LastRequestUri = request.RequestUri;
        LastBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        LastHeaders = request.Headers.ToDictionary(
            header => header.Key,
            header => header.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);

        return await _response(request, cancellationToken);
    }
}
