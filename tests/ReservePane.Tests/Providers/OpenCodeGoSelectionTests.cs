using System.Net;
using System.Text;
using ReservePane.Core;
using ReservePane.Model;
using ReservePane.Providers;
using ReservePane.Tests.Support;

namespace ReservePane.Tests.Providers;

public sealed class OpenCodeGoSelectionTests : IDisposable
{
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.Parse("2026-09-12T10:00:00Z");
    private static readonly OpenCodeConsoleActiveWorkspace FirstWorkspace = new(
        "fixture-account", "fixture-access-token", "fixture-workspace", StartedAt.AddDays(1));
    private const string ApiCredential = """
        { "opencode-go": { "type": "api", "key": "fixture-api-key" } }
        """;

    private readonly TemporaryDirectory _directory = new();
    private readonly MutableTimeProvider _time = new(StartedAt);

    public OpenCodeGoSelectionTests() => File.WriteAllText(CredentialPath, "{}");

    private string CredentialPath => Path.Combine(_directory.Path, "opencode-auth.json");

    [Theory]
    [InlineData("same")]
    [InlineData("account")]
    [InlineData("workspace")]
    public async Task Cooldown_RevalidatesSelectionWithoutBypassingRetryAfter(string change)
    {
        // Break caught: retaining another selection's usage, or making a request before Retry-After expires.
        OpenCodeConsoleActiveWorkspace current = FirstWorkspace;
        var reader = new StubWorkspaceReader(() => Selected(current));
        var client = new StubConsoleClient((request, _) => request switch
        {
            1 => Usage(25),
            2 => RateLimited(),
            3 => Usage(75),
            _ => throw new InvalidOperationException("Unexpected Console request."),
        });
        StatusPoller poller = CreatePoller(reader, client);

        await PollAsync(poller);
        ProviderSnapshot rateLimited = await PollAsync(poller);
        current = change switch
        {
            "account" => FirstWorkspace with { AccountId = "fixture-next-account" },
            "workspace" => FirstWorkspace with { OrganizationId = "fixture-next-workspace" },
            _ => FirstWorkspace with { AccessToken = "fixture-refreshed-token" },
        };
        ProviderSnapshot duringCooldown = await PollAsync(poller);

        Assert.Equal(25, Assert.Single(rateLimited.Windows).Percent);
        Assert.Equal(2, client.RequestCount);
        if (change == "same")
        {
            Assert.Equal(rateLimited.Windows, duringCooldown.Windows);
            Assert.Equal(rateLimited.FetchedAt, duringCooldown.FetchedAt);
        }
        else
        {
            Assert.Empty(duringCooldown.Windows);
            Assert.Empty(duringCooldown.Info);
        }

        _time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        ProviderSnapshot recovered = await PollAsync(poller);

        Assert.Equal(75, Assert.Single(recovered.Windows).Percent);
        Assert.Equal(0, recovered.ConsecutiveFailures);
        Assert.Equal(3, client.RequestCount);
        Assert.Equal(current, client.LastWorkspace);
    }

