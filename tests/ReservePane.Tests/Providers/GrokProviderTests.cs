using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ReservePane.Model;
using ReservePane.Providers;
using ReservePane.Tests.Support;

namespace ReservePane.Tests.Providers;

public sealed class GrokProviderTests : IDisposable
{
    private readonly List<TemporaryDirectory> _directories = [];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IsAvailableAsync_ReturnsCredentialFilePresence(bool credentialExists)
    {
        // Catches discovery parsing credential contents or ignoring the configured credential path.
        var provider = new GrokProvider(
            "auth.json",
            new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run")),
            percent => SeverityPolicy.FromPercent(percent, 80, 95),
            null,
            _ => throw new Xunit.Sdk.XunitException("Credential contents must not be read"),
            path => path == "auth.json" && credentialExists);

        IProviderAvailability availability = provider;
        bool result = await availability.IsAvailableAsync(CancellationToken.None);

        Assert.Equal(credentialExists, result);
    }

    [Fact]
    public async Task IsAvailableAsync_ConfirmedCredentialDirectoryAbsenceReturnsUnavailable()
    {
        // Catches a missing credential directory being treated as indeterminate local presence.
        var provider = new GrokProvider(
            "auth.json",
            new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run")),
            percent => SeverityPolicy.FromPercent(percent, 80, 95),
            null,
            _ => throw new Xunit.Sdk.XunitException("Credential contents must not be read"),
            _ => throw new DirectoryNotFoundException());

        bool result = await ((IProviderAvailability)provider)
            .IsAvailableAsync(CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task IsAvailableAsync_TransientCredentialIoFailureRemainsAvailable()
    {
        // Catches a transient storage failure being collapsed into confirmed credential absence.
        var provider = new GrokProvider(
            "auth.json",
            new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run")),
            percent => SeverityPolicy.FromPercent(percent, 80, 95),
            null,
            _ => throw new Xunit.Sdk.XunitException("Credential contents must not be read"),
            _ => throw new IOException("credential-path-must-not-be-reported"));

        bool result = await ((IProviderAvailability)provider)
            .IsAvailableAsync(CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task FetchAsync_MapsWeeklyCreditsProductsAndOnDemand()
    {
        // Catches a provider that drops the weekly window, product percents, or a positive on-demand cap.
        ProviderSnapshot snapshot = await CreateProvider("grok-credits.json", "application/json")
            .FetchSnapshotAsync(CancellationToken.None);

        Assert.Equal("grok", snapshot.Id);
        Assert.Equal("Grok", snapshot.Label);
        Assert.Equal(HealthState.Ok, snapshot.Health);
        Assert.Equal("SuperGrok Heavy", snapshot.PlanLabel);
        Assert.Collection(
            snapshot.Windows,
            weekly =>
            {
                Assert.Equal("weekly", weekly.Label);
                Assert.Equal(42.5, weekly.Percent);
                Assert.Equal(DateTimeOffset.Parse("2026-06-08T00:00:00Z"), weekly.ResetsAt);
                Assert.Equal(Severity.Normal, weekly.Severity);
            },
            grokBuild =>
            {
                Assert.Equal("GrokBuild", grokBuild.Label);
                Assert.Equal(40.0, grokBuild.Percent);
                Assert.Equal(DateTimeOffset.Parse("2026-06-08T00:00:00Z"), grokBuild.ResetsAt);
                Assert.Equal(Severity.Normal, grokBuild.Severity);
            },
            onDemand =>
            {
                Assert.Equal("on-demand", onDemand.Label);
                Assert.Equal(6, onDemand.Percent);
                Assert.Null(onDemand.ResetsAt);
                Assert.Equal(Severity.Normal, onDemand.Severity);
            });
        Assert.DoesNotContain(snapshot.Windows, window => window.Label == "GrokChat");
    }

    [Fact]
    public async Task FetchAsync_AddsRequiredAuthenticationHeaders()
    {
        // Catches a request that omits the CLI gate header or uses an incorrect bearer credential.
        AuthenticationHeaderValue? authorization = null;
        string? tokenAuth = null;
        Uri? requestUri = null;
        string[] acceptedMediaTypes = [];
        var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Content);
            authorization = request.Headers.Authorization;
            tokenAuth = request.Headers.GetValues("x-xai-token-auth").Single();
            requestUri = request.RequestUri;
            acceptedMediaTypes = request.Headers.Accept.Select(value => value.MediaType!).ToArray();
            return JsonResponse(ReadFixture("grok-credits.json"), "application/json");
        });

        ProviderSnapshot snapshot = await CreateProvider(handler).FetchSnapshotAsync(CancellationToken.None);

        Assert.Equal(HealthState.Ok, snapshot.Health);
        Assert.Equal("Bearer", authorization?.Scheme);
        Assert.Equal("unit-test-access-token", authorization?.Parameter);
        Assert.Equal("xai-grok-cli", tokenAuth);
        Assert.Contains("application/json", acceptedMediaTypes);
        Assert.Equal("https://cli-chat-proxy.grok.com/v1/billing?format=credits", requestUri?.ToString());
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_HtmlUnder200ReturnsInvalidResponse()
    {
        // Catches a provider that attempts to deserialize a successful HTML response.
        ProviderFetchResult result = await CreateProvider("grok-html-200.html", "text/html")
            .FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.InvalidResponse, result.Outcome);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task FetchAsync_AddsNoStoreToUsageRequest()
    {
        // Catches pooled clients that permit credential-bound usage responses to be cached.
        bool noStore = false;
        var handler = new StubHttpMessageHandler(request =>
        {
            noStore = request.Headers.CacheControl?.NoStore == true;
            return JsonResponse(ReadFixture("grok-credits.json"), "application/json");
        });

        ProviderSnapshot snapshot = await CreateProvider(handler).FetchSnapshotAsync(CancellationToken.None);

        Assert.Equal(HealthState.Ok, snapshot.Health);
        Assert.True(noStore);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task FetchAsync_RejectedCredentialReturnsAuthExpired(HttpStatusCode statusCode)
    {
        // Catches an expired Grok credential reported as a generic transport failure.
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(statusCode));

        ProviderFetchResult result = await CreateProvider(handler).FetchAsync(CancellationToken.None);
        ProviderSnapshot snapshot = Assert.IsType<ProviderSnapshot>(result.Snapshot);

        Assert.Equal(ProviderFetchOutcome.AuthenticationRequired, result.Outcome);
        Assert.Equal(HealthState.AuthExpired, snapshot.Health);
        Assert.Equal("re-auth: run grok login", snapshot.Error);
        Assert.Equal(statusCode, result.StatusCode);
    }

    [Fact]
    public async Task FetchAsync_PrefersOidcSessionOverLegacySignIn()
    {
        // Catches a scanner that uses the first auth entry instead of the SuperGrok OIDC session.
        AuthenticationHeaderValue? authorization = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            authorization = request.Headers.Authorization;
            return JsonResponse(ReadFixture("grok-credits.json"), "application/json");
        });
        var directory = new TemporaryDirectory();
        _directories.Add(directory);
        var provider = new GrokProvider(
            directory.WriteFile(
                "auth.json",
                """
                {"https://accounts.x.ai/sign-in":{"key":"legacy-access-token"},"https://auth.x.ai::unit-test-client":{"key":"preferred-access-token"}}
                """),
            handler,
            percent => SeverityPolicy.FromPercent(percent, 80, 95));

        ProviderSnapshot snapshot = await provider.FetchSnapshotAsync(CancellationToken.None);

        Assert.Equal(HealthState.Ok, snapshot.Health);
        Assert.Equal("preferred-access-token", authorization?.Parameter);
    }

