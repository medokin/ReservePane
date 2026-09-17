using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ReservePane.Model;
using ReservePane.Providers;
using ReservePane.Tests.Support;

namespace ReservePane.Tests.Providers;

public sealed class ClaudeProviderTests : IDisposable
{
    private readonly List<TemporaryDirectory> _directories = [];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IsAvailableAsync_ReturnsCredentialFilePresence(bool credentialExists)
    {
        // Catches discovery parsing credential contents or ignoring the configured credential path.
        var provider = new ClaudeProvider(
            "credential.json",
            new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run")),
            percent => SeverityPolicy.FromPercent(percent, 80, 95),
            null,
            _ => throw new Xunit.Sdk.XunitException("Credential contents must not be read"),
            path => path == "credential.json" && credentialExists);

        IProviderAvailability availability = provider;
        bool result = await availability.IsAvailableAsync(CancellationToken.None);

        Assert.Equal(credentialExists, result);
    }

    [Fact]
    public async Task IsAvailableAsync_ConfirmedCredentialAbsenceReturnsUnavailable()
    {
        // Catches a missing credential being treated as indeterminate local presence.
        var provider = new ClaudeProvider(
            "credential.json",
            new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run")),
            percent => SeverityPolicy.FromPercent(percent, 80, 95),
            null,
            _ => throw new Xunit.Sdk.XunitException("Credential contents must not be read"),
            _ => throw new FileNotFoundException());

        bool result = await ((IProviderAvailability)provider)
            .IsAvailableAsync(CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task IsAvailableAsync_AccessDeniedCredentialProbeRemainsAvailable()
    {
        // Catches access denied being collapsed into confirmed credential absence.
        var provider = new ClaudeProvider(
            "credential.json",
            new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run")),
            percent => SeverityPolicy.FromPercent(percent, 80, 95),
            null,
            _ => throw new Xunit.Sdk.XunitException("Credential contents must not be read"),
            _ => throw new UnauthorizedAccessException("credential-path-must-not-be-reported"));

        bool result = await ((IProviderAvailability)provider)
            .IsAvailableAsync(CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task FetchAsync_MapsLimitsAndUncappedSpend()
    {
        // Catches a provider that maps obsolete top-level windows or treats uncapped spend as a quota.
        ProviderSnapshot snapshot = await CreateProviderWithFixtures().FetchSnapshotAsync(CancellationToken.None);

        Assert.Equal(HealthState.Ok, snapshot.Health);
        Assert.Equal("team_standard", snapshot.PlanLabel);
        Assert.Collection(snapshot.Windows,
            session =>
            {
                Assert.Equal("session", session.Label);
                Assert.Equal(2d, session.Percent);
                Assert.Equal(Severity.Normal, session.Severity);
            },
            weekly =>
            {
                Assert.Equal("weekly", weekly.Label);
                Assert.Equal(95d, weekly.Percent);
                Assert.Equal(Severity.Critical, weekly.Severity);
            });
        Assert.Collection(snapshot.Info,
            line => Assert.Equal(new InfoLine("Spend", "EUR 322.52"), line),
            line => Assert.Equal(new InfoLine("Budget", "no cap set"), line));
    }

    [Fact]
    public async Task FetchAsync_MapsCappedSpendAndBudget()
    {
        var handler = new StubHttpMessageHandler(request => JsonResponse(
            request.RequestUri!.AbsolutePath.EndsWith("usage", StringComparison.Ordinal)
                ? ReadFixture("claude-usage-capped.json")
                : ReadFixture("claude-profile.json")));

        ProviderFetchResult result = await CreateProvider(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.Success, result.Outcome);
        Assert.Collection(result.Snapshot!.Info,
            line => Assert.Equal(new InfoLine("Spend", "EUR 123.45"), line),
            line => Assert.Equal(new InfoLine("Budget", "EUR 1000.00"), line));
        Assert.Equal(2, result.Snapshot.Windows.Length);
    }

    [Theory]
    [InlineData("{\"amount_minor\":0,\"currency\":\"USD\",\"exponent\":2}", "USD 0.00")]
    [InlineData("{\"amount_minor\":5000,\"currency\":\"JPY\",\"exponent\":0}", "JPY 5000")]
    [InlineData("{\"amount_minor\":12345,\"currency\":\"KWD\",\"exponent\":3}", "KWD 12.345")]
    [InlineData("{}", "Unavailable")]
    [InlineData("false", "Unavailable")]
    [InlineData("{\"amount_minor\":\"invalid\",\"currency\":\"EUR\",\"exponent\":2}", "Unavailable")]
    [InlineData("{\"amount_minor\":100,\"currency\":\"EUR\",\"exponent\":\"invalid\"}", "Unavailable")]
    [InlineData("{\"amount_minor\":100,\"currency\":\"EUR\",\"exponent\":-1}", "Unavailable")]
    [InlineData("{\"amount_minor\":100,\"currency\":\"EUR\",\"exponent\":29}", "Unavailable")]
    public async Task FetchAsync_BudgetUsesItsOwnMoneyFieldsAndPreservesSpend(string limitJson, string budget)
    {
        string usage = ReadFixture("claude-usage.json").Replace("\"limit\": null", $"\"limit\": {limitJson}");
        var handler = new StubHttpMessageHandler(request => JsonResponse(
            request.RequestUri!.AbsolutePath.EndsWith("usage", StringComparison.Ordinal)
                ? usage
                : ReadFixture("claude-profile.json")));

        ProviderFetchResult result = await CreateProvider(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.Success, result.Outcome);
        Assert.Collection(result.Snapshot!.Info,
            line => Assert.Equal(new InfoLine("Spend", "EUR 322.52"), line),
            line => Assert.Equal(new InfoLine("Budget", budget), line));
        Assert.Equal(2, result.Snapshot.Windows.Length);
    }

    [Fact]
    public async Task FetchAsync_VendorSeverityOverridesDerivedSeverity()
    {
        // Catches a provider that discards the vendor severity in favor of configurable thresholds.
        ProviderSnapshot snapshot = await CreateProviderWithFixtures(percent =>
                SeverityPolicy.FromPercent(percent, 50, 60))
            .FetchSnapshotAsync(CancellationToken.None);

        Assert.Equal(Severity.Normal, snapshot.Windows[0].Severity);
    }

    [Fact]
    public async Task FetchAsync_ExpiredCredentialSkipsHttpAndReturnsAuthExpired()
    {
        // Catches a provider that sends an expired credential over HTTP.
        var handler = new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run"));
        ClaudeProvider provider = CreateProvider(handler, expiresAtUnixMilliseconds: 0);

        ProviderFetchResult result = await provider.FetchAsync(CancellationToken.None);
        ProviderSnapshot snapshot = Assert.IsType<ProviderSnapshot>(result.Snapshot);

        Assert.Equal(ProviderFetchOutcome.AuthenticationRequired, result.Outcome);
        Assert.Equal(HealthState.AuthExpired, snapshot.Health);
        Assert.Equal("re-auth: run claude login", snapshot.Error);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_ExpiredCredential_RefreshesAndUsesRotatedAccessToken()
    {
        // Break caught: a refreshable Claude session is reported as logged out when only its access token expired.
        var now = new DateTimeOffset(2026, 8, 28, 7, 0, 0, TimeSpan.Zero);
        var time = new MutableTimeProvider(now);
        var directory = new TemporaryDirectory();
        _directories.Add(directory);
        string credentialPath = directory.WriteFile(
            "credentials.json",
            CreateCredentialJson("expired-access-token", now.AddMinutes(-1)));
        var observedTokens = new List<string?>();
        var handler = new StubHttpMessageHandler(request =>
        {
            observedTokens.Add(request.Headers.Authorization?.Parameter);
            return JsonResponse(
                request.RequestUri!.AbsolutePath.EndsWith("usage", StringComparison.Ordinal)
                    ? ReadFixture("claude-usage.json")
                    : ReadFixture("claude-profile.json"));
        });
        int refreshCount = 0;
        var provider = new ClaudeProvider(
            credentialPath,
            handler,
            percent => SeverityPolicy.FromPercent(percent, 80, 95),
            time,
            ClaudeProvider.OpenCredentialStream,
            CredentialFilePrerequisite.Probe,
            async cancellationToken =>
            {
                refreshCount++;
                await File.WriteAllTextAsync(
                    credentialPath,
                    CreateCredentialJson("rotated-access-token", now.AddHours(8)),
                    cancellationToken);
                return true;
            });

        ProviderFetchResult result = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.Success, result.Outcome);
        Assert.Equal(HealthState.Ok, Assert.IsType<ProviderSnapshot>(result.Snapshot).Health);
        Assert.Equal(1, refreshCount);
        Assert.Equal(["rotated-access-token", "rotated-access-token"], observedTokens);
    }

    [Fact]
    public async Task FetchAsync_RefreshFailure_PreservesAuthenticationRequiredResult()
    {
        // Break caught: a missing or failed Claude CLI converts a clear auth state into a provider exception.
        var handler = new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run"));
        var directory = new TemporaryDirectory();
        _directories.Add(directory);
        string credentialPath = directory.WriteFile(
            "credentials.json",
            CreateCredentialJson("expired-access-token", DateTimeOffset.UtcNow.AddMinutes(-1)));
        var provider = new ClaudeProvider(
            credentialPath,
            handler,
            percent => SeverityPolicy.FromPercent(percent, 80, 95),
            null,
            ClaudeProvider.OpenCredentialStream,
            CredentialFilePrerequisite.Probe,
            _ => throw new InvalidOperationException("Claude CLI failed"));

        ProviderFetchResult result = await provider.FetchAsync(CancellationToken.None);
        ProviderSnapshot snapshot = Assert.IsType<ProviderSnapshot>(result.Snapshot);

        Assert.Equal(ProviderFetchOutcome.AuthenticationRequired, result.Outcome);
        Assert.Equal(HealthState.AuthExpired, snapshot.Health);
        Assert.Equal("re-auth: run claude login", snapshot.Error);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_RefreshCallerCancellation_Propagates()
    {
        // Break caught: provider timeout or shutdown cancellation is swallowed as an authentication warning.
        var handler = new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run"));
        var directory = new TemporaryDirectory();
        _directories.Add(directory);
        string credentialPath = directory.WriteFile(
            "credentials.json",
            CreateCredentialJson("expired-access-token", DateTimeOffset.UtcNow.AddMinutes(-1)));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ClaudeProvider(
            credentialPath,
            handler,
            percent => SeverityPolicy.FromPercent(percent, 80, 95),
            null,
            ClaudeProvider.OpenCredentialStream,
            CredentialFilePrerequisite.Probe,
            async cancellationToken =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return true;
            });
        using var cancellation = new CancellationTokenSource();

        Task<ProviderFetchResult> fetch = provider.FetchAsync(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_ExpiredCredentialSkipsTrailingRefreshToken()
    {
        // Catches a credential reader that continues into a protected refresh-token tail after required fields are complete.
        var handler = new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run"));
        ClaudeProvider provider = CreateProviderWithCredentialStream(
            handler,
            new SingleByteCredentialStream("""
                {"claudeAiOauth":{"accessToken":"unit-test-access-token","expiresAt":0},"refreshToken":"sentinel-refresh-token"}
                """, ",\"refreshToken\""));

        ProviderSnapshot snapshot = await provider.FetchSnapshotAsync(CancellationToken.None);

        Assert.Equal(HealthState.AuthExpired, snapshot.Health);
        Assert.Equal("re-auth: run claude login", snapshot.Error);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_ExpiredCredentialSkipsNestedRefreshTokenWithOneByteReads()
    {
        // Catches a credential reader that buffers an unknown OAuth refresh-token string before reaching expiry.
        var handler = new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run"));
        ClaudeProvider provider = CreateProviderWithCredentialStream(
            handler,
            new SingleByteCredentialStream("""
                {"claudeAiOauth":{"accessToken":"unit-test-access-token","refreshToken":"sentinel-refresh-token","expiresAt":0}}
                """));

        ProviderSnapshot snapshot = await provider.FetchSnapshotAsync(CancellationToken.None);

        Assert.Equal(HealthState.AuthExpired, snapshot.Health);
        Assert.Equal("re-auth: run claude login", snapshot.Error);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_NestedAccessTokenThrowsWithoutHttp()
    {
        // Catches a reader that mistakes a nested descendant string for direct claudeAiOauth.accessToken.
        var handler = new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run"));
        ClaudeProvider provider = CreateProviderWithCredential(
            handler,
            "{\"claudeAiOauth\":{\"accessToken\":{\"nested\":\"unit-test-access-token\"},\"expiresAt\":0}}");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.FetchSnapshotAsync(CancellationToken.None));

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_ArrayExpiresAtThrowsWithoutHttp()
    {
        // Catches a reader that mistakes an array element for direct claudeAiOauth.expiresAt.
        var handler = new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run"));
        ClaudeProvider provider = CreateProviderWithCredential(
            handler,
            "{\"claudeAiOauth\":{\"accessToken\":\"unit-test-access-token\",\"expiresAt\":[0]}}");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.FetchSnapshotAsync(CancellationToken.None));

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_EmptyAccessTokenThrowsWithoutHttp()
    {
        // Catches a selective reader that accepts an empty direct access token.
        var handler = new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run"));
        ClaudeProvider provider = CreateProviderWithCredential(
            handler,
            "{\"claudeAiOauth\":{\"accessToken\":\"\",\"expiresAt\":0}}");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.FetchSnapshotAsync(CancellationToken.None));

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_EscapedOauthPropertyNamesReturnAuthExpired()
    {
        // Catches a scanner that rejects valid JSON escapes in known OAuth property names.
        var handler = new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run"));
        ClaudeProvider provider = CreateProviderWithCredential(
            handler,
            "{\"claudeAiO\\u0061uth\":{\"accessT\\u006fken\":\"unit-test-access-token\",\"expires\\u0041t\":0}}");

        ProviderSnapshot snapshot = await provider.FetchSnapshotAsync(CancellationToken.None);

        Assert.Equal(HealthState.AuthExpired, snapshot.Health);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_LeadingZeroExpiresAtThrowsWithoutHttp()
    {
        // Catches a scanner that accepts a malformed direct JSON integer token.
        var handler = new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run"));
        ClaudeProvider provider = CreateProviderWithCredential(
            handler,
            "{\"claudeAiOauth\":{\"accessToken\":\"unit-test-access-token\",\"expiresAt\":00}}");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.FetchSnapshotAsync(CancellationToken.None));

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_UnknownValueNestedBeyond64LevelsThrowsWithoutHttp()
    {
        // Catches recursive unknown-value skipping that can overflow the stack on hostile credential JSON.
        string nested = new string('[', 65) + "null" + new string(']', 65);
        var handler = new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run"));
        ClaudeProvider provider = CreateProviderWithCredential(
            handler,
            $"{{\"claudeAiOauth\":{{\"accessToken\":\"unit-test-access-token\",\"metadata\":{nested},\"expiresAt\":0}}}}");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.FetchSnapshotAsync(CancellationToken.None));

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public void OpenCredentialStream_DisablesManagedReadAhead()
    {
        // Catches a production credential stream factory that silently restores FileStream's managed read buffer.
        var directory = new TemporaryDirectory();
        _directories.Add(directory);
        string credentialPath = directory.WriteFile("credentials.json", "{}");

        using Stream stream = ClaudeProvider.OpenCredentialStream(credentialPath);
        FieldInfo? strategyField = typeof(FileStream).GetField("_strategy", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(strategyField);

        Assert.DoesNotContain("Buffered", strategyField!.GetValue(stream)!.GetType().Name, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenCredentialStream_AllowsConcurrentTokenRotation()
    {
        // Break caught: a long credential read blocks the CLI from replacing or rewriting its own file.
        var directory = new TemporaryDirectory();
        _directories.Add(directory);
        string credentialPath = directory.WriteFile("credentials.json", "{}");
        string rotatedPath = Path.Combine(directory.Path, "credentials.previous.json");

        using Stream reader = ClaudeProvider.OpenCredentialStream(credentialPath);
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
    public async Task FetchAsync_MalformedCredentialThrowsSanitizedFailure()
    {
        // Catches an operational credential failure whose exception text exposes authentication material.
        var handler = new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run"));
        ClaudeProvider provider = CreateProviderWithCredential(
            handler,
            "{\"claudeAiOauth\":{\"accessToken\":\"unit-test-access-token\"");

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.FetchSnapshotAsync(CancellationToken.None));

        Assert.DoesNotContain("unit-test-access-token", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_MissingOptionalUsageFieldsReturnsEmptySafeSnapshot()
    {
        // Catches a provider that dereferences optional vendor fields and aborts an otherwise valid response.
        const string usage = """
            {"five_hour":null,"seven_day":null,"seven_day_opus":null,"extra_usage":null,"limits":[{"group":"session"}],"spend":null}
            """;
        var handler = new StubHttpMessageHandler(request => JsonResponse(
            request.RequestUri!.AbsolutePath.EndsWith("usage", StringComparison.Ordinal)
                ? usage
                : "{\"organization\":{}}"));

        ProviderSnapshot snapshot = await CreateProvider(handler).FetchSnapshotAsync(CancellationToken.None);

        Assert.Equal(HealthState.Ok, snapshot.Health);
        Assert.Null(snapshot.PlanLabel);
        UsageWindow window = Assert.Single(snapshot.Windows);
        Assert.Equal("session", window.Label);
        Assert.Null(window.Percent);
        Assert.Null(window.ResetsAt);
        Assert.Equal(Severity.Normal, window.Severity);
        Assert.Empty(snapshot.Info);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task FetchAsync_UnauthorizedResponseReturnsAuthExpired(HttpStatusCode statusCode)
    {
        // Catches a provider that reports an expired or rejected token as a generic transport failure.
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(statusCode));

        ProviderFetchResult result = await CreateProvider(handler).FetchAsync(CancellationToken.None);
        ProviderSnapshot snapshot = Assert.IsType<ProviderSnapshot>(result.Snapshot);

        Assert.Equal(ProviderFetchOutcome.AuthenticationRequired, result.Outcome);
        Assert.Equal(statusCode, result.StatusCode);
        Assert.Equal(HealthState.AuthExpired, snapshot.Health);
        Assert.Equal("re-auth: run claude login", snapshot.Error);
    }

    [Fact]
    public async Task FetchAsync_RejectedCredential_RefreshesAndRetriesOnce()
    {
        // Break caught: a server-invalidated access token bypasses the CLI refresh path because its local expiry is still future.
        var now = new DateTimeOffset(2026, 8, 28, 7, 0, 0, TimeSpan.Zero);
        var time = new MutableTimeProvider(now);
        var directory = new TemporaryDirectory();
        _directories.Add(directory);
        string credentialPath = directory.WriteFile(
            "credentials.json",
            CreateCredentialJson("rejected-access-token", now.AddHours(1)));
        var observedTokens = new List<string?>();
        var handler = new StubHttpMessageHandler(request =>
        {
            string? token = request.Headers.Authorization?.Parameter;
            observedTokens.Add(token);
            if (token == "rejected-access-token")
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            return JsonResponse(
                request.RequestUri!.AbsolutePath.EndsWith("usage", StringComparison.Ordinal)
                    ? ReadFixture("claude-usage.json")
                    : ReadFixture("claude-profile.json"));
        });
        int refreshCount = 0;
        var provider = new ClaudeProvider(
            credentialPath,
            handler,
            percent => SeverityPolicy.FromPercent(percent, 80, 95),
            time,
            ClaudeProvider.OpenCredentialStream,
            CredentialFilePrerequisite.Probe,
            async cancellationToken =>
            {
                refreshCount++;
                await File.WriteAllTextAsync(
                    credentialPath,
                    CreateCredentialJson("rotated-access-token", now.AddHours(8)),
                    cancellationToken);
                return true;
            });

        ProviderFetchResult result = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.Success, result.Outcome);
        Assert.Equal(1, refreshCount);
        Assert.Equal(
            ["rejected-access-token", "rotated-access-token", "rotated-access-token"],
            observedTokens);
    }

    [Fact]
    public async Task FetchAsync_RotatedCredentialIsAlsoRejected_DoesNotRefreshOrRetryAgain()
    {
        // Break caught: the single retry can recursively refresh and make a third API request.
        var now = new DateTimeOffset(2026, 8, 28, 7, 0, 0, TimeSpan.Zero);
        var time = new MutableTimeProvider(now);
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        int refreshCount = 0;
        var directory = new TemporaryDirectory();
        _directories.Add(directory);
        string credentialPath = directory.WriteFile(
            "credentials.json",
            CreateCredentialJson("rejected-access-token", now.AddHours(1)));
        var provider = new ClaudeProvider(
            credentialPath,
            handler,
            percent => SeverityPolicy.FromPercent(percent, 80, 95),
            time,
            ClaudeProvider.OpenCredentialStream,
            CredentialFilePrerequisite.Probe,
            async cancellationToken =>
            {
                refreshCount++;
                await File.WriteAllTextAsync(
                    credentialPath,
                    CreateCredentialJson("rotated-access-token", now.AddHours(8)),
                    cancellationToken);
                return true;
            });

        ProviderFetchResult result = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.AuthenticationRequired, result.Outcome);
        Assert.Equal(1, refreshCount);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_UnchangedRejectedCredential_IsNotRetried()
    {
        // Break caught: a successful CLI exit without token rotation resends the same rejected token.
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        int refreshCount = 0;
        var directory = new TemporaryDirectory();
        _directories.Add(directory);
        string credentialPath = directory.WriteFile(
            "credentials.json",
            CreateCredentialJson("rejected-access-token", DateTimeOffset.UtcNow.AddHours(1)));
        var provider = new ClaudeProvider(
            credentialPath,
            handler,
            percent => SeverityPolicy.FromPercent(percent, 80, 95),
            null,
            ClaudeProvider.OpenCredentialStream,
            CredentialFilePrerequisite.Probe,
            _ =>
            {
                refreshCount++;
                return Task.FromResult(true);
            });

        ProviderFetchResult result = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.AuthenticationRequired, result.Outcome);
        Assert.Equal(1, refreshCount);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_RefreshOnlyExtendsRejectedCredentialExpiry_DoesNotRetry()
    {
        // Break caught: expiry metadata changes cause the same rejected access token to be resent.
        var now = new DateTimeOffset(2026, 8, 28, 7, 0, 0, TimeSpan.Zero);
        var time = new MutableTimeProvider(now);
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        int refreshCount = 0;
        var directory = new TemporaryDirectory();
        _directories.Add(directory);
        string credentialPath = directory.WriteFile(
            "credentials.json",
            CreateCredentialJson("rejected-access-token", now.AddHours(1)));
        var provider = new ClaudeProvider(
            credentialPath,
            handler,
            percent => SeverityPolicy.FromPercent(percent, 80, 95),
            time,
            ClaudeProvider.OpenCredentialStream,
            CredentialFilePrerequisite.Probe,
            async cancellationToken =>
            {
                refreshCount++;
                await File.WriteAllTextAsync(
                    credentialPath,
                    CreateCredentialJson("rejected-access-token", now.AddHours(8)),
                    cancellationToken);
                return true;
            });

        ProviderFetchResult result = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.AuthenticationRequired, result.Outcome);
        Assert.Equal(1, refreshCount);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_CachesProfileForOneHour()
    {
        // Catches a provider that loads the immutable plan profile on every usage poll.
        int profileRequests = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("profile", StringComparison.Ordinal))
            {
                profileRequests++;
                return JsonResponse(ReadFixture("claude-profile.json"));
            }

            return JsonResponse(ReadFixture("claude-usage.json"));
        });
        ClaudeProvider provider = CreateProvider(handler);

        ProviderSnapshot first = await provider.FetchSnapshotAsync(CancellationToken.None);
        ProviderSnapshot second = await provider.FetchSnapshotAsync(CancellationToken.None);

        Assert.Equal("team_standard", first.PlanLabel);
        Assert.Equal("team_standard", second.PlanLabel);
        Assert.Equal(1, profileRequests);
    }

    [Fact]
    public async Task FetchAsync_ProfileAuthenticationFailurePreservesValidUsage()
    {
        // Break caught: profile authentication handling discards usage already mapped from a successful response.
        var handler = new StubHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("usage", StringComparison.Ordinal)
                ? JsonResponse(ReadFixture("claude-usage.json"))
                : new HttpResponseMessage(HttpStatusCode.Unauthorized));

        ProviderFetchResult result = await CreateProvider(handler).FetchAsync(CancellationToken.None);
        ProviderSnapshot snapshot = Assert.IsType<ProviderSnapshot>(result.Snapshot);

        Assert.Equal(ProviderFetchOutcome.AuthenticationRequired, result.Outcome);
        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
        Assert.Equal(HealthState.AuthExpired, snapshot.Health);
        AssertUsageAndSpendPreserved(snapshot);
        Assert.Equal("re-auth: run claude login", snapshot.Error);
    }

    [Fact]
    public async Task FetchAsync_ProfileRejectsCredential_RefreshesAndRetriesOnce()
    {
        // Break caught: authentication rejection from the profile endpoint does not use the refresh path.
        var now = new DateTimeOffset(2026, 8, 28, 7, 0, 0, TimeSpan.Zero);
        var time = new MutableTimeProvider(now);
        var directory = new TemporaryDirectory();
        _directories.Add(directory);
        string credentialPath = directory.WriteFile(
            "credentials.json",
            CreateCredentialJson("rejected-access-token", now.AddHours(1)));
        var observedTokens = new List<string?>();
        var handler = new StubHttpMessageHandler(request =>
        {
            string? token = request.Headers.Authorization?.Parameter;
            observedTokens.Add(token);
            if (token == "rejected-access-token" &&
                request.RequestUri!.AbsolutePath.EndsWith("profile", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }

            return JsonResponse(
                request.RequestUri!.AbsolutePath.EndsWith("usage", StringComparison.Ordinal)
                    ? ReadFixture("claude-usage.json")
                    : ReadFixture("claude-profile.json"));
        });
        int refreshCount = 0;
        var provider = new ClaudeProvider(
            credentialPath,
            handler,
            percent => SeverityPolicy.FromPercent(percent, 80, 95),
            time,
            ClaudeProvider.OpenCredentialStream,
            CredentialFilePrerequisite.Probe,
            async cancellationToken =>
            {
                refreshCount++;
                await File.WriteAllTextAsync(
                    credentialPath,
                    CreateCredentialJson("rotated-access-token", now.AddHours(8)),
                    cancellationToken);
                return true;
            });

        ProviderFetchResult result = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.Success, result.Outcome);
        Assert.Equal(1, refreshCount);
        Assert.Equal(
            [
                "rejected-access-token",
                "rejected-access-token",
                "rotated-access-token",
                "rotated-access-token"
            ],
            observedTokens);
    }

    [Fact]
    public async Task FetchAsync_ProfileCallerCancellationPropagates()
    {
        // Break caught: the ancillary profile boundary converts caller cancellation into a degraded snapshot.
        using var cancellation = new CancellationTokenSource();
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("usage", StringComparison.Ordinal))
            {
                return JsonResponse(ReadFixture("claude-usage.json"));
            }

            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateProvider(handler).FetchSnapshotAsync(cancellation.Token));
    }

    [Fact]
    public async Task FetchAsync_ProfileServerFailurePreservesUsageAndCachedPlan()
    {
        // Break caught: an optional profile outage erases valid usage, spend, and the last known plan.
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        int profileRequests = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("profile", StringComparison.Ordinal))
            {
                return JsonResponse(ReadFixture("claude-usage.json"));
            }

            return Interlocked.Increment(ref profileRequests) == 1
                ? JsonResponse(ReadFixture("claude-profile.json"))
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        ClaudeProvider provider = CreateProvider(handler, timeProvider: time);
        ProviderSnapshot first = await provider.FetchSnapshotAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));

        ProviderFetchResult result = await provider.FetchAsync(CancellationToken.None);
        ProviderSnapshot degraded = Assert.IsType<ProviderSnapshot>(result.Snapshot);

        Assert.Equal(ProviderFetchOutcome.PartialSuccess, result.Outcome);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, result.StatusCode);
        Assert.Equal(HealthState.Ok, first.Health);
        Assert.Equal(HealthState.Degraded, degraded.Health);
        Assert.Equal("team_standard", degraded.PlanLabel);
        AssertUsageAndSpendPreserved(degraded);
        Assert.Equal("Claude profile request failed", degraded.Error);
    }

    [Fact]
    public async Task FetchAsync_NonJsonProfilePreservesUsageWithoutPlan()
    {
        // Break caught: a non-JSON optional profile response discards independently parsed quota data.
        var handler = new StubHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("usage", StringComparison.Ordinal)
                ? JsonResponse(ReadFixture("claude-usage.json"))
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html>not JSON</html>", Encoding.UTF8, "text/html"),
                });

        ProviderSnapshot degraded = await CreateProvider(handler).FetchSnapshotAsync(CancellationToken.None);

        Assert.Equal(HealthState.Degraded, degraded.Health);
        Assert.Null(degraded.PlanLabel);
        AssertUsageAndSpendPreserved(degraded);
        Assert.Equal("Claude profile response was not JSON", degraded.Error);
    }

    [Fact]
    public async Task FetchAsync_ProfileNetworkFailurePreservesUsageWithoutPlan()
    {
        // Break caught: a thrown ancillary profile transport error escapes and loses valid usage data.
        var handler = new StubHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("usage", StringComparison.Ordinal)
                ? JsonResponse(ReadFixture("claude-usage.json"))
                : throw new HttpRequestException("synthetic profile failure"));

        ProviderSnapshot degraded = await CreateProvider(handler).FetchSnapshotAsync(CancellationToken.None);

        Assert.Equal(HealthState.Degraded, degraded.Health);
        Assert.Null(degraded.PlanLabel);
        AssertUsageAndSpendPreserved(degraded);
        Assert.Equal("Claude profile request failed", degraded.Error);
    }

