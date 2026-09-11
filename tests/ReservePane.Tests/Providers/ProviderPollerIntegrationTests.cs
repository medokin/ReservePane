using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;
using ReservePane.Core;
using ReservePane.Model;
using ReservePane.Providers;
using ReservePane.Tests.Support;

namespace ReservePane.Tests.Providers;

public sealed class ProviderPollerIntegrationTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    [Fact]
    public async Task ClaudeOperationalFailuresRetainLastGoodDataUntilRecovery()
    {
        // Break caught: Claude converts transport failures into valid empty snapshots that reset poller failures.
        int request = 0;
        var handler = new StubHttpMessageHandler(message => Interlocked.Increment(ref request) switch
        {
            1 => JsonResponse(ReadFixture("claude-usage.json")),
            2 => JsonResponse(ReadFixture("claude-profile.json")),
            3 or 4 or 5 => throw new HttpRequestException("synthetic transport failure"),
            6 => JsonResponse(ReadFixture("claude-usage.json")),
            _ => throw new InvalidOperationException("Unexpected HTTP request."),
        });
        string credentialPath = _directory.WriteFile(
            "claude-credentials.json",
            JsonSerializer.Serialize(new
            {
                claudeAiOauth = new
                {
                    accessToken = "unit-test-access-token",
                    expiresAt = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds(),
                },
            }));
        var provider = new ClaudeProvider(credentialPath, handler, SeverityFromPercent);
        StatusPoller poller = CreatePoller(provider);

        ProviderSnapshot good = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);
        ProviderSnapshot first = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);
        ProviderSnapshot second = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);
        ProviderSnapshot third = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);
        ProviderSnapshot recovered = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);

        AssertRetained(good, first, HealthState.Ok, 1);
        AssertRetained(good, second, HealthState.Ok, 2);
        AssertRetained(good, third, HealthState.Degraded, 3);
        Assert.Equal(good.PlanLabel, recovered.PlanLabel);
        Assert.True(good.Windows.SequenceEqual(recovered.Windows));
        Assert.True(good.Info.SequenceEqual(recovered.Info));
        Assert.Equal(HealthState.Ok, recovered.Health);
        Assert.Equal(0, recovered.ConsecutiveFailures);
    }

    [Fact]
    public async Task CodexOperationalFailuresRetainLastGoodDataUntilRecovery()
    {
        // Break caught: Codex converts transport failures into valid empty snapshots that reset poller failures.
        int request = 0;
        var handler = new StubHttpMessageHandler(_ => Interlocked.Increment(ref request) switch
        {
            1 => JsonResponse(ReadFixture("codex-wham.json")),
            2 or 3 or 4 => throw new HttpRequestException("synthetic transport failure"),
            5 => JsonResponse(ReadFixture("codex-wham.json")),
            _ => throw new InvalidOperationException("Unexpected HTTP request."),
        });
        string credentialPath = _directory.WriteFile(
            "codex-auth.json",
            """
            {"tokens":{"access_token":"unit-test-access-token","account_id":"unit-test-account-id"}}
            """);
        var provider = new CodexProvider(credentialPath, handler, SeverityFromPercent);
        StatusPoller poller = CreatePoller(provider);

        ProviderSnapshot good = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);
        ProviderSnapshot first = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);
        ProviderSnapshot second = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);
        ProviderSnapshot third = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);
        ProviderSnapshot recovered = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);

        AssertRetained(good, first, HealthState.Ok, 1);
        AssertRetained(good, second, HealthState.Ok, 2);
        AssertRetained(good, third, HealthState.Degraded, 3);
        Assert.Equal(good.PlanLabel, recovered.PlanLabel);
        Assert.True(good.Windows.SequenceEqual(recovered.Windows));
        Assert.True(good.Info.SequenceEqual(recovered.Info));
        Assert.Equal(HealthState.Ok, recovered.Health);
        Assert.Equal(0, recovered.ConsecutiveFailures);
    }

    [Fact]
    public async Task ClaudeCredentialRemovalOmitsProviderWithoutAnotherFetch()
    {
        // Break caught: a provider whose local credential disappears remains visible or performs a fetch.
        var handler = new StubHttpMessageHandler(message =>
            JsonResponse(message.RequestUri!.AbsolutePath.EndsWith("profile", StringComparison.Ordinal)
                ? ReadFixture("claude-profile.json")
                : ReadFixture("claude-usage.json")));
        string credentialPath = _directory.WriteFile(
            "removed-claude-credentials.json",
            CreateClaudeCredential());
        var provider = new ClaudeProvider(credentialPath, handler, SeverityFromPercent);
        StatusPoller poller = CreatePoller(provider);

        ProviderSnapshot present = Assert.Single(
            (await poller.PollOnceAsync(CancellationToken.None)).Providers);
        int requestsBeforeRemoval = handler.RequestCount;
        File.Delete(credentialPath);
        StatusReport removed = await poller.PollOnceAsync(CancellationToken.None);

        Assert.Equal("claude", present.Id);
        Assert.Empty(removed.Providers);
        Assert.Equal(requestsBeforeRemoval, handler.RequestCount);
    }

    [Fact]
    public async Task OpenCodeGoOperationalFailuresRetainLastGoodDataUntilRecovery()
    {
        // Break caught: an OpenCode Go outage erases account-wide windows instead of using poller retention.
        int request = 0;
        var handler = new StubHttpMessageHandler(_ => Interlocked.Increment(ref request) switch
        {
            1 => JsonResponse(ReadFixture("opencode-go-usage.json")),
            2 or 3 or 4 => throw new HttpRequestException("synthetic transport failure"),
            5 => JsonResponse(ReadFixture("opencode-go-usage.json")),
            _ => throw new InvalidOperationException("Unexpected HTTP request."),
        });
        string credentialPath = _directory.WriteFile(
            "opencode-auth.json",
            """
            {"opencode-go":{"type":"api","key":"unit-test-api-key"}}
            """);
        var provider = new OpenCodeGoProvider(credentialPath, handler, SeverityFromPercent);
        StatusPoller poller = CreatePoller(provider);

        ProviderSnapshot good = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);
        ProviderSnapshot first = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);
        ProviderSnapshot second = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);
        ProviderSnapshot third = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);
        ProviderSnapshot recovered = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);

        AssertRetained(good, first, HealthState.Ok, 1);
        AssertRetained(good, second, HealthState.Ok, 2);
        AssertRetained(good, third, HealthState.Degraded, 3);
        Assert.True(good.Windows.SequenceEqual(recovered.Windows));
        Assert.Equal(HealthState.Ok, recovered.Health);
        Assert.Equal(0, recovered.ConsecutiveFailures);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task OpenCodeCompanySeatSelectionChangeDoesNotRetainPreviousWorkspaceData(
        bool changeAccount,
        bool changeWorkspace)
    {
        // Catches a failed first request for a new active workspace showing the previous member's budget.
        DateTimeOffset reset = DateTimeOffset.UtcNow.AddDays(20);
        OpenCodeConsoleActiveWorkspace firstWorkspace = new(
            "account-first",
            "access-first",
            "org-first",
            DateTimeOffset.UtcNow.AddHours(1));
        OpenCodeConsoleActiveWorkspace secondWorkspace = new(
            changeAccount ? "account-second" : "account-first",
            "access-second",
            changeWorkspace ? "org-second" : "org-first",
            DateTimeOffset.UtcNow.AddHours(1));
        var provider = new OpenCodeCompanySeatProvider(
            new StubHttpMessageHandler(_ => throw new InvalidOperationException("HTTP was not expected.")),
            SeverityFromPercent,
            null,
            new SequencedWorkspaceReader(firstWorkspace, secondWorkspace),
            new SequencedCompanySeatClient(
                new OpenCodeCompanySeatFetchResult(
                    OpenCodeCompanySeatFetchOutcome.Success,
                    new OpenCodeCompanySeatBudget(
                        new BigInteger(1_000_000_000),
                        new BigInteger(250_000_000),
                        false,
                        reset,
                        "default")),
                new OpenCodeCompanySeatFetchResult(
                    OpenCodeCompanySeatFetchOutcome.InvalidResponse,
                    StatusCode: HttpStatusCode.OK)),
            IsOpenCodeCommandAvailable);
        AppSettings settings = AppSettings.Default with
        {
            Providers = AppSettings.Default.Providers.SetItem(
                provider.Id,
                new ProviderSettings()),
        };
        var poller = new StatusPoller(
            [provider],
            () => settings,
            new RollingFileLog(Path.Combine(_directory.Path, "company-seat-selection.log")));

        ProviderSnapshot first = Assert.Single(
            (await poller.PollOnceAsync(CancellationToken.None)).Providers);
        ProviderSnapshot changed = Assert.Single(
            (await poller.PollOnceAsync(CancellationToken.None)).Providers);

        Assert.NotEmpty(first.Windows);
        Assert.Empty(changed.Windows);
        Assert.Empty(changed.Info);
        Assert.Equal(1, changed.ConsecutiveFailures);
        Assert.NotEqual(first.FetchedAt, changed.FetchedAt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OpenCodeCompanySeatTransportFailureRetainsOnlyTheSameWorkspaceData(
        bool selectionChanges)
    {
        OpenCodeConsoleActiveWorkspace firstWorkspace = Workspace("account-first", "org-first");
        OpenCodeConsoleActiveWorkspace nextWorkspace = selectionChanges
            ? Workspace("account-second", "org-second")
            : firstWorkspace with { AccessToken = "access-refreshed" };
        int request = 0;
        var provider = new OpenCodeCompanySeatProvider(
            new StubHttpMessageHandler(_ => throw new InvalidOperationException("HTTP was not expected.")),
            SeverityFromPercent,
            null,
            new SequencedWorkspaceReader(firstWorkspace, nextWorkspace),
            new DelegatingCompanySeatClient((_, _, _) => Interlocked.Increment(ref request) switch
            {
                1 => Task.FromResult(SuccessfulBudget(25)),
                2 => throw new HttpRequestException("synthetic transport failure"),
                _ => throw new InvalidOperationException("Unexpected client request."),
            }),
            IsOpenCodeCommandAvailable);
        StatusPoller poller = CreateCompanySeatPoller(provider);

        ProviderSnapshot first = Assert.Single(
            (await poller.PollOnceAsync(CancellationToken.None)).Providers);
        ProviderSnapshot failed = Assert.Single(
            (await poller.PollOnceAsync(CancellationToken.None)).Providers);

        if (selectionChanges)
        {
            Assert.Empty(failed.Windows);
            Assert.Empty(failed.Info);
            Assert.Equal(1, failed.ConsecutiveFailures);
        }
        else
        {
            AssertRetained(first, failed, HealthState.Ok, 1);
        }
    }

    [Fact]
    public async Task OpenCodeCompanySeatTimeoutAfterSelectionChangeClearsPreviousWorkspaceData()
    {
        int request = 0;
        var provider = new OpenCodeCompanySeatProvider(
            new StubHttpMessageHandler(_ => throw new InvalidOperationException("HTTP was not expected.")),
            SeverityFromPercent,
            null,
            new SequencedWorkspaceReader(
                Workspace("account-first", "org-first"),
                Workspace("account-second", "org-second")),
            new DelegatingCompanySeatClient(async (_, _, cancellationToken) =>
            {
                if (Interlocked.Increment(ref request) == 1)
                {
                    return SuccessfulBudget(25);
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable.");
            }),
            IsOpenCodeCommandAvailable);
        StatusPoller poller = CreateCompanySeatPoller(provider, TimeSpan.FromMilliseconds(50));

        ProviderSnapshot first = Assert.Single(
            (await poller.PollOnceAsync(CancellationToken.None)).Providers);
        ProviderSnapshot timedOut = Assert.Single(
            (await poller.PollOnceAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2))).Providers);

        Assert.NotEmpty(first.Windows);
        Assert.Empty(timedOut.Windows);
        Assert.Empty(timedOut.Info);
        Assert.Equal(1, timedOut.ConsecutiveFailures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenCodeCompanySeatDiscoveryFailureClearsPreviousWorkspaceData(
        bool invalid)
    {
        var provider = new OpenCodeCompanySeatProvider(
            new StubHttpMessageHandler(_ => throw new InvalidOperationException("HTTP was not expected.")),
            SeverityFromPercent,
            null,
            new SequencedWorkspaceResultReader(
                new(OpenCodeConsoleActiveWorkspaceReadOutcome.Success, Workspace("account-first", "org-first")),
                new(invalid
                    ? OpenCodeConsoleActiveWorkspaceReadOutcome.InvalidResponse
                    : OpenCodeConsoleActiveWorkspaceReadOutcome.TransientFailure)),
            new SequencedCompanySeatClient(SuccessfulBudget(25)),
            IsOpenCodeCommandAvailable);
        StatusPoller poller = CreateCompanySeatPoller(provider);

        ProviderSnapshot first = Assert.Single(
            (await poller.PollOnceAsync(CancellationToken.None)).Providers);
        ProviderSnapshot failed = Assert.Single(
            (await poller.PollOnceAsync(CancellationToken.None)).Providers);

        Assert.NotEmpty(first.Windows);
        Assert.Empty(failed.Windows);
        Assert.Empty(failed.Info);
        Assert.Equal(1, failed.ConsecutiveFailures);
    }

    [Fact]
    public async Task OpenCodeCompanySeatDiscoveryExceptionClearsPreviousWorkspaceData()
    {
        int read = 0;
        var provider = new OpenCodeCompanySeatProvider(
            new StubHttpMessageHandler(_ => throw new InvalidOperationException("HTTP was not expected.")),
            SeverityFromPercent,
            null,
            new DelegatingWorkspaceReader(_ => Interlocked.Increment(ref read) switch
            {
                1 => Task.FromResult(new OpenCodeConsoleActiveWorkspaceReadResult(
                    OpenCodeConsoleActiveWorkspaceReadOutcome.Success,
                    Workspace("account-first", "org-first"))),
                2 => throw new IOException("synthetic command failure"),
                _ => throw new InvalidOperationException("Unexpected workspace read."),
            }),
            new SequencedCompanySeatClient(SuccessfulBudget(25)),
            IsOpenCodeCommandAvailable);
        StatusPoller poller = CreateCompanySeatPoller(provider);

        ProviderSnapshot first = Assert.Single(
            (await poller.PollOnceAsync(CancellationToken.None)).Providers);
        ProviderSnapshot failed = Assert.Single(
            (await poller.PollOnceAsync(CancellationToken.None)).Providers);

        Assert.NotEmpty(first.Windows);
        Assert.Empty(failed.Windows);
        Assert.Empty(failed.Info);
        Assert.Equal(1, failed.ConsecutiveFailures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenCodeCompanySeatCooldownRevalidatesScopeWithoutBypassingRetryAfter(
        bool selectionChanges)
    {
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        OpenCodeConsoleActiveWorkspace first = Workspace("account-first", "org-first");
        OpenCodeConsoleActiveWorkspace current = selectionChanges
            ? Workspace("account-second", "org-second")
            : first;
        int clientRequests = 0;
        var provider = new OpenCodeCompanySeatProvider(
            new StubHttpMessageHandler(_ => throw new InvalidOperationException("HTTP was not expected.")),
            SeverityFromPercent,
            time,
            new SequencedWorkspaceReader(
                first,
                first,
                current,
                current),
            new DelegatingCompanySeatClient((_, _, _) => Task.FromResult(
                Interlocked.Increment(ref clientRequests) switch
                {
                    1 => SuccessfulBudget(25),
                    2 => new OpenCodeCompanySeatFetchResult(
                    OpenCodeCompanySeatFetchOutcome.RateLimited,
                    StatusCode: HttpStatusCode.TooManyRequests,
                        RetryAfter: TimeSpan.FromMinutes(5)),
                    3 => SuccessfulBudget(75),
                    _ => throw new InvalidOperationException("Unexpected client request."),
                })),
            IsOpenCodeCommandAvailable);
        StatusPoller poller = CreateCompanySeatPoller(provider, timeProvider: time);

        await poller.PollOnceAsync(CancellationToken.None);
        ProviderSnapshot rateLimited = Assert.Single(
            (await poller.PollOnceAsync(CancellationToken.None)).Providers);
        ProviderSnapshot duringCooldown = Assert.Single(
            (await poller.PollOnceAsync(CancellationToken.None)).Providers);
        Assert.Equal(2, clientRequests);
        time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        ProviderSnapshot afterCooldown = Assert.Single(
            (await poller.PollOnceAsync(CancellationToken.None)).Providers);

        Assert.Equal(25, Assert.Single(rateLimited.Windows).Percent);
        if (selectionChanges)
        {
            Assert.Empty(duringCooldown.Windows);
            Assert.Empty(duringCooldown.Info);
        }
        else
        {
            Assert.Equal(25, Assert.Single(duringCooldown.Windows).Percent);
        }

        Assert.Equal(75, Assert.Single(afterCooldown.Windows).Percent);
        Assert.Equal(0, afterCooldown.ConsecutiveFailures);
        Assert.Equal(3, clientRequests);
    }

    [Theory]
    [InlineData("transient")]
    [InlineData("invalid")]
    [InlineData("exception")]
    [InlineData("timeout")]
    public async Task OpenCodeCompanySeatCooldownDiscoveryFailureDoesNotBypassRetryAfter(
        string failureKind)
    {
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        OpenCodeConsoleActiveWorkspace workspace = Workspace("account-first", "org-first");
        int reads = 0;
        int clientRequests = 0;
        var provider = new OpenCodeCompanySeatProvider(
            new StubHttpMessageHandler(_ => throw new InvalidOperationException("HTTP was not expected.")),
            SeverityFromPercent,
            time,
            new DelegatingWorkspaceReader(async cancellationToken =>
            {
                if (Interlocked.Increment(ref reads) != 3)
                {
                    return new OpenCodeConsoleActiveWorkspaceReadResult(
                        OpenCodeConsoleActiveWorkspaceReadOutcome.Success,
                        workspace);
                }

                if (failureKind == "exception")
                {
                    throw new IOException("synthetic command failure");
                }

                if (failureKind == "timeout")
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                return new OpenCodeConsoleActiveWorkspaceReadResult(
                    failureKind == "invalid"
                        ? OpenCodeConsoleActiveWorkspaceReadOutcome.InvalidResponse
                        : OpenCodeConsoleActiveWorkspaceReadOutcome.TransientFailure);
            }),
            new DelegatingCompanySeatClient((_, _, _) => Task.FromResult(
                Interlocked.Increment(ref clientRequests) switch
                {
                    1 => SuccessfulBudget(25),
                    2 => new OpenCodeCompanySeatFetchResult(
                        OpenCodeCompanySeatFetchOutcome.RateLimited,
                        StatusCode: HttpStatusCode.TooManyRequests,
                        RetryAfter: TimeSpan.FromMinutes(5)),
                    3 => SuccessfulBudget(75),
                    _ => throw new InvalidOperationException("Unexpected client request."),
                })),
            IsOpenCodeCommandAvailable);
        StatusPoller poller = CreateCompanySeatPoller(
            provider,
            failureKind == "timeout" ? TimeSpan.FromSeconds(1) : null,
            time);

        await poller.PollOnceAsync(CancellationToken.None);
        await poller.PollOnceAsync(CancellationToken.None);
        ProviderSnapshot duringCooldown = Assert.Single(
            (await poller.PollOnceAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2))).Providers);

        Assert.Empty(duringCooldown.Windows);
        Assert.Empty(duringCooldown.Info);
        Assert.Equal(2, clientRequests);

        time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        ProviderSnapshot afterCooldown = Assert.Single(
            (await poller.PollOnceAsync(CancellationToken.None)).Providers);

        Assert.Equal(75, Assert.Single(afterCooldown.Windows).Percent);
        Assert.Equal(3, clientRequests);
    }

    [Fact]
    public async Task TransientIncompleteClaudeCredentialUsesPollerRetentionBoundary()
    {
        // Break caught: a credential rotation window publishes an empty snapshot instead of retaining last-good data.
        var handler = new StubHttpMessageHandler(message => JsonResponse(
            message.RequestUri!.AbsolutePath.EndsWith("profile", StringComparison.Ordinal)
                ? ReadFixture("claude-profile.json")
                : ReadFixture("claude-usage.json")));
        string credentialPath = _directory.WriteFile(
            "rotating-claude-credentials.json",
            CreateClaudeCredential());
        var provider = new ClaudeProvider(credentialPath, handler, SeverityFromPercent);
        StatusPoller poller = CreatePoller(provider);
        ProviderSnapshot good = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);

        await File.WriteAllTextAsync(credentialPath, "{\"claudeAiOauth\":{");
        ProviderSnapshot retained = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);
        await File.WriteAllTextAsync(credentialPath, CreateClaudeCredential());
        ProviderSnapshot recovered = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);

        AssertRetained(good, retained, HealthState.Ok, 1);
        Assert.True(good.Windows.SequenceEqual(recovered.Windows));
        Assert.True(good.Info.SequenceEqual(recovered.Info));
        Assert.Equal(0, recovered.ConsecutiveFailures);
    }

    [Fact]
    public async Task CodexNonJsonResponseRetainsLastGoodData()
    {
        // Break caught: a 200 HTML response erases the last successful Codex quota snapshot.
        int request = 0;
        var handler = new StubHttpMessageHandler(_ => Interlocked.Increment(ref request) switch
        {
            1 => JsonResponse(ReadFixture("codex-wham.json")),
            2 => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>login</html>", Encoding.UTF8, "text/html"),
            },
            _ => throw new InvalidOperationException("Unexpected HTTP request."),
        });
        string credentialPath = _directory.WriteFile(
            "codex-invalid-response-auth.json",
            """
            {"tokens":{"access_token":"unit-test-access-token","account_id":"unit-test-account-id"}}
            """);
        StatusPoller poller = CreatePoller(new CodexProvider(credentialPath, handler, SeverityFromPercent));

        ProviderSnapshot good = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);
        ProviderSnapshot retained = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);

        AssertRetained(good, retained, HealthState.Ok, 1);
    }

    [Fact]
    public async Task OllamaConnectionFailureIncrementsFailuresAndRetainsUsage()
    {
        // Break caught: Ollama connection refusal is published as a successful empty snapshot.
        int request = 0;
        var handler = new StubHttpMessageHandler(message => Interlocked.Increment(ref request) switch
        {
            1 => JsonResponse("""{"limits":{"weekly":{"usage":0.25}}}"""),
            2 => throw new HttpRequestException("refused"),
            _ => throw new InvalidOperationException($"Unexpected request: {message.RequestUri}"),
        });
        StatusPoller poller = CreatePoller(new OllamaProvider(OllamaTestIdentity.Write(_directory), handler, SeverityFromPercent));

        ProviderSnapshot good = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);
        ProviderSnapshot retained = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);

        AssertRetained(good, retained, HealthState.Ok, 1);
    }

    [Fact]
    public async Task OllamaIdentityChangeDoesNotRetainPreviousUsageAfterFailure()
    {
        int request = 0;
        var handler = new StubHttpMessageHandler(_ => ++request == 1
            ? JsonResponse("""{"limits":{"weekly":{"usage":0.25}}}""")
            : throw new HttpRequestException("refused"));
        string keyPath = OllamaTestIdentity.Write(_directory);
        StatusPoller poller = CreatePoller(new OllamaProvider(keyPath, handler, SeverityFromPercent));
        ProviderSnapshot good = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);
        Assert.Single(good.Windows);

        OllamaTestIdentity.Write(_directory);
        ProviderSnapshot changed = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);

        Assert.Empty(changed.Windows);
        Assert.Equal(1, changed.ConsecutiveFailures);
    }

    [Fact]
    public async Task CanceledMultiProviderPollDoesNotCommitCodexCooldown()
    {
        // Break caught: a completed rate-limit response mutates scheduler state before poll cancellation commits.
        int request = 0;
        var firstCodexRequestCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler(_ =>
        {
            int currentRequest = Interlocked.Increment(ref request);
            HttpResponseMessage response = currentRequest switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.TooManyRequests),
                2 => JsonResponse(ReadFixture("codex-wham.json")),
                _ => throw new InvalidOperationException("Unexpected HTTP request."),
            };
            if (currentRequest == 1)
            {
                firstCodexRequestCompleted.TrySetResult();
            }

            return response;
        });
        string credentialPath = _directory.WriteFile(
            "codex-cancel-auth.json",
            """
            {"tokens":{"access_token":"unit-test-access-token","account_id":"unit-test-account-id"}}
            """);
        var codex = new CodexProvider(credentialPath, handler, SeverityFromPercent);
        FakeStatusProvider blocking = FakeStatusProvider.Blocking("ollama");
        StatusPoller poller = CreatePoller([codex, blocking]);
        using var cancellation = new CancellationTokenSource();

        Task<StatusReport> canceledPoll = poller.PollOnceAsync(cancellation.Token);
        await Task.WhenAll(
            blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(1)),
            firstCodexRequestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledPoll);
        blocking.CompleteOk();

        ProviderSnapshot codexSnapshot = Assert.Single(
            (await poller.PollOnceAsync(CancellationToken.None)).Providers,
            snapshot => snapshot.Id == "codex");

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(HealthState.Ok, codexSnapshot.Health);
        Assert.Equal(0, codexSnapshot.ConsecutiveFailures);
    }

    public void Dispose() => _directory.Dispose();

    private StatusPoller CreatePoller(IStatusProvider provider) => new(
        [provider],
        () => AppSettings.Default,
        new RollingFileLog(Path.Combine(_directory.Path, $"poller-{Guid.NewGuid():N}.log")));

    private StatusPoller CreatePoller(IReadOnlyList<IStatusProvider> providers) => new(
        providers,
        () => AppSettings.Default,
        new RollingFileLog(Path.Combine(_directory.Path, $"poller-{Guid.NewGuid():N}.log")));

    private StatusPoller CreateCompanySeatPoller(
        OpenCodeCompanySeatProvider provider,
        TimeSpan? providerTimeout = null,
        TimeProvider? timeProvider = null)
    {
        AppSettings settings = AppSettings.Default with
        {
            Providers = AppSettings.Default.Providers.SetItem(
                provider.Id,
                new ProviderSettings()),
        };
        return new StatusPoller(
            [provider],
            () => settings,
            new RollingFileLog(Path.Combine(_directory.Path, $"company-seat-{Guid.NewGuid():N}.log")),
            timeProvider,
            providerTimeout: providerTimeout);
    }

    private static Severity SeverityFromPercent(double? percent) =>
        SeverityPolicy.FromPercent(percent, 80, 95);

    private static bool IsOpenCodeCommandAvailable(string command) => command == "opencode";

    private static OpenCodeConsoleActiveWorkspace Workspace(string accountId, string organizationId) =>
        new(accountId, $"access-{accountId}", organizationId, DateTimeOffset.UtcNow.AddHours(1));

    private static OpenCodeCompanySeatFetchResult SuccessfulBudget(int percent) => new(
        OpenCodeCompanySeatFetchOutcome.Success,
        new OpenCodeCompanySeatBudget(
            new BigInteger(1_000_000_000),
            new BigInteger(10_000_000 * percent),
            false,
            DateTimeOffset.UtcNow.AddDays(20),
            "default"));

    private static void AssertRetained(
        ProviderSnapshot expected,
        ProviderSnapshot actual,
        HealthState health,
        int failures)
    {
        Assert.Equal(expected.PlanLabel, actual.PlanLabel);
        Assert.Equal(expected.Windows, actual.Windows);
        Assert.Equal(expected.Info, actual.Info);
        Assert.Equal(expected.FetchedAt, actual.FetchedAt);
        Assert.Equal(health, actual.Health);
        Assert.Equal(failures, actual.ConsecutiveFailures);
    }

    private static string CreateClaudeCredential() => JsonSerializer.Serialize(new
    {
        claudeAiOauth = new
        {
            accessToken = "unit-test-access-token",
            expiresAt = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds(),
        },
    });

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static string ReadFixture(string fileName) =>
        File.ReadAllText(Path.Combine(FindFixtureDirectory(), fileName));

    private static string FindFixtureDirectory()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "tests", "ReservePane.Tests", "fixtures");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("The source fixture directory was not found.");
    }

    private sealed class SequencedWorkspaceReader(params OpenCodeConsoleActiveWorkspace[] workspaces)
        : IOpenCodeConsoleActiveWorkspaceReader
    {
        private int _index;

        public Task<OpenCodeConsoleActiveWorkspaceReadResult> ReadAsync(
            CancellationToken cancellationToken) => Task.FromResult(new OpenCodeConsoleActiveWorkspaceReadResult(
                OpenCodeConsoleActiveWorkspaceReadOutcome.Success,
                workspaces[_index++]));
    }

    private sealed class SequencedWorkspaceResultReader(
        params OpenCodeConsoleActiveWorkspaceReadResult[] results)
        : IOpenCodeConsoleActiveWorkspaceReader
    {
        private int _index;

        public Task<OpenCodeConsoleActiveWorkspaceReadResult> ReadAsync(
            CancellationToken cancellationToken) => Task.FromResult(results[_index++]);
    }

    private sealed class DelegatingWorkspaceReader(
        Func<CancellationToken, Task<OpenCodeConsoleActiveWorkspaceReadResult>> read)
        : IOpenCodeConsoleActiveWorkspaceReader
    {
        public Task<OpenCodeConsoleActiveWorkspaceReadResult> ReadAsync(
            CancellationToken cancellationToken) => read(cancellationToken);
    }

    private sealed class SequencedCompanySeatClient(params OpenCodeCompanySeatFetchResult[] results)
        : IOpenCodeCompanySeatClient
    {
        private int _index;

        public Task<OpenCodeCompanySeatFetchResult> FetchAsync(
            OpenCodeConsoleActiveWorkspace workspace,
            CancellationToken cancellationToken) => Task.FromResult(results[_index++]);
    }

    private sealed class DelegatingCompanySeatClient(
        Func<int, OpenCodeConsoleActiveWorkspace, CancellationToken, Task<OpenCodeCompanySeatFetchResult>> fetch)
        : IOpenCodeCompanySeatClient
    {
        private int _index;

        public Task<OpenCodeCompanySeatFetchResult> FetchAsync(
            OpenCodeConsoleActiveWorkspace workspace,
            CancellationToken cancellationToken) => fetch(_index++, workspace, cancellationToken);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }
}