    [Fact]
    public async Task FetchAsync_UsesLegacySignInWhenOidcSessionIsAbsent()
    {
        // Catches a scanner that requires the SuperGrok OIDC key and ignores a usable legacy session.
        AuthenticationHeaderValue? authorization = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            authorization = request.Headers.Authorization;
            return JsonResponse(ReadFixture("grok-credits.json"), "application/json");
        });
        var directory = new TemporaryDirectory();
        _directories.Add(directory);
        var provider = new GrokProvider(
            directory.WriteFile(
                "auth.json",
                """
                {"https://accounts.x.ai/sign-in":{"access_token":"legacy-access-token"}}
                """),
            handler,
            percent => SeverityPolicy.FromPercent(percent, 80, 95));

        ProviderSnapshot snapshot = await provider.FetchSnapshotAsync(CancellationToken.None);

        Assert.Equal(HealthState.Ok, snapshot.Health);
        Assert.Equal("legacy-access-token", authorization?.Parameter);
    }

    [Fact]
    public async Task FetchAsync_SkipsApiKeyEntries()
    {
        // Catches an API-key auth entry being sent to the consumer credits host.
        var handler = new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run"));
        var directory = new TemporaryDirectory();
        _directories.Add(directory);
        var provider = new GrokProvider(
            directory.WriteFile(
                "auth.json",
                """
                {"xai::api_key":{"key":"xai-unit-test-api-key","auth_mode":"api_key"}}
                """),
            handler,
            percent => SeverityPolicy.FromPercent(percent, 80, 95));

        ProviderFetchResult result = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.NotConfigured, result.Outcome);
        Assert.Equal(HealthState.Unreachable, Assert.IsType<ProviderSnapshot>(result.Snapshot).Health);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_CurrentPeriodWithoutPercentIsZero()
    {
        // Catches a missing creditUsagePercent on a weekly period being treated as unknown instead of 0%.
        const string response = """
            {"config":{"currentPeriod":{"type":"USAGE_PERIOD_TYPE_WEEKLY","end":"2026-06-08T00:00:00Z"}}}
            """;
        var handler = new StubHttpMessageHandler(_ => JsonResponse(response, "application/json"));

        ProviderSnapshot snapshot = await CreateProvider(handler).FetchSnapshotAsync(CancellationToken.None);

        UsageWindow weekly = Assert.Single(snapshot.Windows);
        Assert.Equal("weekly", weekly.Label);
        Assert.Equal(0, weekly.Percent);
        Assert.Equal(DateTimeOffset.Parse("2026-06-08T00:00:00Z"), weekly.ResetsAt);
    }

    [Theory]
    [InlineData("USAGE_PERIOD_TYPE_MONTHLY", "monthly")]
    [InlineData("USAGE_PERIOD_TYPE_UNKNOWN", "credits")]
    public async Task FetchAsync_LabelsPeriodTypeInvariantly(string periodType, string expectedLabel)
    {
        // Catches a period mapper that loses the documented compact window label.
        string response = """
            {"config":{"creditUsagePercent":10,"currentPeriod":{"type":"PERIOD","end":"2026-07-01T00:00:00Z"}}}
            """.Replace("PERIOD", periodType, StringComparison.Ordinal);
        var handler = new StubHttpMessageHandler(_ => JsonResponse(response, "application/json"));

        ProviderSnapshot snapshot = await CreateProvider(handler).FetchSnapshotAsync(CancellationToken.None);

        Assert.Equal(expectedLabel, Assert.Single(snapshot.Windows).Label);
    }

    [Fact]
    public async Task FetchAsync_OmitsOnDemandWhenCapIsZero()
    {
        // Catches a disabled on-demand cap being rendered as a zero-percent window.
        const string response = """
            {"config":{"creditUsagePercent":10,"currentPeriod":{"type":"USAGE_PERIOD_TYPE_WEEKLY","end":"2026-06-08T00:00:00Z"},"onDemandCap":{"val":0},"onDemandUsed":{"val":0}}}
            """;
        var handler = new StubHttpMessageHandler(_ => JsonResponse(response, "application/json"));

        ProviderSnapshot snapshot = await CreateProvider(handler).FetchSnapshotAsync(CancellationToken.None);

        Assert.Equal("weekly", Assert.Single(snapshot.Windows).Label);
    }

    [Fact]
    public async Task FetchAsync_UsesConfiguredSeverityForCreditPercent()
    {
        // Catches Grok windows bypassing the shared warning and critical thresholds.
        const string response = """
            {"config":{"creditUsagePercent":85,"currentPeriod":{"type":"USAGE_PERIOD_TYPE_WEEKLY","end":"2026-06-08T00:00:00Z"}}}
            """;
        var handler = new StubHttpMessageHandler(_ => JsonResponse(response, "application/json"));

        ProviderSnapshot snapshot = await CreateProvider(handler).FetchSnapshotAsync(CancellationToken.None);

        Assert.Equal(Severity.Warning, Assert.Single(snapshot.Windows).Severity);
    }

    [Fact]
    public async Task FetchAsync_StopsReadingAfterDirectCredentialFields()
    {
        // Catches a credential reader that buffers or reads refresh-token material after the bearer field.
        var handler = new StubHttpMessageHandler(_ => JsonResponse(ReadFixture("grok-credits.json"), "application/json"));
        var credential = new SingleByteCredentialStream(
            """
            {"https://auth.x.ai::unit-test-client":{"key":"unit-test-access-token","refresh_token":"sentinel-refresh-token"}}
            """,
            "refresh_token");
        var provider = new GrokProvider(
            "credential-stream.json",
            handler,
            percent => SeverityPolicy.FromPercent(percent, 80, 95),
            null,
            _ => credential);

        ProviderSnapshot snapshot = await provider.FetchSnapshotAsync(CancellationToken.None);

        Assert.Equal(HealthState.Ok, snapshot.Health);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_RejectsCredentialFieldsOutsideSessionObjectsWithOperationalFailure()
    {
        // Catches a scanner that accepts access credentials from an object other than an xAI session.
        var handler = new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run"));
        var directory = new TemporaryDirectory();
        _directories.Add(directory);
        var provider = new GrokProvider(
            directory.WriteFile(
                "auth.json",
                """
                {"other":{"key":"unit-test-access-token"}}
                """),
            handler,
            percent => SeverityPolicy.FromPercent(percent, 80, 95));

        ProviderFetchResult result = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.NotConfigured, result.Outcome);
        Assert.Equal(HealthState.Unreachable, Assert.IsType<ProviderSnapshot>(result.Snapshot).Health);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public void OpenCredentialStream_AllowsConcurrentTokenRotation()
    {
        // Break caught: a long credential read blocks Grok CLI token replacement or rewrite.
        var directory = new TemporaryDirectory();
        _directories.Add(directory);
        string credentialPath = directory.WriteFile("auth.json", "{}");
        string rotatedPath = Path.Combine(directory.Path, "auth.previous.json");

        using Stream reader = GrokProvider.OpenCredentialStream(credentialPath);
        using (new FileStream(
            credentialPath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete))
        {
        }

        File.Move(credentialPath, rotatedPath);

        Assert.True(File.Exists(rotatedPath));
    }

    [Fact]
    public async Task FetchAsync_UsageServerFailureReturnsTransientFailure()
    {
        // Break caught: a transient Grok endpoint failure is returned as a successful empty snapshot.
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        ProviderFetchResult result = await CreateProvider(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.TransientFailure, result.Outcome);
        Assert.Equal(HttpStatusCode.BadGateway, result.StatusCode);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task FetchAsync_TruncatedUsageJsonReturnsInvalidResponse()
    {
        // Break caught: truncated Grok JSON bypasses the poller's last-good retention boundary.
        var handler = new StubHttpMessageHandler(_ => JsonResponse("{\"config\":", "application/json"));

        ProviderFetchResult result = await CreateProvider(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.InvalidResponse, result.Outcome);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task FetchAsync_MissingCredentialReturnsNotConfiguredSnapshot()
    {
        // Break caught: a missing Grok credential becomes a repeated transport failure.
        var directory = new TemporaryDirectory();
        _directories.Add(directory);
        var handler = new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run"));
        var provider = new GrokProvider(
            Path.Combine(directory.Path, "missing-auth.json"),
            handler,
            percent => SeverityPolicy.FromPercent(percent, 80, 95));

        ProviderFetchResult result = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.NotConfigured, result.Outcome);
        Assert.Equal(HealthState.Unreachable, Assert.IsType<ProviderSnapshot>(result.Snapshot).Health);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "60", 60)]
    [InlineData(HttpStatusCode.ServiceUnavailable, null, 300)]
    public async Task FetchAsync_RateLimitReturnsSafeCooldown(
        HttpStatusCode statusCode,
        string? retryAfter,
        int expectedSeconds)
    {
        // Break caught: Grok rate limiting erases retained quota data.
        var handler = new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(statusCode);
            if (retryAfter is not null)
            {
                response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            }

            return response;
        });

        ProviderFetchResult result = await CreateProvider(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.RateLimited, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), result.RetryAfter);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task FetchAsync_CallerCancellationPropagates()
    {
        // Break caught: provider cancellation is converted into a successful degraded snapshot.
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var handler = new StubHttpMessageHandler(_ =>
            throw new OperationCanceledException(cancellation.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateProvider(handler).FetchSnapshotAsync(cancellation.Token));
    }

    private GrokProvider CreateProvider(string fixtureName, string contentType) =>
        CreateProvider(new StubHttpMessageHandler(_ => JsonResponse(ReadFixture(fixtureName), contentType)));

    private GrokProvider CreateProvider(HttpMessageHandler handler)
    {
        var directory = new TemporaryDirectory();
        _directories.Add(directory);
        string credentialPath = directory.WriteFile(
            "auth.json",
            """
            {"https://auth.x.ai::unit-test-client":{"key":"unit-test-access-token"}}
            """);

        return new GrokProvider(
            credentialPath,
            handler,
            percent => SeverityPolicy.FromPercent(percent, 80, 95));
    }

    private static HttpResponseMessage JsonResponse(string body, string contentType) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, contentType)
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

    public void Dispose()
    {
        foreach (TemporaryDirectory directory in _directories)
        {
            directory.Dispose();
        }
    }

    private sealed class SingleByteCredentialStream : Stream
    {
        private readonly byte[] _credential;
        private readonly int _protectedTailOffset;
        private int _position;

        public SingleByteCredentialStream(string credential, string protectedTail)
        {
            _credential = Encoding.UTF8.GetBytes(credential);
            _protectedTailOffset = credential.IndexOf(protectedTail, StringComparison.Ordinal);
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count != 1)
            {
                throw new Xunit.Sdk.XunitException("credential reader must not request a bulk buffer");
            }

            if (_position >= _protectedTailOffset)
            {
                throw new Xunit.Sdk.XunitException("refresh-token tail must not be read");
            }

            buffer[offset] = _credential[_position++];
            return 1;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