    [Fact]
    public async Task FetchAsync_NonJsonUsageResponseReturnsInvalidResponse()
    {
        // Catches a provider that attempts to parse an HTML success page as a usage response.
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>not JSON</html>", Encoding.UTF8, "text/html")
        });

        ProviderFetchResult result = await CreateProvider(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.InvalidResponse, result.Outcome);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task FetchAsync_ServiceUnavailableReturnsRateLimitedFallback()
    {
        // Break caught: a transient usage endpoint failure is returned as a successful empty snapshot.
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        ProviderFetchResult result = await CreateProvider(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.RateLimited, result.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(5), result.RetryAfter);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task FetchAsync_TruncatedUsageJsonReturnsInvalidResponse()
    {
        // Break caught: truncated vendor JSON replaces valid retained usage with an empty degraded snapshot.
        var handler = new StubHttpMessageHandler(_ => JsonResponse("{\"limits\":["));

        ProviderFetchResult result = await CreateProvider(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.InvalidResponse, result.Outcome);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task FetchAsync_MissingCredentialReturnsNotConfiguredSnapshot()
    {
        // Break caught: a missing credential is counted as a transport failure.
        var handler = new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run"));
        var directory = new TemporaryDirectory();
        _directories.Add(directory);
        string missingPath = Path.Combine(directory.Path, "missing-credential.json");
        var provider = new ClaudeProvider(
            missingPath,
            handler,
            percent => SeverityPolicy.FromPercent(percent, 80, 95));

        ProviderFetchResult result = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.NotConfigured, result.Outcome);
        Assert.Equal(HealthState.Unreachable, Assert.IsType<ProviderSnapshot>(result.Snapshot).Health);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "120", 120)]
    [InlineData(HttpStatusCode.ServiceUnavailable, null, 300)]
    public async Task FetchAsync_RateLimitReturnsSafeCooldown(
        HttpStatusCode statusCode,
        string? retryAfter,
        int expectedSeconds)
    {
        // Break caught: 429 or 503 is returned as a successful empty snapshot.
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
    public async Task FetchAsync_AddsNoStoreToEveryRequest()
    {
        // Catches pooled clients that permit credential-bound usage responses to be cached.
        var cacheDirectives = new List<bool>();
        var handler = new StubHttpMessageHandler(request =>
        {
            cacheDirectives.Add(request.Headers.CacheControl?.NoStore == true);
            return JsonResponse(
                request.RequestUri!.AbsolutePath.EndsWith("usage", StringComparison.Ordinal)
                    ? ReadFixture("claude-usage.json")
                    : ReadFixture("claude-profile.json"));
        });

        ProviderSnapshot snapshot = await CreateProvider(handler).FetchSnapshotAsync(CancellationToken.None);

        Assert.Equal(HealthState.Ok, snapshot.Health);
        Assert.Equal([true, true], cacheDirectives);
    }

    private ClaudeProvider CreateProviderWithFixtures(Func<double?, Severity>? severityFromPercent = null) =>
        CreateProvider(new StubHttpMessageHandler(request => JsonResponse(
            request.RequestUri!.AbsolutePath.EndsWith("usage", StringComparison.Ordinal)
                ? ReadFixture("claude-usage.json")
                : ReadFixture("claude-profile.json"))), severityFromPercent: severityFromPercent);

    private ClaudeProvider CreateProvider(
        HttpMessageHandler handler,
        long? expiresAtUnixMilliseconds = null,
        Func<double?, Severity>? severityFromPercent = null,
        TimeProvider? timeProvider = null)
    {
        var directory = new TemporaryDirectory();
        _directories.Add(directory);
        long expiresAt = expiresAtUnixMilliseconds ?? DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds();
        string credentialPath = directory.WriteFile(
            "credentials.json",
            CreateCredentialJson(
                "unit-test-access-token",
                DateTimeOffset.FromUnixTimeMilliseconds(expiresAt)));

        return new ClaudeProvider(
            credentialPath,
            handler,
            severityFromPercent ?? (percent => SeverityPolicy.FromPercent(percent, 80, 95)),
            timeProvider,
            ClaudeProvider.OpenCredentialStream);
    }

    private ClaudeProvider CreateProviderWithCredential(HttpMessageHandler handler, string credentialJson)
    {
        var directory = new TemporaryDirectory();
        _directories.Add(directory);
        return new ClaudeProvider(
            directory.WriteFile("credentials.json", credentialJson),
            handler,
            percent => SeverityPolicy.FromPercent(percent, 80, 95),
            null,
            ClaudeProvider.OpenCredentialStream);
    }

    private static ClaudeProvider CreateProviderWithCredentialStream(HttpMessageHandler handler, Stream credentialStream) =>
        new(
            "credential-stream.json",
            handler,
            percent => SeverityPolicy.FromPercent(percent, 80, 95),
            null,
            _ => credentialStream);

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static string CreateCredentialJson(string accessToken, DateTimeOffset expiresAt) =>
        JsonSerializer.Serialize(new
        {
            claudeAiOauth = new
            {
                accessToken,
                expiresAt = expiresAt.ToUnixTimeMilliseconds()
            }
        });

    private static void AssertUsageAndSpendPreserved(ProviderSnapshot snapshot)
    {
        Assert.Collection(
            snapshot.Windows,
            session => Assert.Equal("session", session.Label),
            weekly => Assert.Equal("weekly", weekly.Label));
        Assert.Collection(snapshot.Info,
            line => Assert.Equal(new InfoLine("Spend", "EUR 322.52"), line),
            line => Assert.Equal(new InfoLine("Budget", "no cap set"), line));
    }

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

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }

    private sealed class SingleByteCredentialStream : Stream
    {
        private readonly byte[] _credential;
        private readonly int _protectedTailOffset;
        private int _position;

        public SingleByteCredentialStream(string credential, string? protectedTail = null)
        {
            _credential = Encoding.UTF8.GetBytes(credential);
            _protectedTailOffset = protectedTail is null
                ? _credential.Length
                : credential.IndexOf(protectedTail, StringComparison.Ordinal);
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
                throw new Xunit.Sdk.XunitException("refreshToken tail must not be read");
            }

            buffer[offset] = _credential[_position++];
            return 1;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
