using System.Numerics;
using ReservePane.Model;
using ReservePane.Providers;
using ReservePane.Tests.Support;

namespace ReservePane.Tests.Providers;

public sealed class OpenCodeCompanySeatProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Reset = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
    private static readonly OpenCodeConsoleActiveWorkspace Workspace = new(
        "account-active",
        "access-active",
        "org-active",
        Now.AddHours(1));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IsAvailableAsync_ReturnsOpenCodeCommandPresence(bool commandAvailable)
    {
        // Catches discovery reading Console state instead of checking the local command prerequisite.
        var provider = new OpenCodeCompanySeatProvider(
            new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run")),
            SeverityFromPercent,
            new FixedTimeProvider(Now),
            new StubWorkspaceReader(() => throw new Xunit.Sdk.XunitException("Workspace must not be read")),
            new StubCompanySeatClient(() => throw new Xunit.Sdk.XunitException("Client must not run")),
            command => command == "opencode" && commandAvailable);

        IProviderAvailability availability = provider;
        bool result = await availability.IsAvailableAsync(CancellationToken.None);

        Assert.Equal(commandAvailable, result);
    }

    [Fact]
    public async Task FetchAsync_PositiveBudgetBuildsCompanySeatSnapshotWithUsdInformation()
    {
        // Catches valid member spend being labeled as organization-wide data or converted with the wrong unit.
        OpenCodeCompanySeatProvider provider = CreateProvider(new OpenCodeCompanySeatBudget(
            new BigInteger(1_000_000_000),
            new BigInteger(250_000_000),
            false,
            Reset,
            "default"));

        ProviderFetchResult result = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.Success, result.Outcome);
        ProviderSnapshot snapshot = Assert.IsType<ProviderSnapshot>(result.Snapshot);
        Assert.Equal("opencode-company-seat", snapshot.Id);
        Assert.Equal("OpenCode", snapshot.Label);
        Assert.Equal("Company Seat", snapshot.PlanLabel);
        Assert.Equal(HealthState.Ok, snapshot.Health);
        UsageWindow window = Assert.Single(snapshot.Windows);
        Assert.Equal("monthly budget", window.Label);
        Assert.Equal(25, window.Percent);
        Assert.Equal(Reset, window.ResetsAt);
        Assert.Equal(Severity.Normal, window.Severity);
        Assert.Collection(
            snapshot.Info,
            line => Assert.Equal(new InfoLine("Spend", "USD 2.50"), line),
            line => Assert.Equal(new InfoLine("Budget", "USD 10.00"), line));
        Assert.DoesNotContain("organization", snapshot.Label, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("organization", snapshot.PlanLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(5_000_000_000L, "USD 50.00")]
    [InlineData(123_499_999L, "USD 1.23")]
    [InlineData(123_500_000L, "USD 1.24")]
    [InlineData(99_500_000L, "USD 1.00")]
    public async Task FetchAsync_TwoHundredDollarBudgetConvertsMicrocentsAndRoundsSpend(
        long spentMicroCents,
        string expectedSpend)
    {
        OpenCodeCompanySeatProvider provider = CreateProvider(new OpenCodeCompanySeatBudget(
            new BigInteger(20_000_000_000L),
            new BigInteger(spentMicroCents),
            false,
            Reset,
            "custom"));

        ProviderFetchResult result = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.Success, result.Outcome);
        Assert.Collection(
            result.Snapshot!.Info,
            line => Assert.Equal(new InfoLine("Spend", expectedSpend), line),
            line => Assert.Equal(new InfoLine("Budget", "USD 200.00"), line));
    }

    [Fact]
    public async Task FetchAsync_OverBudgetUtilizationIsNotClamped()
    {
        // Catches over-budget member spend being hidden by a 100 percent UI clamp.
        OpenCodeCompanySeatProvider provider = CreateProvider(new OpenCodeCompanySeatBudget(
            new BigInteger(100_000_000),
            new BigInteger(150_000_000),
            true,
            Reset,
            "custom"));

        ProviderSnapshot snapshot = Assert.IsType<ProviderSnapshot>(
            (await provider.FetchAsync(CancellationToken.None)).Snapshot);

        UsageWindow window = Assert.Single(snapshot.Windows);
        Assert.Equal(150, window.Percent);
        Assert.Equal(Severity.Critical, window.Severity);
        Assert.Equal(HealthState.Ok, snapshot.Health);
    }

    [Fact]
    public async Task FetchAsync_VeryLargeMicrocentValuesCalculateWithoutIntermediateOverflow()
    {
        // Catches decimal or integer overflow changing a valid large ratio into an invalid response.
        BigInteger limit = BigInteger.Pow(10, 1000);
        BigInteger spent = limit / 2;
        OpenCodeCompanySeatProvider provider = CreateProvider(new OpenCodeCompanySeatBudget(
            limit,
            spent,
            false,
            Reset,
            "default"));

        ProviderFetchResult result = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.Success, result.Outcome);
        Assert.Equal(50, Assert.Single(result.Snapshot!.Windows).Percent);
    }

    [Theory]
    [InlineData(false, HealthState.Ok, Severity.Normal, null)]
    [InlineData(true, HealthState.Degraded, Severity.Critical, "Monthly budget exceeded")]
    public async Task FetchAsync_ZeroBudgetUsesServerExceededStateWithoutDividing(
        bool exceeded,
        HealthState expectedHealth,
        Severity expectedSeverity,
        string? expectedError)
    {
        // Catches a zero effective limit causing division by zero or discarding server health.
        OpenCodeCompanySeatProvider provider = CreateProvider(new OpenCodeCompanySeatBudget(
            BigInteger.Zero,
            new BigInteger(50_000_000),
            exceeded,
            Reset,
            "custom"));

        ProviderSnapshot snapshot = Assert.IsType<ProviderSnapshot>(
            (await provider.FetchAsync(CancellationToken.None)).Snapshot);

        Assert.Equal(expectedHealth, snapshot.Health);
        Assert.Equal(expectedError, snapshot.Error);
        UsageWindow window = Assert.Single(snapshot.Windows);
        Assert.Null(window.Percent);
        Assert.Equal(expectedSeverity, window.Severity);
        Assert.Contains(new InfoLine("Spend", "USD 0.50"), snapshot.Info);
        Assert.Contains(new InfoLine("Budget", "USD 0.00"), snapshot.Info);
    }

    [Fact]
    public async Task FetchAsync_NullBudgetReportsNoEffectiveMemberBudget()
    {
        // Catches a 200 null response being treated as transport failure or an organization-wide zero.
        OpenCodeCompanySeatProvider provider = CreateProvider(null);

        ProviderFetchResult result = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.PartialSuccess, result.Outcome);
        ProviderSnapshot snapshot = Assert.IsType<ProviderSnapshot>(result.Snapshot);
        Assert.Equal(HealthState.Degraded, snapshot.Health);
        Assert.Equal("No effective member budget is configured", snapshot.Error);
        Assert.Empty(snapshot.Windows);
        Assert.Equal(new InfoLine("Budget", "Not configured"), Assert.Single(snapshot.Info));
    }

    [Fact]
    public async Task FetchAsync_MissingEffectiveLimitShowsAvailableSpendWithoutUtilization()
    {
        // Catches an inherited account with no effective limit losing valid spend information.
        OpenCodeCompanySeatProvider provider = CreateProvider(new OpenCodeCompanySeatBudget(
            null,
            new BigInteger(125_000_000),
            false,
            Reset,
            "default"));

        ProviderFetchResult result = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.PartialSuccess, result.Outcome);
        ProviderSnapshot snapshot = Assert.IsType<ProviderSnapshot>(result.Snapshot);
        Assert.Null(Assert.Single(snapshot.Windows).Percent);
        Assert.Contains(new InfoLine("Spend", "USD 1.25"), snapshot.Info);
        Assert.Contains(new InfoLine("Budget", "Not configured"), snapshot.Info);
    }

    [Fact]
    public async Task FetchAsync_MissingActiveWorkspaceIsSanitizedAndDoesNotCallHttp()
    {
        // Catches disabled or incomplete Console state issuing requests with empty credentials.
        var handler = new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("HTTP was not expected."));
        var provider = new OpenCodeCompanySeatProvider(
            handler,
            SeverityFromPercent,
            new FixedTimeProvider(Now),
            new StubWorkspaceReader(() => null),
            new StubCompanySeatClient(() => throw new InvalidOperationException("Client was not expected.")));

        ProviderFetchResult result = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.NotConfigured, result.Outcome);
        Assert.Equal(HealthState.Unreachable, result.Snapshot!.Health);
        Assert.Empty(result.Snapshot.Windows);
        Assert.Equal(0, handler.RequestCount);
    }

    private static OpenCodeCompanySeatProvider CreateProvider(OpenCodeCompanySeatBudget? budget) => new(
        new StubHttpMessageHandler(_ => throw new InvalidOperationException("HTTP was not expected.")),
        SeverityFromPercent,
        new FixedTimeProvider(Now),
        new StubWorkspaceReader(() => Workspace),
        new StubCompanySeatClient(() => new OpenCodeCompanySeatFetchResult(
            OpenCodeCompanySeatFetchOutcome.Success,
            budget)));

    private static Severity SeverityFromPercent(double? percent) =>
        SeverityPolicy.FromPercent(percent, 80, 95);

    private sealed class StubWorkspaceReader(Func<OpenCodeConsoleActiveWorkspace?> read)
        : IOpenCodeConsoleActiveWorkspaceReader
    {
        public Task<OpenCodeConsoleActiveWorkspaceReadResult> ReadAsync(
            CancellationToken cancellationToken) => Task.FromResult(new OpenCodeConsoleActiveWorkspaceReadResult(
                OpenCodeConsoleActiveWorkspaceReadOutcome.Success,
                read()));
    }

    private sealed class StubCompanySeatClient(Func<OpenCodeCompanySeatFetchResult> fetch)
        : IOpenCodeCompanySeatClient
    {
        public Task<OpenCodeCompanySeatFetchResult> FetchAsync(
            OpenCodeConsoleActiveWorkspace workspace,
            CancellationToken cancellationToken) => Task.FromResult(fetch());
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