    [Theory]
    [InlineData(false, "transient")]
    [InlineData(false, "invalid")]
    [InlineData(false, "exception")]
    [InlineData(true, "transient")]
    [InlineData(true, "invalid")]
    [InlineData(true, "exception")]
    public async Task DiscoveryFailure_ClearsPreviousUsageAndPreservesCooldown(
        bool duringCooldown,
        string failureKind)
    {
        // Break caught: unreadable selection data retains old usage or clears the server's cooldown.
        bool discoveryFails = false;
        var reader = new StubWorkspaceReader(() => !discoveryFails
            ? Selected(FirstWorkspace)
            : failureKind switch
            {
                "exception" => throw new IOException("Synthetic workspace discovery failure."),
                "invalid" => new(OpenCodeConsoleActiveWorkspaceReadOutcome.InvalidResponse),
                _ => new(OpenCodeConsoleActiveWorkspaceReadOutcome.TransientFailure),
            });
        var client = new StubConsoleClient((request, _) => request switch
        {
            1 => Usage(25),
            2 when duringCooldown => RateLimited(),
            _ => Usage(75),
        });
        StatusPoller poller = CreatePoller(reader, client);
        ProviderSnapshot first = await PollAsync(poller);
        if (duringCooldown)
        {
            await PollAsync(poller);
        }

        discoveryFails = true;
        ProviderSnapshot failed = await PollAsync(poller);

        Assert.Equal(25, Assert.Single(first.Windows).Percent);
        Assert.Empty(failed.Windows);
        Assert.Empty(failed.Info);
        Assert.Equal(1, failed.ConsecutiveFailures);
        Assert.Equal(duringCooldown ? 2 : 1, client.RequestCount);

        discoveryFails = false;
        if (duringCooldown)
        {
            ProviderSnapshot stillCoolingDown = await PollAsync(poller);
            Assert.Empty(stillCoolingDown.Windows);
            Assert.Equal(2, client.RequestCount);
            _time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        }

        ProviderSnapshot recovered = await PollAsync(poller);
        Assert.Equal(75, Assert.Single(recovered.Windows).Percent);
        Assert.Equal(0, recovered.ConsecutiveFailures);
        Assert.Equal(duringCooldown ? 3 : 2, client.RequestCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CredentialSourceChange_FailedRequestClearsUnrelatedUsageAndKeepsApiKeyPrecedence(
        bool startsWithApiKey,
        bool transportThrows)
    {
        // Break caught: a failed request keeps usage from the other credential source or falls back from an API key.
        File.WriteAllText(CredentialPath, startsWithApiKey ? ApiCredential : "{}");
        bool requestFails = false;
        string? authorizationScheme = null;
        string? authorizationParameter = null;
        using var handler = new StubHttpMessageHandler(request =>
        {
            authorizationScheme = request.Headers.Authorization?.Scheme;
            authorizationParameter = request.Headers.Authorization?.Parameter;
            if (requestFails)
            {
                return transportThrows
                    ? throw new HttpRequestException("Synthetic API request failure.")
                    : new HttpResponseMessage(HttpStatusCode.BadGateway);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    { "usage": { "rolling": { "percent": 25, "resetsAt": "2026-09-13T10:00:00Z" } } }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        var reader = new StubWorkspaceReader(() => Selected(FirstWorkspace));
        var client = new StubConsoleClient((_, _) => !requestFails
            ? Usage(25)
            : transportThrows
                ? throw new HttpRequestException("Synthetic Console request failure.")
                : new(OpenCodeConsoleFetchOutcome.TransientFailure, [], HttpStatusCode.BadGateway));
        StatusPoller poller = CreatePoller(reader, client, handler);

        ProviderSnapshot first = await PollAsync(poller);
        Assert.Equal(25, Assert.Single(first.Windows).Percent);
        Assert.Equal(startsWithApiKey ? 0 : 1, reader.ReadCount);
        Assert.Equal(startsWithApiKey ? 0 : 1, client.RequestCount);

        File.WriteAllText(CredentialPath, startsWithApiKey ? "{}" : ApiCredential);
        requestFails = true;
        _time.Advance(TimeSpan.FromMinutes(1));
        ProviderSnapshot failed = await PollAsync(poller);

        Assert.Empty(failed.Windows);
        Assert.Empty(failed.Info);
        Assert.Equal(1, failed.ConsecutiveFailures);
        Assert.NotEqual(first.FetchedAt, failed.FetchedAt);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(1, reader.ReadCount);
        Assert.Equal(1, client.RequestCount);
        Assert.Equal("Bearer", authorizationScheme);
        Assert.Equal("fixture-api-key", authorizationParameter);
    }

    public void Dispose() => _directory.Dispose();

    private StatusPoller CreatePoller(
        StubWorkspaceReader reader,
        StubConsoleClient client,
        HttpMessageHandler? handler = null)
    {
        var provider = new OpenCodeGoProvider(
            CredentialPath,
            handler ?? new StubHttpMessageHandler(_ => throw new InvalidOperationException("API HTTP was not expected.")),
            percent => SeverityPolicy.FromPercent(percent, 80, 95),
            _time,
            OpenCodeGoProvider.OpenCredentialStream,
            reader,
            client,
            File.Exists,
            command => command == "opencode");
        return new StatusPoller(
            [provider],
            () => AppSettings.Default,
            new RollingFileLog(Path.Combine(_directory.Path, "selection.log")),
            _time);
    }

    private static async Task<ProviderSnapshot> PollAsync(StatusPoller poller) =>
        Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);

    private static OpenCodeConsoleActiveWorkspaceReadResult Selected(OpenCodeConsoleActiveWorkspace workspace) =>
        new(OpenCodeConsoleActiveWorkspaceReadOutcome.Success, workspace);

    private static OpenCodeConsoleFetchResult Usage(double percent) => new(
        OpenCodeConsoleFetchOutcome.Success,
        [new UsageWindow("rolling", percent, StartedAt.AddDays(1), Severity.Normal)],
        HttpStatusCode.OK);

    private static OpenCodeConsoleFetchResult RateLimited() => new(
        OpenCodeConsoleFetchOutcome.RateLimited,
        [],
        HttpStatusCode.TooManyRequests,
        TimeSpan.FromMinutes(5));

    private sealed class StubWorkspaceReader(Func<OpenCodeConsoleActiveWorkspaceReadResult> read)
        : IOpenCodeConsoleActiveWorkspaceReader
    {
        public int ReadCount { get; private set; }

        public Task<OpenCodeConsoleActiveWorkspaceReadResult> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return Task.FromResult(read());
        }
    }

    private sealed class StubConsoleClient(Func<int, OpenCodeConsoleActiveWorkspace, OpenCodeConsoleFetchResult> fetch)
        : IOpenCodeConsoleGoClient
    {
        public int RequestCount { get; private set; }
        public OpenCodeConsoleActiveWorkspace? LastWorkspace { get; private set; }

        public Task<OpenCodeConsoleFetchResult> FetchAsync(
            OpenCodeConsoleActiveWorkspace workspace,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastWorkspace = workspace;
            return Task.FromResult(fetch(++RequestCount, workspace));
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
}
