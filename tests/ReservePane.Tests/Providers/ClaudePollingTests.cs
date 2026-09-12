using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using ReservePane.Core;
using ReservePane.Model;
using ReservePane.Providers;
using ReservePane.Tests.Support;

namespace ReservePane.Tests.Providers;

public sealed class ClaudePollingTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();
    private readonly MutableTimeProvider _time = new(new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero));

    [Theory]
    [InlineData(HttpStatusCode.OK, HealthState.Ok)]
    [InlineData(HttpStatusCode.ServiceUnavailable, HealthState.Degraded)]
    public async Task PollOnceAsync_SpacesClaudeChecksWhileOtherProvidersContinue(
        HttpStatusCode profileStatus,
        HealthState expectedHealth)
    {
        // Catches normal or partial success resuming one-minute Claude requests or aging retained data as fresh.
        using var handler = new StubHttpMessageHandler(request => IsUsage(request)
            ? UsageResponse()
            : profileStatus == HttpStatusCode.OK ? ProfileResponse() : new HttpResponseMessage(profileStatus));
        ClaudeProvider claude = CreateProvider(handler);
        FakeStatusProvider other = FakeStatusProvider.Returning("other", FakeStatusProvider.Snapshot("other"));
        StatusPoller poller = CreatePoller(claude, other);

        ProviderSnapshot first = await PollClaudeAsync(poller);
        Assert.Equal(expectedHealth, first.Health);
        Assert.Equal(2, handler.RequestCount);

        // Immediate manual refresh and each normal one-minute poll share the same eligibility check.
        Assert.Same(first, await PollClaudeAsync(poller));
        for (int minute = 1; minute <= 4; minute++)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            Assert.Same(first, await PollClaudeAsync(poller));
        }

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(6, other.InvocationCount);
        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.Same(first, await PollClaudeAsync(poller));
        _time.Advance(TimeSpan.FromSeconds(1));
        ProviderSnapshot refreshed = await PollClaudeAsync(poller);

        Assert.Equal(_time.GetUtcNow(), refreshed.FetchedAt);
        Assert.Equal(expectedHealth, refreshed.Health);
        Assert.Equal(0, refreshed.ConsecutiveFailures);
        Assert.Equal(profileStatus == HttpStatusCode.OK ? 3 : 4, handler.RequestCount);
        Assert.Equal(8, other.InvocationCount);
    }

    [Theory]
    [InlineData(60, 300)]
    [InlineData(600, 600)]
    public async Task PollOnceAsync_RateLimitRecoveryKeepsMinimumSpacing(int retryAfterSeconds, int expectedDelaySeconds)
    {
        // Catches a short Retry-After bypassing pacing, a long one being shortened, or recovery returning to one minute.
        int usageRequests = 0;
        using var handler = new StubHttpMessageHandler(request =>
        {
            if (!IsUsage(request)) return ProfileResponse();
            if (++usageRequests != 2) return UsageResponse();
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.Add("Retry-After", retryAfterSeconds.ToString(CultureInfo.InvariantCulture));
            return response;
        });
        StatusPoller poller = CreatePoller(CreateProvider(handler));
        ProviderSnapshot first = await PollClaudeAsync(poller);
        _time.Advance(TimeSpan.FromMinutes(5));
        ProviderSnapshot limited = await PollClaudeAsync(poller);

        Assert.StartsWith("Rate limited.", limited.Error);
        Assert.Equal(first.FetchedAt, limited.FetchedAt);
        Assert.Equal(1, limited.ConsecutiveFailures);
        Assert.Contains(
            $"cooldown-seconds={expectedDelaySeconds}",
            File.ReadAllText(Path.Combine(_directory.Path, "poller.log")));
        _time.Advance(TimeSpan.FromSeconds(expectedDelaySeconds - 1));
        Assert.Same(limited, await PollClaudeAsync(poller));
        Assert.Equal(2, usageRequests);
        _time.Advance(TimeSpan.FromSeconds(1));
        ProviderSnapshot recovered = await PollClaudeAsync(poller);

        Assert.Null(recovered.Error);
        Assert.Equal(0, recovered.ConsecutiveFailures);
        Assert.Equal(3, usageRequests);
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Same(recovered, await PollClaudeAsync(poller));
        Assert.Equal(3, usageRequests);
        _time.Advance(TimeSpan.FromMinutes(4));
        Assert.Null((await PollClaudeAsync(poller)).Error);
        Assert.Equal(4, usageRequests);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("invalid-json")]
    [InlineData("exception")]
    public async Task PollOnceAsync_FailedClaudeChecksRemainSpaced(string failure)
    {
        // Catches failure handling clearing the minimum interval and immediately retrying Claude.
        using var handler = new StubHttpMessageHandler(_ => failure switch
        {
            "http" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            "invalid-json" => JsonResponse("{"),
            _ => throw new HttpRequestException("synthetic connection failure"),
        });
        StatusPoller poller = CreatePoller(CreateProvider(handler));
        ProviderSnapshot failed = await PollClaudeAsync(poller);
        _time.Advance(TimeSpan.FromMinutes(1));

        Assert.NotNull(failed.Error);
        Assert.Equal(1, failed.ConsecutiveFailures);
        Assert.Same(failed, await PollClaudeAsync(poller));
        Assert.Equal(1, handler.RequestCount);
        _time.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(2, (await PollClaudeAsync(poller)).ConsecutiveFailures);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task PollOnceAsync_ClaudeAvailabilityChangesAreStillDiscoveredDuringMinimumInterval()
    {
        // Catches minimum spacing bypassing local discovery or hiding credentials reintroduced after removal.
        using var handler = new StubHttpMessageHandler(request => IsUsage(request) ? UsageResponse() : ProfileResponse());
        ClaudeProvider provider = CreateProvider(handler);
        StatusPoller poller = CreatePoller(provider);
        await PollClaudeAsync(poller);
        File.Delete(Path.Combine(_directory.Path, "claude-credentials.json"));

        Assert.Empty((await poller.PollOnceAsync(CancellationToken.None)).Providers);
        Assert.Equal(2, handler.RequestCount);
        WriteCredential();

        Assert.Null((await PollClaudeAsync(poller)).Error);
        Assert.Equal(3, handler.RequestCount);
    }

    public void Dispose() => _directory.Dispose();

    private ClaudeProvider CreateProvider(HttpMessageHandler handler) => new(
        WriteCredential(), handler, percent => SeverityPolicy.FromPercent(percent, 80, 95), _time);

    private string WriteCredential() => _directory.WriteFile("claude-credentials.json", JsonSerializer.Serialize(new
    {
        claudeAiOauth = new
        {
            accessToken = "unit-test-access-token",
            expiresAt = _time.GetUtcNow().AddDays(1).ToUnixTimeMilliseconds(),
        },
    }));

    private StatusPoller CreatePoller(params IStatusProvider[] providers) => new(
        providers,
        () => AppSettings.Default,
        new RollingFileLog(Path.Combine(_directory.Path, "poller.log")),
        _time);

    private static async Task<ProviderSnapshot> PollClaudeAsync(StatusPoller poller) =>
        Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers, snapshot => snapshot.Id == "claude");

    private static bool IsUsage(HttpRequestMessage request) => request.RequestUri!.AbsolutePath == "/api/oauth/usage";

    private static HttpResponseMessage UsageResponse() => JsonResponse(
        """{"limits":[{"group":"five_hour","percent":25,"resets_at":"2026-09-12T17:00:00Z"}]}""");

    private static HttpResponseMessage ProfileResponse() => JsonResponse("""{"organization":{"seat_tier":"team_standard"}}""");

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }
}
