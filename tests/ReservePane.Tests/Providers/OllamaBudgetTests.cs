using System.Net;
using System.Text;
using System.Text.Json;
using ReservePane.Model;
using ReservePane.Providers;
using ReservePane.Tests.Support;

namespace ReservePane.Tests.Providers;

public sealed class OllamaBudgetTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    [Theory]
    [InlineData("pro", "Pro", "USD 22.50", "USD 60.00")]
    [InlineData(" MAX ", "Max", "USD 112.50", "USD 300.00")]
    [InlineData("team", "Team", "USD 375.00", "USD 1000.00")]
    public async Task FetchAsync_MonthlyPaidPlanShowsEstimatedSpendAndPublishedBudget(
        string plan, string label, string spend, string budget)
    {
        var handler = CreateHandler("0.375", () => JsonResponse(JsonSerializer.Serialize(new { plan })));
        ProviderFetchResult result = await CreateProvider(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.Success, result.Outcome);
        Assert.Equal(label, result.Snapshot!.PlanLabel);
        Assert.Equal(37.5, Assert.Single(result.Snapshot.Windows).Percent);
        Assert.Collection(result.Snapshot.Info,
            line => Assert.Equal(new InfoLine("Estimated spend", spend), line),
            line => Assert.Equal(new InfoLine("Budget", budget), line));
        Assert.Equal(2, handler.RequestCount);
    }

    [Theory]
    [InlineData("0", "USD 0.00")]
    [InlineData("1.125", "USD 337.50")]
    [InlineData("0.00005", "USD 0.02")]
    public async Task FetchAsync_EstimateHandlesZeroOverageAndCentRounding(string fraction, string spend)
    {
        var handler = CreateHandler(fraction, () => JsonResponse("""{"plan":"max"}"""));
        ProviderFetchResult result = await CreateProvider(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.Success, result.Outcome);
        Assert.Equal(spend, Assert.Single(result.Snapshot!.Info, line => line.Label == "Estimated spend").Value);
    }

    [Fact]
    public async Task FetchAsync_NativeAccountPropertyCasingShowsBudget()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ReservePane.slnx")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        string fixture = File.ReadAllText(Path.Combine(directory.FullName,
            "tests", "ReservePane.Tests", "Fixtures", "ollama-account-max.json"));
        var handler = CreateHandler("0.375", () => JsonResponse(fixture));
        ProviderFetchResult result = await CreateProvider(handler).FetchAsync(CancellationToken.None);

        Assert.Equal("Max", result.Snapshot!.PlanLabel);
        Assert.Equal("USD 300.00", result.Snapshot.Info.Single(line => line.Label == "Budget").Value);
        Assert.Equal("USD 112.50", result.Snapshot.Info.Single(line => line.Label == "Estimated spend").Value);
    }

    [Theory]
    [InlineData("free", "Free")]
    [InlineData("", null)]
    [InlineData("sentinel-private-plan", null)]
    public async Task FetchAsync_UnknownAllowanceKeepsPercentageWithoutInventingMoney(string plan, string? label)
    {
        var handler = CreateHandler("0.375", () => JsonResponse(JsonSerializer.Serialize(new { plan })));
        ProviderFetchResult result = await CreateProvider(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.Success, result.Outcome);
        Assert.Equal(37.5, Assert.Single(result.Snapshot!.Windows).Percent);
        Assert.Equal(label, result.Snapshot.PlanLabel);
        Assert.Empty(result.Snapshot.Info);
        Assert.DoesNotContain("sentinel-private", result.Snapshot.ToString());
    }

    [Fact]
    public async Task FetchAsync_PlanChangeRefreshesBudgetWithoutCachedEstimates()
    {
        string plan = "max";
        var handler = CreateHandler("0.375", () => JsonResponse(JsonSerializer.Serialize(new { plan })));
        OllamaProvider provider = CreateProvider(handler);
        ProviderFetchResult first = await provider.FetchAsync(CancellationToken.None);
        plan = "pro";
        ProviderFetchResult second = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal("USD 300.00", first.Snapshot!.Info.Single(line => line.Label == "Budget").Value);
        Assert.Equal("USD 60.00", second.Snapshot!.Info.Single(line => line.Label == "Budget").Value);
        Assert.Equal("USD 22.50", second.Snapshot.Info.Single(line => line.Label == "Estimated spend").Value);
        Assert.Equal(4, handler.RequestCount);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"plan\":42}")]
    [InlineData("{\"plan\":")]
    public async Task FetchAsync_InvalidPlanRetainsFreshPercentageWithoutBudget(string accountJson)
    {
        var handler = CreateHandler("0.375", () => JsonResponse(accountJson));
        ProviderFetchResult result = await CreateProvider(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.PartialSuccess, result.Outcome);
        Assert.Equal(37.5, Assert.Single(result.Snapshot!.Windows).Percent);
        Assert.Empty(result.Snapshot.Info);
        Assert.Equal("Plan unavailable. Refresh to retry.", result.Snapshot.Error);
    }

    [Fact]
    public async Task FetchAsync_FailedPlanRefreshClearsPreviousEstimateAndKeepsCurrentUsage()
    {
        bool fail = false;
        var handler = CreateHandler("0.375", () => fail
            ? throw new HttpRequestException("sentinel-private-account")
            : JsonResponse("""{"plan":"max"}"""));
        OllamaProvider provider = CreateProvider(handler);
        await provider.FetchAsync(CancellationToken.None);
        fail = true;
        ProviderFetchResult result = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.PartialSuccess, result.Outcome);
        Assert.Equal(37.5, Assert.Single(result.Snapshot!.Windows).Percent);
        Assert.Empty(result.Snapshot.Info);
        Assert.Null(result.Snapshot.PlanLabel);
        Assert.DoesNotContain("sentinel-private", result.Snapshot.Error);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ProviderFetchOutcome.AuthenticationRequired)]
    [InlineData(HttpStatusCode.Forbidden, ProviderFetchOutcome.AuthenticationRequired)]
    [InlineData(HttpStatusCode.BadGateway, ProviderFetchOutcome.PartialSuccess)]
    [InlineData(HttpStatusCode.TooManyRequests, ProviderFetchOutcome.RateLimited)]
    public async Task FetchAsync_PlanHttpFailurePreservesAuthenticationAndCooldownRules(
        HttpStatusCode status, ProviderFetchOutcome outcome)
    {
        var handler = CreateHandler("0.375", () =>
        {
            var response = new HttpResponseMessage(status);
            if (status == HttpStatusCode.TooManyRequests) response.Headers.Add("Retry-After", "45");
            return response;
        });
        ProviderFetchResult result = await CreateProvider(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(status, result.StatusCode);
        if (outcome == ProviderFetchOutcome.RateLimited)
            Assert.Equal(TimeSpan.FromSeconds(45), result.RetryAfter);
        else
        {
            Assert.Equal(37.5, Assert.Single(result.Snapshot!.Windows).Percent);
            Assert.Empty(result.Snapshot.Info);
        }
    }

    [Fact]
    public async Task FetchAsync_CancellationDuringPlanLookupPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = CreateHandler("0.375", () =>
        {
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
            throw new InvalidOperationException();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateProvider(handler).FetchAsync(cancellation.Token));
    }

    [Theory]
    [InlineData("1e28")]
    [InlineData("1e29")]
    public async Task FetchAsync_UnrepresentableSpendKeepsBudgetAndPercentage(string fraction)
    {
        var handler = CreateHandler(fraction, () => JsonResponse("""{"plan":"max"}"""));
        ProviderFetchResult result = await CreateProvider(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.PartialSuccess, result.Outcome);
        Assert.Equal("Unavailable", result.Snapshot!.Info.Single(line => line.Label == "Estimated spend").Value);
        Assert.Equal("USD 300.00", result.Snapshot.Info.Single(line => line.Label == "Budget").Value);
        Assert.True(Assert.Single(result.Snapshot.Windows).Percent > 100);
    }

    [Fact]
    public async Task FetchAsync_OversizedPlanKeepsUsageWithoutBudget()
    {
        var handler = CreateHandler("0.375", () => JsonResponse(new string(' ', ProviderHttpSafety.MaximumJsonBytes + 1)));
        ProviderFetchResult result = await CreateProvider(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.PartialSuccess, result.Outcome);
        Assert.Equal(37.5, Assert.Single(result.Snapshot!.Windows).Percent);
        Assert.Empty(result.Snapshot.Info);
    }

    public void Dispose() => _directory.Dispose();

    private OllamaProvider CreateProvider(HttpMessageHandler handler) => new(
        OllamaTestIdentity.Write(_directory), handler, _ => Severity.Normal);

    private static StubHttpMessageHandler CreateHandler(string fraction, Func<HttpResponseMessage> accountResponse) =>
        new(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/usage" => JsonResponse("""{"limits":{"monthly":{"usage":VALUE}}}""".Replace("VALUE", fraction)),
            "/api/me" => accountResponse(),
            _ => throw new InvalidOperationException("Unexpected endpoint."),
        });

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
}
