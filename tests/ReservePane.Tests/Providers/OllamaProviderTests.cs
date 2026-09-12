using System.Net;
using System.Text;
using Renci.SshNet;
using ReservePane.Model;
using ReservePane.Providers;
using ReservePane.Tests.Support;

namespace ReservePane.Tests.Providers;

public sealed class OllamaProviderTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    [Fact]
    public async Task FetchAsync_SignsOnlyBodylessCloudUsageRequest()
    {
        string keyPath = OllamaTestIdentity.Write(_directory);
        using var key = new PrivateKeyFile(keyPath);
        var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Content);
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal("ollama.com", request.RequestUri.Host);
            Assert.Equal("/api/usage", request.RequestUri.AbsolutePath);
            Assert.StartsWith("?ts=", request.RequestUri.Query);
            string[] authorization = Assert.Single(request.Headers.GetValues("Authorization")).Split(':');
            Assert.Equal(Convert.ToBase64String(key.HostKeyAlgorithms.Single().Data), authorization[0]);
            Assert.True(key.Key.VerifySignature(Encoding.UTF8.GetBytes("GET," + request.RequestUri.PathAndQuery),
                Convert.FromBase64String(authorization[1])));
            Assert.True(request.Headers.CacheControl?.NoStore);
            return JsonResponse("""{"limits":{"session":{"usage":0.25},"weekly":{"usage":0.9}}}""");
        });
        ProviderFetchResult result = await CreateProvider(handler, keyPath).FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderFetchOutcome.Success, result.Outcome);
        Assert.Equal("Ollama Cloud", result.Snapshot!.Label);
        Assert.Collection(result.Snapshot.Windows,
            window => Assert.Equal(new UsageWindow("Session", 25, null, Severity.Normal), window),
            window => Assert.Equal(new UsageWindow("Weekly", 90, null, Severity.Warning), window));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task IsAvailableAsync_UsesLocalIdentityWithoutNetwork()
    {
        var handler = NoRequests();
        Assert.True(await CreateProvider(handler).IsAvailableAsync(CancellationToken.None));
        Assert.False(await CreateProvider(handler, MissingKey).IsAvailableAsync(CancellationToken.None));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_MissingIdentityIsNotConfigured()
    {
        ProviderFetchResult result = await CreateProvider(NoRequests(), MissingKey).FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderFetchOutcome.NotConfigured, result.Outcome);
        Assert.Contains("ollama signin", result.Snapshot!.Error);
    }

    [Fact]
    public async Task FetchAsync_InvalidIdentityRequiresSignInWithoutNetwork()
    {
        ProviderFetchResult result = await CreateProvider(NoRequests(), _directory.WriteFile("invalid", "sensitive-key"))
            .FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderFetchOutcome.AuthenticationRequired, result.Outcome);
        Assert.DoesNotContain("sensitive", result.Snapshot!.Error);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task FetchAsync_RejectedIdentityRequiresSignIn(HttpStatusCode status)
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(status));
        ProviderFetchResult result = await CreateProvider(handler).FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderFetchOutcome.AuthenticationRequired, result.Outcome);
        Assert.Equal(HealthState.AuthExpired, result.Snapshot!.Health);
        Assert.Contains("ollama signin", result.Snapshot.Error);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_OversizedIdentityNeverMakesRequest()
    {
        ProviderFetchResult result = await CreateProvider(NoRequests(), _directory.WriteFile("oversized", new string('x', 65_537)))
            .FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderFetchOutcome.AuthenticationRequired, result.Outcome);
    }

    [Fact]
    public async Task FetchAsync_OversizedUsageIsInvalidResponse()
    {
        ProviderFetchResult result = await CreateProvider(new StubHttpMessageHandler(_ =>
            JsonResponse(new string(' ', ProviderHttpSafety.MaximumJsonBytes + 1))))
            .FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderFetchOutcome.InvalidResponse, result.Outcome);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"limits\":{\"weekly\":{}}}")]
    [InlineData("{\"limits\":{\"weekly\":{\"usage\":null}}}")]
    [InlineData("{\"limits\":{\"weekly\":{\"usage\":-1}}}")]
    [InlineData("{\"limits\":{\"weekly\":{\"usage\":1.1}}}")]
    [InlineData("{\"limits\":{\"weekly\":{\"usage\":\"0.5\"}}}")]
    [InlineData("{\"limits\":{\"monthly\":{\"usage\":null}}}")]
    [InlineData("{\"limits\":{\"monthly\":{\"usage\":-1}}}")]
    [InlineData("{\"limits\":{\"monthly\":{\"usage\":\"0.5\"}}}")]
    [InlineData("{\"limits\":{\"monthly\":{\"usage\":1e308}}}")]
    [InlineData("{\"limits\":")]
    public async Task FetchAsync_InvalidUsageNeverInventsCapacity(string json)
    {
        ProviderFetchResult result = await CreateProvider(new StubHttpMessageHandler(_ => JsonResponse(json)))
            .FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderFetchOutcome.InvalidResponse, result.Outcome);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task FetchAsync_ValidWindowSurvivesInvalidOtherWindow()
    {
        ProviderFetchResult result = await CreateProvider(new StubHttpMessageHandler(_ =>
            JsonResponse("""{"limits":{"session":{"usage":null},"weekly":{"usage":0}}}""")))
            .FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderFetchOutcome.PartialSuccess, result.Outcome);
        Assert.Equal(0, Assert.Single(result.Snapshot!.Windows).Percent);
    }

    [Theory]
    [InlineData("0", 0, Severity.Normal)]
    [InlineData("0.375", 37.5, Severity.Normal)]
    [InlineData("0.925", 92.5, Severity.Warning)]
    [InlineData("1", 100, Severity.Warning)]
    [InlineData("1.125", 112.5, Severity.Warning)]
    public async Task FetchAsync_MonthlyFractionDisplaysUsedPercentage(string usage, double expectedPercent,
        Severity expectedSeverity)
    {
        ProviderFetchResult result = await CreateProvider(new StubHttpMessageHandler(_ =>
            JsonResponse("""{"limits":{"monthly":{"usage":VALUE}}}""".Replace("VALUE", usage))))
            .FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderFetchOutcome.Success, result.Outcome);
        UsageWindow monthly = Assert.Single(result.Snapshot!.Windows);
        Assert.Equal(new UsageWindow("Monthly", expectedPercent, null, expectedSeverity), monthly);
        Assert.Equal(HealthState.Ok, result.Snapshot.Health);
        Assert.Null(result.Snapshot.Error);
    }

    [Fact]
    public async Task FetchAsync_MonthlyUsageIgnoresUnrelatedActivityCostsAndPeriods()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ReservePane.slnx")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        string fixture = File.ReadAllText(Path.Combine(directory.FullName,
            "tests", "ReservePane.Tests", "Fixtures", "ollama-cloud-monthly.json"));
        ProviderFetchResult result = await CreateProvider(new StubHttpMessageHandler(_ => JsonResponse(fixture)))
            .FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderFetchOutcome.Success, result.Outcome);
        UsageWindow monthly = Assert.Single(result.Snapshot!.Windows);
        Assert.Equal(37.5, monthly.Percent);
        Assert.Null(monthly.ResetsAt);
        Assert.Empty(result.Snapshot.Info);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "30", 30)]
    [InlineData(HttpStatusCode.ServiceUnavailable, null, 300)]
    public async Task FetchAsync_RateLimitReturnsSafeCooldown(HttpStatusCode status, string? retryAfter, int seconds)
    {
        var handler = new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(status);
            if (retryAfter is not null) response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            return response;
        });
        ProviderFetchResult result = await CreateProvider(handler).FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderFetchOutcome.RateLimited, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(seconds), result.RetryAfter);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_ConnectionFailureReturnsTransientFailure()
    {
        ProviderFetchResult result = await CreateProvider(new StubHttpMessageHandler(_ => throw new HttpRequestException("sensitive response")))
            .FetchAsync(CancellationToken.None);
        Assert.Equal(ProviderFetchOutcome.TransientFailure, result.Outcome);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task FetchAsync_CallerCancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateProvider(NoRequests()).FetchAsync(cancellation.Token));
    }

    public void Dispose() => _directory.Dispose();
    private string MissingKey => Path.Combine(_directory.Path, "missing");
    private static StubHttpMessageHandler NoRequests() => new(_ => throw new InvalidOperationException("No network request allowed."));
    private OllamaProvider CreateProvider(HttpMessageHandler handler, string? keyPath = null) => new(
        keyPath ?? OllamaTestIdentity.Write(_directory), handler,
        percent => percent >= 80 ? Severity.Warning : Severity.Normal);
    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
}
