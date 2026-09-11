using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using ReservePane.Core;
using ReservePane.Model;
using ReservePane.Providers;
using ReservePane.Tests.Support;

namespace ReservePane.Tests.Core;

public sealed class StatusPollerTests : IDisposable
{
    private readonly List<TemporaryDirectory> _directories = [];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PollOnceAsync_ReportsRefreshProgressAndClearsItAfterCompletion(bool cancel)
    {
        // Break caught: refresh feedback never appears or remains busy after completion/cancellation.
        FakeStatusProvider provider = FakeStatusProvider.Blocking("codex");
        StatusPoller poller = CreatePoller([provider]);
        var states = new ConcurrentQueue<bool>();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        poller.RefreshStateChanged += (_, refreshing) =>
        {
            states.Enqueue(refreshing);
            if (!refreshing) finished.TrySetResult();
        };
        using var cancellation = new CancellationTokenSource();
        Task<StatusReport> poll = poller.PollOnceAsync(cancellation.Token);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        bool wasRefreshing = poller.IsRefreshing;
        if (cancel)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poll);
        }
        else
        {
            provider.CompleteOk();
            await poll;
        }

        Assert.True(wasRefreshing);
        Assert.False(poller.IsRefreshing);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal([true, false], states);
    }

    [Fact]
    public async Task RefreshStateChanged_HandlerFailureDoesNotPreventRefreshOrLaterHandlers()
    {
        // Break caught: a UI feedback exception breaks polling or hides completion from other subscribers.
        StatusPoller poller = CreatePoller([FakeStatusProvider.Returning("codex", FakeStatusProvider.Snapshot("codex"))]);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        poller.RefreshStateChanged += (_, _) => throw new InvalidOperationException("private diagnostic");
        poller.RefreshStateChanged += (_, refreshing) => { if (!refreshing) finished.TrySetResult(); };

        Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(poller.IsRefreshing);
    }

    [Theory]
    [InlineData(ProviderFetchOutcome.TransientFailure, null, "Provider refresh failed. Refresh to retry.")]
    [InlineData(ProviderFetchOutcome.TransientFailure, HttpStatusCode.BadGateway, "Provider request failed (HTTP 502). Refresh to retry.")]
    [InlineData(ProviderFetchOutcome.InvalidResponse, HttpStatusCode.OK, "Provider returned an unexpected response.")]
    public async Task PollOnceAsync_FailureExplainsWhyRefreshDidNotUpdateData(
        ProviderFetchOutcome outcome, HttpStatusCode? status, string expected)
    {
        // Break caught: failed refreshes retain a generic or obsolete error instead of the actual failure category.
        ProviderSnapshot good = FakeStatusProvider.Snapshot("codex", planLabel: "retained");
        var provider = FakeStatusProvider.SequenceResults("codex",
        [
            _ => Task.FromResult(new ProviderFetchResult(ProviderFetchOutcome.Success, good)),
            _ => Task.FromResult(new ProviderFetchResult(outcome, statusCode: status)),
            _ => Task.FromResult(new ProviderFetchResult(ProviderFetchOutcome.Success, good)),
        ]);
        StatusPoller poller = CreatePoller([provider]);
        await poller.PollOnceAsync(CancellationToken.None);

        ProviderSnapshot failed = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);
        Assert.Equal(expected, failed.Error);
        Assert.Equal("retained", failed.PlanLabel);
        Assert.Equal(good.FetchedAt, failed.FetchedAt);
        Assert.Null(Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers).Error);
    }

    [Fact]
    public async Task PollOnceAsync_RateLimitShowsRetryTimeWhileManualRefreshIsDeferred()
    {
        // Break caught: a rate-limited refresh silently skips the provider without explaining the retry deadline.
        var time = new RecordingTimeProvider();
        var provider = FakeStatusProvider.ReturningResult("claude", new ProviderFetchResult(
            ProviderFetchOutcome.RateLimited, statusCode: HttpStatusCode.TooManyRequests,
            retryAfter: TimeSpan.FromMinutes(5)));
        StatusPoller poller = CreatePoller([provider], timeProvider: time);
        await poller.PollOnceAsync(CancellationToken.None);
        ProviderSnapshot snapshot = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);

        Assert.StartsWith("Rate limited. Retry after ", snapshot.Error);
        Assert.Contains((time.GetUtcNow() + TimeSpan.FromMinutes(5)).ToLocalTime().ToString("HH:mm:ss"), snapshot.Error);
        Assert.Equal(1, provider.InvocationCount);
    }

    [Theory]
    [InlineData(false, "Provider refresh failed. Refresh to retry.")]
    [InlineData(true, "Connection failed. Refresh to retry.")]
    public async Task PollOnceAsync_LocalFailureDoesNotClaimAConnectionFailure(bool network, string expected)
    {
        // Break caught: local credential or CLI errors are incorrectly presented as network failures.
        Exception exception = network ? new HttpRequestException("private detail") : new IOException("private detail");
        var provider = new FakeStatusProvider("codex", (_, _) => Task.FromException<ProviderSnapshot>(exception));
        StatusPoller poller = CreatePoller([provider]);

        Assert.Equal(expected, Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers).Error);
    }

    [Fact]
    public async Task PollOnceAsync_StartsProvidersBeforeEitherCompletes()
    {
        // Break caught: awaiting each fetch while starting it serializes provider polling.
        FakeStatusProvider first = FakeStatusProvider.Blocking("first");
        FakeStatusProvider second = FakeStatusProvider.Blocking("second");
        StatusPoller poller = CreatePoller([first, second]);

        Task<StatusReport> poll = poller.PollOnceAsync(CancellationToken.None);
        await Task.WhenAll(first.Started.Task, second.Started.Task).WaitAsync(TimeSpan.FromSeconds(1));
        first.CompleteOk();
        second.CompleteOk();

        Assert.Equal(["first", "second"], (await poll).Providers.Select(provider => provider.Id));
    }

    [Fact]
    public async Task PollOnceAsync_SerializesConcurrentTransitionsAndKeepsNewerResult()
    {
        // Break caught: a slower older poll publishes after a newer concurrent poll.
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<ProviderSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ProviderSnapshot older = FakeStatusProvider.Snapshot("claude", planLabel: "older");
        ProviderSnapshot newer = FakeStatusProvider.Snapshot("claude", planLabel: "newer");
        FakeStatusProvider provider = new(
            "claude",
            (invocation, cancellationToken) =>
            {
                if (invocation == 1)
                {
                    firstStarted.TrySetResult();
                    return releaseFirst.Task.WaitAsync(cancellationToken);
                }

                secondStarted.TrySetResult();
                return Task.FromResult(newer);
            });
        StatusPoller poller = CreatePoller([provider]);

        Task<StatusReport> firstPoll = poller.PollOnceAsync(CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Task<StatusReport> secondPoll = poller.PollOnceAsync(CancellationToken.None);

        Assert.False(secondStarted.Task.IsCompleted);
        releaseFirst.SetResult(older);
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.WhenAll(firstPoll, secondPoll).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("newer", Assert.Single(poller.Current.Providers).PlanLabel);
    }

    [Fact]
    public async Task PollOnceAsync_ConcurrentFailuresIncrementFromPublishedState()
    {
        // Break caught: concurrent failures both derive count one from the same prior report.
        var firstFailureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFailureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ProviderSnapshot success = FakeStatusProvider.Snapshot("claude", planLabel: "retained");
        FakeStatusProvider provider = new(
            "claude",
            async (invocation, cancellationToken) =>
            {
                if (invocation == 1)
                {
                    return success;
                }

                if (invocation == 2)
                {
                    firstFailureStarted.TrySetResult();
                    await releaseFirstFailure.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    secondFailureStarted.TrySetResult();
                }

                throw new IOException();
            });
        StatusPoller poller = CreatePoller([provider]);
        await poller.PollOnceAsync(CancellationToken.None);

        Task<StatusReport> firstFailure = poller.PollOnceAsync(CancellationToken.None);
        await firstFailureStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Task<StatusReport> secondFailure = poller.PollOnceAsync(CancellationToken.None);

        Assert.False(secondFailureStarted.Task.IsCompleted);
        releaseFirstFailure.SetResult();
        await secondFailureStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        StatusReport[] reports = await Task.WhenAll(firstFailure, secondFailure).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, Assert.Single(reports[0].Providers).ConsecutiveFailures);
        Assert.Equal(2, Assert.Single(reports[1].Providers).ConsecutiveFailures);
        Assert.Equal(2, Assert.Single(poller.Current.Providers).ConsecutiveFailures);
    }

    [Fact]
    public async Task PollOnceAsync_DefaultTimeoutIsTenSeconds()
    {
        // Break caught: the production timeout drifts from ten seconds.
        var time = new RecordingTimeProvider();
        FakeStatusProvider provider = FakeStatusProvider.Blocking("slow");
        StatusPoller poller = CreatePoller([provider], timeProvider: time);

        Task<StatusReport> poll = poller.PollOnceAsync(CancellationToken.None);
        RecordingTimer timeout = await time.WaitForTimerAsync(TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
        timeout.Fire();

        ProviderSnapshot snapshot = Assert.Single((await poll).Providers);
        Assert.Equal(HealthState.Unreachable, snapshot.Health);
        Assert.Equal(1, snapshot.ConsecutiveFailures);
    }

    [Fact]
    public async Task PollOnceAsync_UsesInjectedShortProviderTimeout()
    {
        // Break caught: provider calls can block past an injected timeout.
        FakeStatusProvider provider = FakeStatusProvider.Blocking("slow");
        StatusPoller poller = CreatePoller([provider], providerTimeout: TimeSpan.FromMilliseconds(20));

        ProviderSnapshot snapshot = Assert.Single(
            (await poller.PollOnceAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1))).Providers);

        Assert.Equal(HealthState.Unreachable, snapshot.Health);
        Assert.Equal(1, snapshot.ConsecutiveFailures);
        Assert.Equal("Provider request timed out. Refresh to retry.", snapshot.Error);
    }

    [Fact]
    public async Task PollOnceAsync_SuccessReplacesSnapshotAndResetsFailureCount()
    {
        // Break caught: recovery keeps retained data or a nonzero failure count.
        ProviderSnapshot old = FakeStatusProvider.Snapshot(
            "claude",
            planLabel: "old",
            info: [new InfoLine("model", "old")]);
        ProviderSnapshot replacement = FakeStatusProvider.Snapshot(
            "claude",
            planLabel: "new",
            info: [new InfoLine("model", "new")],
            consecutiveFailures: 19);
        FakeStatusProvider provider = FakeStatusProvider.Sequence(
            "claude",
            [
                _ => Task.FromResult(old),
                _ => Task.FromException<ProviderSnapshot>(new HttpRequestException()),
                _ => Task.FromResult(replacement),
            ]);
        StatusPoller poller = CreatePoller([provider]);
        await poller.PollOnceAsync(CancellationToken.None);
        await poller.PollOnceAsync(CancellationToken.None);

        ProviderSnapshot recovered = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);

        Assert.Equal("new", recovered.PlanLabel);
        Assert.Equal("new", Assert.Single(recovered.Info).Value);
        Assert.Equal(0, recovered.ConsecutiveFailures);
    }

    [Fact]
    public async Task PollOnceAsync_FirstTwoFailuresRetainDataAndHealth()
    {
        // Break caught: a transient failure clears data or degrades health too early.
        ProviderSnapshot success = FakeStatusProvider.Snapshot(
            "claude",
            planLabel: "Pro",
            windows: [new UsageWindow("weekly", 42, null, Severity.Normal)],
            fetchedAt: DateTimeOffset.Parse("2026-08-25T11:00:00Z"));
        FakeStatusProvider provider = FakeStatusProvider.Sequence(
            "claude",
            [
                _ => Task.FromResult(success),
                _ => Task.FromException<ProviderSnapshot>(new HttpRequestException()),
                _ => Task.FromException<ProviderSnapshot>(new IOException()),
            ]);
        StatusPoller poller = CreatePoller([provider]);
        await poller.PollOnceAsync(CancellationToken.None);

        ProviderSnapshot firstFailure = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);
        ProviderSnapshot secondFailure = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);

        AssertRetained(success, firstFailure, HealthState.Ok, 1);
        AssertRetained(success, secondFailure, HealthState.Ok, 2);
    }

    [Fact]
    public async Task PollOnceAsync_ThirdFailureDegradesRetainedSnapshot()
    {
        // Break caught: three consecutive failures do not change retained health to degraded.
        ProviderSnapshot success = FakeStatusProvider.Snapshot(
            "claude",
            health: HealthState.AuthExpired,
            planLabel: "Pro",
            info: [new InfoLine("account", "retained")],
            fetchedAt: DateTimeOffset.Parse("2026-08-25T11:00:00Z"));
        FakeStatusProvider provider = FakeStatusProvider.Sequence(
            "claude",
            [
                _ => Task.FromResult(success),
                _ => Task.FromException<ProviderSnapshot>(new IOException()),
                _ => Task.FromException<ProviderSnapshot>(new IOException()),
                _ => Task.FromException<ProviderSnapshot>(new IOException()),
            ]);
        StatusPoller poller = CreatePoller([provider]);
        await poller.PollOnceAsync(CancellationToken.None);
        await poller.PollOnceAsync(CancellationToken.None);
        await poller.PollOnceAsync(CancellationToken.None);

        ProviderSnapshot thirdFailure = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);

        AssertRetained(success, thirdFailure, HealthState.Degraded, 3);
    }

    [Theory]
    [InlineData(HealthState.Degraded)]
    [InlineData(HealthState.Unreachable)]
    public async Task PollOnceAsync_ProviderHealthIsSuccessfulFetchResult(HealthState health)
    {
        // Break caught: provider-reported degraded or unreachable health is mistaken for a thrown fetch failure.
        ProviderSnapshot returned = FakeStatusProvider.Snapshot(
            "ollama",
            health: health,
            error: "provider detail",
            consecutiveFailures: 8);
        StatusPoller poller = CreatePoller([FakeStatusProvider.Returning("ollama", returned)]);

        ProviderSnapshot snapshot = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);

        Assert.Equal(health, snapshot.Health);
        Assert.Equal("provider detail", snapshot.Error);
        Assert.Equal(0, snapshot.ConsecutiveFailures);
    }

    [Theory]
    [InlineData(ProviderFetchOutcome.TransientFailure)]
    [InlineData(ProviderFetchOutcome.InvalidResponse)]
    public async Task PollOnceAsync_ReturnedFailureRetainsLastGoodAndIncrementsFailures(
        ProviderFetchOutcome outcome)
    {
        // Break caught: a returned failure is mistaken for a successful fetch and resets failure state.
        ProviderSnapshot good = FakeStatusProvider.Snapshot(
            "codex",
            planLabel: "Pro",
            windows: [new UsageWindow("5h", 42, null, Severity.Normal)]);
        FakeStatusProvider provider = FakeStatusProvider.SequenceResults(
            "codex",
            [
                _ => Task.FromResult(new ProviderFetchResult(ProviderFetchOutcome.Success, good)),
                _ => Task.FromResult(new ProviderFetchResult(outcome)),
            ]);
        StatusPoller poller = CreatePoller([provider]);
        await poller.PollOnceAsync(CancellationToken.None);

        ProviderSnapshot failed = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);

        AssertRetained(good, failed, HealthState.Ok, 1);
    }

    [Theory]
    [InlineData(ProviderFetchOutcome.PartialSuccess, HealthState.Degraded)]
    [InlineData(ProviderFetchOutcome.AuthenticationRequired, HealthState.AuthExpired)]
    public async Task PollOnceAsync_PublishableOutcomePublishesSnapshotAndResetsFailures(
        ProviderFetchOutcome outcome,
        HealthState health)
    {
        // Break caught: an expected publishable outcome is retained as a transport failure.
        ProviderSnapshot fresh = FakeStatusProvider.Snapshot("claude", health: health, consecutiveFailures: 8);
        StatusPoller poller = CreatePoller([
            FakeStatusProvider.ReturningResult("claude", new ProviderFetchResult(outcome, fresh)),
        ]);

        ProviderSnapshot published = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);

        Assert.Equal(health, published.Health);
        Assert.Equal(0, published.ConsecutiveFailures);
    }

    [Fact]
    public async Task PollOnceAsync_NotConfiguredProviderIsOmittedAndRetried()
    {
        // Break caught: a provider that reports missing configuration remains visible or is never rediscovered.
        ProviderSnapshot notConfigured = FakeStatusProvider.Snapshot(
            "opencode-go",
            health: HealthState.Unreachable,
            error: "not configured");
        ProviderSnapshot configured = FakeStatusProvider.Snapshot(
            "opencode-go",
            planLabel: "Go");
        FakeStatusProvider provider = FakeStatusProvider.SequenceResults(
            "opencode-go",
            [
                _ => Task.FromResult(new ProviderFetchResult(
                    ProviderFetchOutcome.NotConfigured,
                    notConfigured)),
                _ => Task.FromResult(new ProviderFetchResult(
                    ProviderFetchOutcome.Success,
                    configured)),
            ]);
        StatusPoller poller = CreatePoller([provider]);

        StatusReport hidden = await poller.PollOnceAsync(CancellationToken.None);
        StatusReport discovered = await poller.PollOnceAsync(CancellationToken.None);

        Assert.Empty(hidden.Providers);
        Assert.Equal("Go", Assert.Single(discovered.Providers).PlanLabel);
        Assert.Equal(2, provider.InvocationCount);
    }

    [Fact]
    public async Task PollOnceAsync_WrongProviderIdIsInvalidAndRetainsLastGood()
    {
        // Break caught: a provider can replace another provider's state by returning the wrong ID.
        ProviderSnapshot good = FakeStatusProvider.Snapshot("codex", planLabel: "Pro");
        ProviderSnapshot wrong = FakeStatusProvider.Snapshot("claude", planLabel: "wrong");
        FakeStatusProvider provider = FakeStatusProvider.SequenceResults(
            "codex",
            [
                _ => Task.FromResult(new ProviderFetchResult(ProviderFetchOutcome.Success, good)),
                _ => Task.FromResult(new ProviderFetchResult(ProviderFetchOutcome.Success, wrong)),
            ]);
        StatusPoller poller = CreatePoller([provider]);
        await poller.PollOnceAsync(CancellationToken.None);

        ProviderSnapshot retained = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);

        AssertRetained(good, retained, HealthState.Ok, 1);
    }

    [Fact]
    public async Task PollOnceAsync_RateLimitSkipsOnlyCoolingProviderUntilDeadline()
    {
        // Break caught: manual polls bypass a cooldown or one cooled provider suppresses other providers.
        var time = new RecordingTimeProvider();
        ProviderSnapshot recovered = FakeStatusProvider.Snapshot("codex", planLabel: "recovered");
        FakeStatusProvider cooled = FakeStatusProvider.SequenceResults(
            "codex",
            [
                _ => Task.FromResult(new ProviderFetchResult(
                    ProviderFetchOutcome.RateLimited,
                    statusCode: HttpStatusCode.TooManyRequests,
                    retryAfter: TimeSpan.FromMinutes(5))),
                _ => Task.FromResult(new ProviderFetchResult(ProviderFetchOutcome.Success, recovered)),
            ]);
        FakeStatusProvider healthy = FakeStatusProvider.Returning(
            "ollama",
            FakeStatusProvider.Snapshot("ollama"));
        StatusPoller poller = CreatePoller([cooled, healthy], timeProvider: time);

        await poller.PollOnceAsync(CancellationToken.None);
        ProviderSnapshot[] duringCooldown = (await poller.PollOnceAsync(CancellationToken.None)).Providers.ToArray();
        int cooledInvocationsDuringCooldown = cooled.InvocationCount;
        int healthyInvocationsDuringCooldown = healthy.InvocationCount;
        time.Advance(TimeSpan.FromMinutes(5));
        ProviderSnapshot[] afterCooldown = (await poller.PollOnceAsync(CancellationToken.None)).Providers.ToArray();

        Assert.Equal(1, cooledInvocationsDuringCooldown);
        Assert.Equal(2, healthyInvocationsDuringCooldown);
        Assert.Equal(1, Assert.Single(duringCooldown, snapshot => snapshot.Id == "codex").ConsecutiveFailures);
        Assert.Equal("recovered", Assert.Single(afterCooldown, snapshot => snapshot.Id == "codex").PlanLabel);
        Assert.Equal(2, cooled.InvocationCount);
        Assert.Equal(3, healthy.InvocationCount);
    }

    [Fact]
    public async Task PollOnceAsync_CallerCancellationDoesNotCommitCompletedProviderCooldown()
    {
        // Break caught: one completed provider mutates cooldown state before another provider observes cancellation.
        ProviderSnapshot recovered = FakeStatusProvider.Snapshot("codex", planLabel: "eligible");
        FakeStatusProvider rateLimited = FakeStatusProvider.SequenceResults(
            "codex",
            [
                _ => Task.FromResult(new ProviderFetchResult(
                    ProviderFetchOutcome.RateLimited,
                    statusCode: HttpStatusCode.TooManyRequests,
                    retryAfter: TimeSpan.FromMinutes(5))),
                _ => Task.FromResult(new ProviderFetchResult(ProviderFetchOutcome.Success, recovered)),
            ]);
        FakeStatusProvider blocking = FakeStatusProvider.Blocking("ollama");
        StatusPoller poller = CreatePoller([rateLimited, blocking]);
        using var cancellation = new CancellationTokenSource();

        Task<StatusReport> canceledPoll = poller.PollOnceAsync(cancellation.Token);
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledPoll);
        blocking.CompleteOk();

        ProviderSnapshot[] next = (await poller.PollOnceAsync(CancellationToken.None)).Providers.ToArray();

        Assert.Equal(2, rateLimited.InvocationCount);
        Assert.Equal("eligible", Assert.Single(next, snapshot => snapshot.Id == "codex").PlanLabel);
    }

    [Fact]
    public async Task PollOnceAsync_PropagatesCallerCancellation()
    {
        // Break caught: caller cancellation is converted into a retained provider failure.
        FakeStatusProvider provider = FakeStatusProvider.Blocking("claude");
        StatusPoller poller = CreatePoller([provider], providerTimeout: TimeSpan.FromMinutes(1));
        using var cancellation = new CancellationTokenSource();

        Task<StatusReport> poll = poller.PollOnceAsync(cancellation.Token);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poll.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Empty(poller.Current.Providers);
    }

    [Fact]
    public async Task PollOnceAsync_UnavailableProviderIsOmittedWithoutFetching()
    {
        // Break caught: an unavailable provider is fetched and published as a failure snapshot.
        var provider = new AvailabilityStatusProvider("claude")
        {
            Availability = _ => Task.FromResult(false),
        };
        StatusPoller poller = CreatePoller([provider]);

        StatusReport report = await poller.PollOnceAsync(CancellationToken.None);

        Assert.Empty(report.Providers);
        Assert.Equal(0, provider.FetchCount);
        Assert.Equal(1, provider.AvailabilityCount);
    }

    [Fact]
    public async Task PollOnceAsync_ReevaluatesAvailabilityAcrossPolls()
    {
        // Break caught: availability is cached, so provider appearance or disappearance is not reflected per poll.
        bool isAvailable = false;
        var provider = new AvailabilityStatusProvider("codex")
        {
            Availability = _ => Task.FromResult(isAvailable),
        };
        StatusPoller poller = CreatePoller([provider]);

        StatusReport unavailable = await poller.PollOnceAsync(CancellationToken.None);
        isAvailable = true;
        StatusReport available = await poller.PollOnceAsync(CancellationToken.None);
        isAvailable = false;
        StatusReport unavailableAgain = await poller.PollOnceAsync(CancellationToken.None);

        Assert.Empty(unavailable.Providers);
        Assert.Equal("codex", Assert.Single(available.Providers).Id);
        Assert.Empty(unavailableAgain.Providers);
        Assert.Equal(3, provider.AvailabilityCount);
        Assert.Equal(1, provider.FetchCount);
    }

    [Fact]
    public async Task PollOnceAsync_UnavailableCycleClearsProviderCooldown()
    {
        // Break caught: an unavailable cycle leaves retry-after state that suppresses a fresh fetch after discovery.
        bool isAvailable = true;
        var provider = new AvailabilityStatusProvider("codex")
        {
            Availability = _ => Task.FromResult(isAvailable),
            Fetch = (invocation, _) => Task.FromResult(invocation == 1
                ? new ProviderFetchResult(
                    ProviderFetchOutcome.RateLimited,
                    statusCode: HttpStatusCode.TooManyRequests,
                    retryAfter: TimeSpan.FromMinutes(5))
                : new ProviderFetchResult(
                    ProviderFetchOutcome.Success,
                    FakeStatusProvider.Snapshot("codex", planLabel: "fresh"))),
        };
        StatusPoller poller = CreatePoller([provider]);

        await poller.PollOnceAsync(CancellationToken.None);
        isAvailable = false;
        StatusReport unavailable = await poller.PollOnceAsync(CancellationToken.None);
        isAvailable = true;
        ProviderSnapshot fresh = Assert.Single(
            (await poller.PollOnceAsync(CancellationToken.None)).Providers);

        Assert.Empty(unavailable.Providers);
        Assert.Equal("fresh", fresh.PlanLabel);
        Assert.Equal(2, provider.FetchCount);
    }

    [Fact]
    public async Task PollOnceAsync_UnavailableProviderDoesNotSuppressOrdinaryOrAvailableProviders()
    {
        // Break caught: filtering one unavailable provider drops neighbors or reorders the survivors.
        var unavailable = new AvailabilityStatusProvider("claude")
        {
            Availability = _ => Task.FromResult(false),
        };
        FakeStatusProvider ordinary = FakeStatusProvider.Returning(
            "custom",
            FakeStatusProvider.Snapshot("custom"));
        var available = new AvailabilityStatusProvider("ollama");
        StatusPoller poller = CreatePoller([unavailable, ordinary, available]);

        ProviderSnapshot[] snapshots = (await poller.PollOnceAsync(CancellationToken.None)).Providers.ToArray();

        Assert.Equal(["custom", "ollama"], snapshots.Select(snapshot => snapshot.Id));
        Assert.Equal(1, ordinary.InvocationCount);
        Assert.Equal(1, available.FetchCount);
        Assert.Equal(0, unavailable.FetchCount);
    }

    [Fact]
    public async Task PollOnceAsync_CallerCancellationDuringAvailabilityPropagates()
    {
        // Break caught: caller cancellation during discovery is converted into an unavailable provider result.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new AvailabilityStatusProvider("claude")
        {
            Availability = async cancellationToken =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return true;
            },
        };
        StatusPoller poller = CreatePoller([provider], providerTimeout: TimeSpan.FromMinutes(1));
        using var cancellation = new CancellationTokenSource();

        Task<StatusReport> poll = poller.PollOnceAsync(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poll);
        Assert.Equal(0, provider.FetchCount);
    }

    [Fact]
    public async Task PollOnceAsync_SynchronouslyBlockingAvailabilityDoesNotDelayOtherProbeAndTimesOut()
    {
        // Break caught: synchronous discovery blocks poll startup and escapes the configured timeout.
        var blockingStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var blockingFinished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var otherStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var otherFetched = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var blocking = new AvailabilityStatusProvider("claude")
        {
            Availability = _ =>
            {
                blockingStarted.TrySetResult();
                release.Wait();
                blockingFinished.TrySetResult();
                return Task.FromResult(true);
            },
        };
        var other = new AvailabilityStatusProvider("codex")
        {
            Availability = _ =>
            {
                otherStarted.TrySetResult();
                return Task.FromResult(true);
            },
            Fetch = (_, _) =>
            {
                otherFetched.TrySetResult();
                return Task.FromResult(new ProviderFetchResult(
                    ProviderFetchOutcome.Success,
                    FakeStatusProvider.Snapshot("codex")));
            },
        };
        var time = new RecordingTimeProvider();
        TimeSpan providerTimeout = TimeSpan.FromSeconds(10);
        StatusPoller poller = CreatePoller(
            [blocking, other],
            timeProvider: time,
            providerTimeout: providerTimeout);
        Task<StatusReport> poll = Task.Run(() => poller.PollOnceAsync(CancellationToken.None));

        try
        {
            await blockingStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await otherStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await otherFetched.Task.WaitAsync(TimeSpan.FromSeconds(1));
            _ = await time.WaitForTimerAsync(
                providerTimeout,
                Timeout.InfiniteTimeSpan);
            Assert.Equal(1, time.CountActiveTimers(providerTimeout, Timeout.InfiniteTimeSpan));
            time.FireTimers(providerTimeout, Timeout.InfiniteTimeSpan);

            StatusReport report = await poll.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Equal("codex", Assert.Single(report.Providers).Id);
            Assert.Equal(0, blocking.FetchCount);
            Assert.Equal(1, other.FetchCount);

            release.Set();
            await blockingFinished.Task.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Equal("codex", Assert.Single(poller.Current.Providers).Id);
            Assert.Equal(0, blocking.FetchCount);
        }
        finally
        {
            release.Set();
            await blockingFinished.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await poll.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task PollOnceAsync_ReusesNonCooperativeTimedOutAvailabilityProbe()
    {
        // Break caught: every poll starts another permanently blocked availability worker.
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var provider = new AvailabilityStatusProvider("claude")
        {
            Availability = _ =>
            {
                started.TrySetResult();
                release.Wait();
                finished.TrySetResult();
                return Task.FromResult(true);
            },
        };
        StatusPoller poller = CreatePoller(
            [provider],
            providerTimeout: TimeSpan.FromMilliseconds(20));

        try
        {
            StatusReport first = await poller
                .PollOnceAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(1));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            StatusReport second = await poller
                .PollOnceAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Empty(first.Providers);
            Assert.Empty(second.Providers);
            Assert.Equal(1, provider.AvailabilityCount);
            Assert.Equal(0, provider.FetchCount);

            release.Set();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(1));
            ProviderSnapshot? rediscovered = null;
            for (int attempt = 0; attempt < 10 && rediscovered is null; attempt++)
            {
                StatusReport report = await poller.PollOnceAsync(CancellationToken.None);
                rediscovered = report.Providers.SingleOrDefault();
                await Task.Yield();
            }

            Assert.Equal("claude", Assert.IsType<ProviderSnapshot>(rediscovered).Id);
            Assert.Equal(2, provider.AvailabilityCount);
            Assert.Equal(1, provider.FetchCount);
        }
        finally
        {
            release.Set();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task PollOnceAsync_CallerCancellationReusesBlockedAvailabilityProbeUntilCompletion()
    {
        // Break caught: caller cancellation detaches a blocked probe and the next poll starts a duplicate worker.
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var provider = new AvailabilityStatusProvider("claude")
        {
            Availability = _ =>
            {
                started.TrySetResult();
                release.Wait();
                finished.TrySetResult();
                return Task.FromResult(true);
            },
        };
        var time = new RecordingTimeProvider();
        TimeSpan providerTimeout = TimeSpan.FromSeconds(10);
        StatusPoller poller = CreatePoller(
            [provider],
            timeProvider: time,
            providerTimeout: providerTimeout);
        using var cancellation = new CancellationTokenSource();

        try
        {
            Task<StatusReport> canceled = Task.Run(() => poller.PollOnceAsync(cancellation.Token));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => canceled.WaitAsync(TimeSpan.FromSeconds(1)));

            StatusReport hidden = await poller
                .PollOnceAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromMilliseconds(100));

            Assert.Empty(hidden.Providers);
            Assert.Equal(1, provider.AvailabilityCount);
            Assert.Equal(0, provider.FetchCount);

            release.Set();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(1));
            ProviderSnapshot rediscovered = Assert.Single(
                (await poller.PollOnceAsync(CancellationToken.None)).Providers);

            Assert.Equal("claude", rediscovered.Id);
            Assert.Equal(2, provider.AvailabilityCount);
            Assert.Equal(1, provider.FetchCount);
        }
        finally
        {
            release.Set();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task PollOnceAsync_CallerCancellationStopsWaitingForSynchronousAvailability()
    {
        // Break caught: caller cancellation waits for a non-cooperative synchronous probe to return.
        var blockingStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var blockingFinished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var provider = new AvailabilityStatusProvider("claude")
        {
            Availability = _ =>
            {
                blockingStarted.TrySetResult();
                release.Wait();
                blockingFinished.TrySetResult();
                return Task.FromResult(true);
            },
        };
        StatusPoller poller = CreatePoller([provider], providerTimeout: TimeSpan.FromMinutes(1));
        using var cancellation = new CancellationTokenSource();
        Task<StatusReport> poll = Task.Run(() => poller.PollOnceAsync(cancellation.Token));

        try
        {
            await blockingStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => poll.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Empty(poller.Current.Providers);
            Assert.Equal(0, provider.FetchCount);
        }
        finally
        {
            release.Set();
            await blockingFinished.Task.WaitAsync(TimeSpan.FromSeconds(1));
            try
            {
                await poll.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [Fact]
    public async Task PollOnceAsync_AvailabilityExceptionIsSanitizedAndProviderIsOmitted()
    {
        // Break caught: discovery exceptions publish providers or leak exception messages into the log.
        const string secret = "availability-secret";
        var directory = new TemporaryDirectory();
        _directories.Add(directory);
        string logPath = Path.Combine(directory.Path, "availability-exception.log");
        var provider = new AvailabilityStatusProvider("claude")
        {
            Availability = _ => throw new IOException(secret),
        };
        var poller = new StatusPoller(
            [provider],
            Settings,
            new RollingFileLog(logPath));

        StatusReport report = await poller.PollOnceAsync(CancellationToken.None);
        string log = await File.ReadAllTextAsync(logPath);

        Assert.Empty(report.Providers);
        Assert.Equal(0, provider.FetchCount);
        Assert.Contains("provider=claude", log, StringComparison.Ordinal);
        Assert.Contains("exception=IOException", log, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PollOnceAsync_AvailabilityTimeoutIsSanitizedAndProviderIsOmitted()
    {
        // Break caught: discovery can hang past the provider timeout or is treated as a fetch failure snapshot.
        var directory = new TemporaryDirectory();
        _directories.Add(directory);
        string logPath = Path.Combine(directory.Path, "availability-timeout.log");
        var provider = new AvailabilityStatusProvider("ollama")
        {
            Availability = async cancellationToken =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return true;
            },
        };
        var poller = new StatusPoller(
            [provider],
            Settings,
            new RollingFileLog(logPath),
            providerTimeout: TimeSpan.FromMilliseconds(20));

        StatusReport report = await poller
            .PollOnceAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));
        string log = await File.ReadAllTextAsync(logPath);

        Assert.Empty(report.Providers);
        Assert.Equal(0, provider.FetchCount);
        Assert.Contains("provider=ollama", log, StringComparison.Ordinal);
        Assert.Contains("timed-out", log, StringComparison.Ordinal);
        Assert.Contains("exception=TaskCanceledException", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_ReadyWaitsForCadenceTimerInitialization()
    {
        // Break caught: startup completion races ahead of the background poll loop initialization.
        using var time = new GatedTimeProvider();
        StatusPoller poller = CreatePoller([], timeProvider: time);
        using var cancellation = new CancellationTokenSource();
        PollLoopRun run = poller.Start(cancellation.Token);

        try
        {
            await time.TimerCreationStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(run.Ready.IsCompleted);

            time.ReleaseTimerCreation();
            await run.Ready.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.True(time.TimerCreated);
        }
        finally
        {
            time.ReleaseTimerCreation();
            cancellation.Cancel();
            await run.Completion.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task RequestRefresh_WakesRunLoopBeforeTimerTick()
    {
        // Break caught: refresh requests wait for the scheduled cadence.
        var time = new RecordingTimeProvider();
        FakeStatusProvider provider = FakeStatusProvider.Returning("claude", FakeStatusProvider.Snapshot("claude"));
        StatusPoller poller = CreatePoller([provider], timeProvider: time);
        using var cancellation = new CancellationTokenSource();
        Task run = poller.RunAsync(cancellation.Token);
        await time.WaitForTimerAsync(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

        poller.RequestRefresh();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, provider.InvocationCount);
    }

    [Fact]
    public async Task RequestRefresh_PerformsOnePoll()
    {
        // Break caught: a consumed auto-reset signal remains completed and drives a second poll.
        var time = new RecordingTimeProvider();
        var firstResult = new TaskCompletionSource<ProviderSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        FakeStatusProvider provider = new(
            "claude",
            (invocation, cancellationToken) => invocation == 1
                ? firstResult.Task.WaitAsync(cancellationToken)
                : Task.FromException<ProviderSnapshot>(new InvalidOperationException("Unexpected second poll.")));
        StatusPoller poller = CreatePoller([provider], timeProvider: time);
        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        poller.ReportUpdated += (_, _) => updated.TrySetResult();
        using var cancellation = new CancellationTokenSource();
        Task run = poller.RunAsync(cancellation.Token);
        await time.WaitForTimerAsync(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

        poller.RequestRefresh();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        firstResult.SetResult(FakeStatusProvider.Snapshot("claude"));
        await updated.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, provider.InvocationCount);
        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task RequestRefresh_RepeatedRequestsLeaveAtMostOnePendingPoll()
    {
        // Break caught: repeated refresh requests queue redundant full polls.
        var time = new RecordingTimeProvider();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeStatusProvider provider = new(
            "claude",
            async (invocation, cancellationToken) =>
            {
                if (invocation == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }

                if (invocation == 2)
                {
                    secondStarted.TrySetResult();
                }

                return FakeStatusProvider.Snapshot("claude");
            });
        StatusPoller poller = CreatePoller([provider], timeProvider: time);
        using var cancellation = new CancellationTokenSource();
        Task run = poller.RunAsync(cancellation.Token);
        await time.WaitForTimerAsync(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

        try
        {
            poller.RequestRefresh();
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            for (int request = 0; request < 20; request++)
            {
                poller.RequestRefresh();
            }

            releaseFirst.TrySetResult();
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await Task.Delay(50);

            Assert.Equal(2, provider.InvocationCount);
        }
        finally
        {
            cancellation.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task RunAsync_RejectsSecondCallerWithoutCreatingAnotherTimer()
    {
        // Break caught: two active RunAsync callers own independent timers and consume refreshes concurrently.
        var time = new RecordingTimeProvider();
        StatusPoller poller = CreatePoller([], timeProvider: time);
        using var cancellation = new CancellationTokenSource();
        Task firstRun = poller.RunAsync(cancellation.Token);
        await time.WaitForTimerAsync(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        Task secondRun = poller.RunAsync(cancellation.Token);

        try
        {
            Assert.Equal(1, time.CountActiveTimers(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => secondRun.WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            cancellation.Cancel();
            await Task.WhenAll(firstRun, secondRun.ContinueWith(_ => { }, TaskScheduler.Default))
                .WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task SetReducedCadence_RecreatesTimerUsingCurrentSettings()
    {
        // Break caught: cadence mode changes leave the old timer active or use stale settings.
        var time = new RecordingTimeProvider();
        AppSettings currentSettings = Settings();
        StatusPoller poller = CreatePoller([], () => currentSettings, time);
        using var cancellation = new CancellationTokenSource();
        Task run = poller.RunAsync(cancellation.Token);
        RecordingTimer original = await time.WaitForTimerAsync(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

        poller.SetReducedCadence(true);
        RecordingTimer reduced = await time.WaitForTimerAsync(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        Assert.True(original.IsDisposed);

        currentSettings = currentSettings with { PollInterval = TimeSpan.FromSeconds(30) };
        poller.SetReducedCadence(false);
        RecordingTimer restored = await time.WaitForTimerAsync(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        Assert.True(reduced.IsDisposed);

        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(restored.IsDisposed);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 5)]
    public async Task PreRunCadenceChange_QueuesNoRefreshAndExplicitStartupRefreshPollsOnce(
        bool reduced,
        int expectedMinutes)
    {
        // Break caught: initial reduced cadence plus the explicit startup refresh produces two immediate polls.
        var time = new RecordingTimeProvider();
        FakeStatusProvider provider = FakeStatusProvider.Blocking("claude");
        StatusPoller poller = CreatePoller(
            [provider],
            timeProvider: time);
        poller.SetReducedCadence(reduced);
        using var cancellation = new CancellationTokenSource();

        Task run = poller.RunAsync(cancellation.Token);
        RecordingTimer cadence = await time.WaitForTimerAsync(
            TimeSpan.FromMinutes(expectedMinutes),
            TimeSpan.FromMinutes(expectedMinutes));

        Assert.False(provider.Started.Task.IsCompleted);
        poller.RequestRefresh();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, provider.InvocationCount);

        provider.CompleteOk();
        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, provider.InvocationCount);
        Assert.True(cadence.IsDisposed);
    }

    [Fact]
    public async Task RunAsync_RecreatesTimerWhenSettingsChangeAfterTick()
    {
        // Break caught: a settings update is ignored after the current timer wakes the loop.
        var time = new RecordingTimeProvider();
        AppSettings currentSettings = Settings();
        StatusPoller poller = CreatePoller([], () => currentSettings, time);
        using var cancellation = new CancellationTokenSource();
        Task run = poller.RunAsync(cancellation.Token);
        RecordingTimer original = await time.WaitForTimerAsync(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

        currentSettings = currentSettings with { PollInterval = TimeSpan.FromSeconds(20) };
        original.Fire();
        RecordingTimer changed = await time.WaitForTimerAsync(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));

        Assert.True(original.IsDisposed);
        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(changed.IsDisposed);
    }

    [Fact]
    public async Task PollOnceAsync_DispatchesReportUpdatedOnCapturedSynchronizationContext()
    {
        // Break caught: report events run inline on a worker instead of the constructor's UI context.
        var context = new QueuedSynchronizationContext();
        SynchronizationContext? previous = SynchronizationContext.Current;
        StatusPoller poller;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            poller = CreatePoller([
                FakeStatusProvider.Returning("claude", FakeStatusProvider.Snapshot("claude")),
            ]);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        StatusReport? observed = null;
        poller.ReportUpdated += (_, report) => observed = report;
        StatusReport result = await Task.Run(() => poller.PollOnceAsync(CancellationToken.None));

        Assert.Null(observed);
        Assert.Same(result, poller.Current);
        await context.Posted.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, context.PendingCount);
        context.RunOne();
        Assert.Same(result, observed);
    }

    [Fact]
    public async Task RunAsync_ExecutesProvidersOffCapturedUiContextAndDeliversReportsOnIt()
    {
        // Break caught: a prequeued startup refresh runs synchronous credential scans on the captured UI thread.
        using var uiContext = new DedicatedThreadSynchronizationContext();
        using var cancellation = new CancellationTokenSource();
        var providerRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reportDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int providerThread = -1;
        int deliveryThread = -1;
        SynchronizationContext? providerContext = null;
        SynchronizationContext? deliveryContext = null;
        var provider = new FakeStatusProvider("claude", (_, _) =>
        {
            providerThread = Environment.CurrentManagedThreadId;
            providerContext = SynchronizationContext.Current;
            providerRan.TrySetResult();
            return Task.FromResult(FakeStatusProvider.Snapshot("claude"));
        });

        (StatusPoller Poller, Task Loop) running = await uiContext.InvokeAsync(() =>
        {
            StatusPoller poller = CreatePoller([provider]);
            poller.ReportUpdated += (_, _) =>
            {
                deliveryThread = Environment.CurrentManagedThreadId;
                deliveryContext = SynchronizationContext.Current;
                reportDelivered.TrySetResult();
            };
            poller.RequestRefresh();
            return (poller, poller.RunAsync(cancellation.Token));
        });

        try
        {
            await providerRan.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await reportDelivered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.NotEqual(uiContext.ThreadId, providerThread);
            Assert.NotSame(uiContext, providerContext);
            Assert.Equal(uiContext.ThreadId, deliveryThread);
            Assert.Same(uiContext, deliveryContext);
        }
        finally
        {
            cancellation.Cancel();
            await running.Loop.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task ReportUpdated_ThrowingCapturedContextNeverFallsBackOffContextOrDuplicates()
    {
        // Break caught: a failed context post invokes UI handlers on the pump thread and again from a queued callback.
        var directory = new TemporaryDirectory();
        _directories.Add(directory);
        string logPath = Path.Combine(directory.Path, "poller.log");
        var context = new QueueThenThrowSynchronizationContext();
        SynchronizationContext? previous = SynchronizationContext.Current;
        StatusPoller poller;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            poller = new StatusPoller(
                [new FakeStatusProvider(
                    "claude",
                    (invocation, _) => Task.FromResult(FakeStatusProvider.Snapshot(
                        "claude",
                        planLabel: invocation.ToString(System.Globalization.CultureInfo.InvariantCulture))))],
                Settings,
                new RollingFileLog(logPath));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        var deliveries = new ConcurrentQueue<(string? Plan, bool OnContext)>();
        poller.ReportUpdated += (_, report) => deliveries.Enqueue((
            Assert.Single(report.Providers).PlanLabel,
            context.IsExecuting));

        await Task.Run(() => poller.PollOnceAsync(CancellationToken.None));
        await context.FirstPostAttempted.Task.WaitAsync(TimeSpan.FromMilliseconds(300));
        await Task.Run(() => poller.PollOnceAsync(CancellationToken.None));
        await context.SecondPostQueued.Task.WaitAsync(TimeSpan.FromMilliseconds(300));

        try
        {
            Assert.Empty(deliveries);
        }
        finally
        {
            context.RunAll();
        }

        Assert.Equal(["1", "2"], deliveries.Select(delivery => delivery.Plan));
        Assert.All(deliveries, delivery => Assert.True(delivery.OnContext));
        string log = File.ReadAllText(logPath);
        Assert.Contains(" ui failed exception=InvalidOperationException", log, StringComparison.Ordinal);
        Assert.DoesNotContain("dispatch secret", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportUpdated_NoContextHandlerCanSynchronouslyPollAgain()
    {
        // Break caught: inline notification under the poll gate deadlocks a synchronous reentrant poll.
        FakeStatusProvider provider = new(
            "claude",
            (invocation, _) => Task.FromResult(FakeStatusProvider.Snapshot(
                "claude",
                planLabel: invocation.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        StatusPoller poller = await Task.Run(() => CreatePoller([provider]));
        var handlerCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int notifications = 0;
        poller.ReportUpdated += (_, _) =>
        {
            if (Interlocked.Increment(ref notifications) == 1)
            {
                poller.PollOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
                handlerCompleted.TrySetResult();
            }
        };

        Task<StatusReport> outerPoll = Task.Run(() => poller.PollOnceAsync(CancellationToken.None));

        await outerPoll.WaitAsync(TimeSpan.FromMilliseconds(300));
        await handlerCompleted.Task.WaitAsync(TimeSpan.FromMilliseconds(300));
        Assert.Equal(2, provider.InvocationCount);
    }

    [Fact]
    public async Task ReportUpdated_DeliversSerializedPublicationsInOrderWithoutBlockingPolls()
    {
        // Break caught: a slow first handler blocks later publication or permits out-of-order delivery.
        FakeStatusProvider provider = new(
            "claude",
            (invocation, _) => Task.FromResult(FakeStatusProvider.Snapshot(
                "claude",
                planLabel: invocation.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        StatusPoller poller = await Task.Run(() => CreatePoller([provider]));
        var firstHandlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseFirstHandler = new ManualResetEventSlim();
        var deliveries = new ConcurrentQueue<string?>();
        poller.ReportUpdated += (_, report) =>
        {
            string? plan = Assert.Single(report.Providers).PlanLabel;
            deliveries.Enqueue(plan);
            if (plan == "1")
            {
                firstHandlerStarted.TrySetResult();
                releaseFirstHandler.Wait();
            }
            else
            {
                secondDelivered.TrySetResult();
            }
        };

        Task<StatusReport> firstPoll = Task.Run(() => poller.PollOnceAsync(CancellationToken.None));

        try
        {
            await firstHandlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Task<StatusReport> secondPoll = Task.Run(() => poller.PollOnceAsync(CancellationToken.None));
            await Task.WhenAll(firstPoll, secondPoll).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            releaseFirstHandler.Set();
        }

        await secondDelivered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(["1", "2"], deliveries);
    }

    [Fact]
    public async Task ReportUpdated_HandlerFailureIsLoggedAndDoesNotStopDelivery()
    {
        // Break caught: one external handler exception escapes polling, leaks text, or stops later delivery.
        var directory = new TemporaryDirectory();
        _directories.Add(directory);
        string logPath = Path.Combine(directory.Path, "poller.log");
        FakeStatusProvider provider = FakeStatusProvider.Returning(
            "claude",
            FakeStatusProvider.Snapshot("claude"));
        StatusPoller poller = await Task.Run(() => new StatusPoller(
            [provider],
            Settings,
            new RollingFileLog(logPath)));
        var healthyHandlerCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        poller.ReportUpdated += (_, _) => throw new InvalidOperationException("handler secret");
        poller.ReportUpdated += (_, _) => healthyHandlerCalled.TrySetResult();

        await poller.PollOnceAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(300));
        await healthyHandlerCalled.Task.WaitAsync(TimeSpan.FromMilliseconds(300));

        string log = File.ReadAllText(logPath);
        Assert.Contains(" ui failed exception=InvalidOperationException", log, StringComparison.Ordinal);
        Assert.DoesNotContain("handler secret", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShutdownDrainsAlreadyPublishedNotification()
    {
        // Break caught: shutdown abandons a notification that was published before cancellation.
        var time = new RecordingTimeProvider();
        FakeStatusProvider provider = FakeStatusProvider.Returning(
            "claude",
            FakeStatusProvider.Snapshot("claude"));
        StatusPoller poller = await Task.Run(() => CreatePoller([provider], timeProvider: time));
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseHandler = new ManualResetEventSlim();
        poller.ReportUpdated += (_, _) =>
        {
            handlerStarted.TrySetResult();
            releaseHandler.Wait(TimeSpan.FromSeconds(1));
            handlerCompleted.TrySetResult();
        };
        using var cancellation = new CancellationTokenSource();
        Task run = poller.RunAsync(cancellation.Token);
        RecordingTimer cadence = await time.WaitForTimerAsync(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

        poller.RequestRefresh();
        await handlerStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(300));
        cancellation.Cancel();

        try
        {
            await cadence.Disposed.Task.WaitAsync(TimeSpan.FromMilliseconds(300));
            Assert.False(run.IsCompleted);
        }
        finally
        {
            releaseHandler.Set();
        }

        await run.WaitAsync(TimeSpan.FromMilliseconds(300));
        await handlerCompleted.Task.WaitAsync(TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public async Task RunAsync_CancellationStopsActivePollAndDisposesTimer()
    {
        // Break caught: shutdown leaves a cadence timer or provider wait abandoned.
        var time = new RecordingTimeProvider();
        FakeStatusProvider provider = FakeStatusProvider.Blocking("claude");
        StatusPoller poller = CreatePoller([provider], timeProvider: time);
        using var cancellation = new CancellationTokenSource();
        Task run = poller.RunAsync(cancellation.Token);
        RecordingTimer cadence = await time.WaitForTimerAsync(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

        poller.RequestRefresh();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        await run.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(cadence.IsDisposed);
        Assert.False(time.HasActiveTimer(TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan));
    }

    private StatusPoller CreatePoller(
        IReadOnlyList<IStatusProvider> providers,
        Func<AppSettings>? settings = null,
        TimeProvider? timeProvider = null,
        TimeSpan? providerTimeout = null)
    {
        var directory = new TemporaryDirectory();
        _directories.Add(directory);
        return new StatusPoller(
            providers,
            settings ?? Settings,
            new RollingFileLog(Path.Combine(directory.Path, "poller.log")),
            timeProvider,
            providerTimeout);
    }

    private static AppSettings Settings() => AppSettings.Default;

    private static void AssertRetained(
        ProviderSnapshot expected,
        ProviderSnapshot actual,
        HealthState expectedHealth,
        int expectedFailures)
    {
        Assert.Equal(expected.PlanLabel, actual.PlanLabel);
        Assert.Equal(expected.Windows, actual.Windows);
        Assert.Equal(expected.Info, actual.Info);
        Assert.Equal(expected.FetchedAt, actual.FetchedAt);
        Assert.Equal(expectedHealth, actual.Health);
        Assert.Equal(expectedFailures, actual.ConsecutiveFailures);
    }

    public void Dispose()
    {
        foreach (TemporaryDirectory directory in _directories)
        {
            directory.Dispose();
        }
    }

    private sealed class AvailabilityStatusProvider(string id) : IStatusProvider, IProviderAvailability
    {
        private int _availabilityCount;
        private int _fetchCount;

        public string Id { get; } = id;

        public string Label => Id;

        public int AvailabilityCount => Volatile.Read(ref _availabilityCount);

        public int FetchCount => Volatile.Read(ref _fetchCount);

        public Func<CancellationToken, Task<bool>> Availability { get; init; } =
            _ => Task.FromResult(true);

        public Func<int, CancellationToken, Task<ProviderFetchResult>>? Fetch { get; init; }

        public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _availabilityCount);
            return await Availability(cancellationToken);
        }

        public Task<ProviderFetchResult> FetchAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int invocation = Interlocked.Increment(ref _fetchCount);
            return Fetch is null
                ? Task.FromResult(new ProviderFetchResult(
                    ProviderFetchOutcome.Success,
                    FakeStatusProvider.Snapshot(Id)))
                : Fetch(invocation, cancellationToken);
        }
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();
        private readonly TaskCompletionSource _posted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int PendingCount => _callbacks.Count;

        public Task Posted => _posted.Task;

        public override void Post(SendOrPostCallback d, object? state)
        {
            _callbacks.Enqueue((d, state));
            _posted.TrySetResult();
        }

        public void RunOne()
        {
            Assert.True(_callbacks.TryDequeue(out var callback));
            callback.Callback(callback.State);
        }
    }

    private sealed class DedicatedThreadSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _callbacks = [];
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread _thread;

        public DedicatedThreadSynchronizationContext()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "ReservePane.Tests UI context",
            };
            _thread.Start();
            _ready.Task.Wait(TimeSpan.FromSeconds(2));
        }

        public int ThreadId { get; private set; }

        public override void Post(SendOrPostCallback callback, object? state) =>
            _callbacks.Add((callback, state));

        public Task<T> InvokeAsync<T>(Func<T> action)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(
                _ =>
                {
                    try
                    {
                        completion.TrySetResult(action());
                    }
                    catch (Exception exception)
                    {
                        completion.TrySetException(exception);
                    }
                },
                null);
            return completion.Task;
        }

        public void Dispose()
        {
            _callbacks.CompleteAdding();
            Assert.True(_thread.Join(TimeSpan.FromSeconds(2)), "The dedicated synchronization-context thread did not stop.");
            _callbacks.Dispose();
        }

        private void Run()
        {
            SynchronizationContext.SetSynchronizationContext(this);
            ThreadId = Environment.CurrentManagedThreadId;
            _ready.TrySetResult();
            foreach ((SendOrPostCallback callback, object? state) in _callbacks.GetConsumingEnumerable())
            {
                callback(state);
            }
        }
    }

    private sealed class QueueThenThrowSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();
        private int _postCount;
        private int _executing;

        public bool IsExecuting => Volatile.Read(ref _executing) != 0;

        public TaskCompletionSource FirstPostAttempted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondPostQueued { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Post(SendOrPostCallback d, object? state)
        {
            _callbacks.Enqueue((d, state));
            int post = Interlocked.Increment(ref _postCount);
            if (post == 1)
            {
                FirstPostAttempted.TrySetResult();
                throw new InvalidOperationException("dispatch secret");
            }

            if (post == 2)
            {
                SecondPostQueued.TrySetResult();
            }
        }

        public void RunAll()
        {
            while (_callbacks.TryDequeue(out var callback))
            {
                Volatile.Write(ref _executing, 1);
                try
                {
                    callback.Callback(callback.State);
                }
                finally
                {
                    Volatile.Write(ref _executing, 0);
                }
            }
        }
    }

    private sealed class RecordingTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<RecordingTimer> _timers = [];
        private DateTimeOffset _utcNow = DateTimeOffset.Parse("2026-08-27T12:00:00Z");

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow += amount;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new RecordingTimer(callback, state, dueTime, period);
            lock (_gate)
            {
                _timers.Add(timer);
            }

            return timer;
        }

        public async Task<RecordingTimer> WaitForTimerAsync(TimeSpan dueTime, TimeSpan period)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            while (true)
            {
                lock (_gate)
                {
                    RecordingTimer? timer = _timers.LastOrDefault(candidate =>
                        !candidate.IsDisposed && candidate.DueTime == dueTime && candidate.Period == period);
                    if (timer is not null)
                    {
                        return timer;
                    }
                }

                await Task.Delay(1, timeout.Token);
            }
        }

        public bool HasActiveTimer(TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                return _timers.Any(timer =>
                    !timer.IsDisposed && timer.DueTime == dueTime && timer.Period == period);
            }
        }

        public int CountActiveTimers(TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                return _timers.Count(timer =>
                    !timer.IsDisposed && timer.DueTime == dueTime && timer.Period == period);
            }
        }

        public void FireTimers(TimeSpan dueTime, TimeSpan period)
        {
            RecordingTimer[] timers;
            lock (_gate)
            {
                timers = _timers.Where(timer =>
                    !timer.IsDisposed && timer.DueTime == dueTime && timer.Period == period).ToArray();
            }

            foreach (RecordingTimer timer in timers)
            {
                timer.Fire();
            }
        }
    }

    private sealed class GatedTimeProvider : TimeProvider, IDisposable
    {
        private readonly ManualResetEventSlim _timerCreationReleased = new();

        public TaskCompletionSource TimerCreationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TimerCreated { get; private set; }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            TimerCreationStarted.TrySetResult();
            _timerCreationReleased.Wait(TimeSpan.FromSeconds(1));
            TimerCreated = true;
            return new RecordingTimer(callback, state, dueTime, period);
        }

        public void ReleaseTimerCreation() => _timerCreationReleased.Set();

        public void Dispose() => _timerCreationReleased.Dispose();
    }

    private sealed class RecordingTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period) : ITimer
    {
        private int _disposed;

        public TimeSpan DueTime { get; private set; } = dueTime;

        public TimeSpan Period { get; private set; } = period;

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public TaskCompletionSource Disposed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (IsDisposed)
            {
                return false;
            }

            DueTime = dueTime;
            Period = period;
            return true;
        }

        public void Fire()
        {
            if (!IsDisposed)
            {
                callback(state);
            }
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
            Disposed.TrySetResult();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
