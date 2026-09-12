using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ReservePane.Core;
using ReservePane.Model;
using ReservePane.Providers;
using ReservePane.Tests.Support;

namespace ReservePane.Tests.Providers;

public sealed class OpenCodeGoProviderTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public async Task IsAvailableAsync_UsesCredentialOrConsoleCommand(
        bool credentialExists,
        bool commandAvailable,
        bool expected)
    {
        // Catches discovery parsing credentials or requiring persisted settings for Console discovery.
        var provider = new OpenCodeGoProvider(
            "opencode-auth.json",
            new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run")),
            SeverityFromPercent,
            null,
            _ => throw new Xunit.Sdk.XunitException("Credential contents must not be read"),
            new StubWorkspaceReader(() => throw new Xunit.Sdk.XunitException("Accounts must not be read")),
            new StubConsoleClient(() => throw new Xunit.Sdk.XunitException("Console HTTP must not run")),
            path => path == "opencode-auth.json" && credentialExists,
            command => command == "opencode" && commandAvailable);

        IProviderAvailability availability = provider;
        bool result = await availability.IsAvailableAsync(CancellationToken.None);

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task IsAvailableAsync_CredentialSecurityFailureRemainsAvailable()
    {
        // Catches a credential security failure falling through to Console command discovery.
        var provider = new OpenCodeGoProvider(
            "opencode-auth.json",
            new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run")),
            SeverityFromPercent,
            null,
            _ => throw new Xunit.Sdk.XunitException("Credential contents must not be read"),
            new StubWorkspaceReader(() => throw new Xunit.Sdk.XunitException("Accounts must not be read")),
            new StubConsoleClient(() => throw new Xunit.Sdk.XunitException("Console HTTP must not run")),
            _ => throw new System.Security.SecurityException("credential-path-must-not-be-reported"),
            _ => throw new Xunit.Sdk.XunitException("Command discovery must not run"));

        bool result = await ((IProviderAvailability)provider)
            .IsAvailableAsync(CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task FetchAsync_MapsUsageWindowsAndConfiguredSeverity()
    {
        // Catches a provider that omits valid windows, loses reset times, or clamps policy input.
        var handler = new StubHttpMessageHandler(_ => JsonResponse(ReadFixture("opencode-go-usage.json")));

        ProviderSnapshot snapshot = await CreateProvider(handler)
            .FetchSnapshotAsync(CancellationToken.None);

        Assert.Equal(HealthState.Ok, snapshot.Health);
        Assert.Collection(
            snapshot.Windows,
            rolling =>
            {
                Assert.Equal("rolling", rolling.Label);
                Assert.Equal(23, rolling.Percent);
                Assert.Equal(DateTimeOffset.Parse("2026-08-27T12:00:00Z"), rolling.ResetsAt);
                Assert.Equal(Severity.Normal, rolling.Severity);
            },
            weekly =>
            {
                Assert.Equal("weekly", weekly.Label);
                Assert.Equal(85, weekly.Percent);
                Assert.Equal(DateTimeOffset.Parse("2026-08-31T00:00:00Z"), weekly.ResetsAt);
                Assert.Equal(Severity.Warning, weekly.Severity);
            },
            monthly =>
            {
                Assert.Equal("monthly", monthly.Label);
                Assert.Equal(110, monthly.Percent);
                Assert.Equal(DateTimeOffset.Parse("2026-09-15T00:00:00Z"), monthly.ResetsAt);
                Assert.Equal(Severity.Critical, monthly.Severity);
            });
    }

    [Fact]
    public async Task FetchAsync_SendsCredentialOnlyToFixedJsonEndpoint()
    {
        // Catches an endpoint or header regression that misroutes the credential or negotiates non-JSON content.
        Uri? requestUri = null;
        AuthenticationHeaderValue? authorization = null;
        string[] acceptedMediaTypes = [];
        bool noStore = false;
        var handler = new StubHttpMessageHandler(request =>
        {
            requestUri = request.RequestUri;
            authorization = request.Headers.Authorization;
            acceptedMediaTypes = request.Headers.Accept.Select(value => value.MediaType!).ToArray();
            noStore = request.Headers.CacheControl?.NoStore == true;
            return JsonResponse(ReadFixture("opencode-go-usage.json"));
        });

        ProviderSnapshot snapshot = await CreateProvider(handler).FetchSnapshotAsync(CancellationToken.None);

        Assert.Equal(HealthState.Ok, snapshot.Health);
        Assert.Equal("https://opencode.ai/zen/go/v1/usage", requestUri?.ToString());
        Assert.Equal("Bearer", authorization?.Scheme);
        Assert.Equal("unit-test-api-key", authorization?.Parameter);
        Assert.Equal(["application/json"], acceptedMediaTypes);
        Assert.True(noStore);
    }

    [Fact]
    public async Task FetchAsync_MissingCredentialFileReturnsQuietNotConfiguredSnapshot()
    {
        // Catches a missing OpenCode installation becoming a repeated provider alert or HTTP request.
        var handler = new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run"));
        var provider = new OpenCodeGoProvider(
            Path.Combine(_directory.Path, "missing-auth.json"),
            handler,
            SeverityFromPercent,
            null,
            OpenCodeGoProvider.OpenCredentialStream,
            new StubWorkspaceReader(() => null),
            new StubConsoleClient(() => throw new Xunit.Sdk.XunitException("Console HTTP must not run")));

        ProviderFetchResult result = await provider.FetchAsync(CancellationToken.None);
        ProviderSnapshot snapshot = Assert.IsType<ProviderSnapshot>(result.Snapshot);

        Assert.Equal(ProviderFetchOutcome.NotConfigured, result.Outcome);
        Assert.Equal(HealthState.Unreachable, snapshot.Health);
        Assert.Null(snapshot.Error);
        Assert.Empty(snapshot.Windows);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"opencode-go\":{\"type\":\"oauth\",\"key\":\"unit-test-api-key\"}}")]
    [InlineData("{\"opencode-go\":{\"type\":\"api\",\"key\":\"\"}}")]
    public async Task FetchAsync_MissingRequiredCredentialFieldsReturnsNotConfigured(string credential)
    {
        // Catches unrelated or incomplete OpenCode auth entries being used as bearer credentials.
        var handler = new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run"));

        ProviderFetchResult result = await CreateConsoleProvider(
            handler,
            new StubWorkspaceReader(() => null),
            new StubConsoleClient(() => throw new Xunit.Sdk.XunitException("Console HTTP must not run")),
            credential).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.NotConfigured, result.Outcome);
        Assert.Equal(HealthState.Unreachable, Assert.IsType<ProviderSnapshot>(result.Snapshot).Health);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task FetchAsync_RejectedCredentialReturnsSanitizedAuthExpired(HttpStatusCode statusCode)
    {
        // Catches rejected credentials being reported as transient failures or leaking response content.
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("sensitive server response"),
        });

        ProviderFetchResult result = await CreateProvider(handler).FetchAsync(CancellationToken.None);
        ProviderSnapshot snapshot = Assert.IsType<ProviderSnapshot>(result.Snapshot);

        Assert.Equal(ProviderFetchOutcome.AuthenticationRequired, result.Outcome);
        Assert.Equal(statusCode, result.StatusCode);
        Assert.Equal(HealthState.AuthExpired, snapshot.Health);
        Assert.Equal("re-auth: run opencode auth login", snapshot.Error);
        Assert.DoesNotContain("sensitive", snapshot.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchAsync_MalformedOptionalWindowsDoNotDiscardValidWindow()
    {
        // Catches one omitted or malformed optional window invalidating all OpenCode Go usage.
        const string response = """
            {"usage":{"rolling":{"percent":42,"resetsAt":"2026-08-27T12:00:00Z"},"weekly":{"percent":"bad","resetsAt":"bad"}}}
            """;
        var handler = new StubHttpMessageHandler(_ => JsonResponse(response));

        ProviderSnapshot snapshot = await CreateProvider(handler).FetchSnapshotAsync(CancellationToken.None);

        UsageWindow rolling = Assert.Single(snapshot.Windows);
        Assert.Equal("rolling", rolling.Label);
        Assert.Equal(42, rolling.Percent);
    }

    [Theory]
    [InlineData("{\"usage\":", "application/json")]
    [InlineData("<html>login</html>", "text/html")]
    [InlineData("{\"usage\":{\"rolling\":{\"percent\":23}}}", "application/json")]
    public async Task FetchAsync_InvalidUsageResponseReturnsInvalidResponse(string body, string contentType)
    {
        // Catches malformed, non-JSON, or incomplete responses being published as empty successful snapshots.
        var handler = new StubHttpMessageHandler(_ => JsonResponse(body, contentType));

        ProviderFetchResult result = await CreateProvider(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.InvalidResponse, result.Outcome);
        Assert.Null(result.Snapshot);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"usage\"")]
    public async Task FetchAsync_NonObjectJsonRootReturnsInvalidResponse(string body)
    {
        // Catches valid JSON scalars and arrays escaping the provider as unhandled mapping exceptions.
        var handler = new StubHttpMessageHandler(_ => JsonResponse(body));

        ProviderFetchResult result = await CreateProvider(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.InvalidResponse, result.Outcome);
        Assert.Null(result.Snapshot);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "60", 60)]
    [InlineData(HttpStatusCode.ServiceUnavailable, null, 300)]
    public async Task FetchAsync_RateLimitReturnsSafeCooldown(
        HttpStatusCode statusCode,
        string? retryAfter,
        int expectedSeconds)
    {
        // Catches OpenCode Go rate limiting erasing retained data or bypassing the shared cooldown policy.
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
    public async Task FetchAsync_ServerFailureReturnsTransientFailure()
    {
        // Catches an endpoint outage replacing retained quota data with an empty snapshot.
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        ProviderFetchResult result = await CreateProvider(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.TransientFailure, result.Outcome);
        Assert.Equal(HttpStatusCode.BadGateway, result.StatusCode);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task FetchAsync_StreamsResponseIntoBoundedJsonReader()
    {
        // Catches HttpClient buffering the entire body before the shared JSON size limit is applied.
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new HeadersOnlyContent(ReadFixture("opencode-go-usage.json")),
        });

        ProviderSnapshot snapshot = await CreateProvider(handler).FetchSnapshotAsync(CancellationToken.None);

        Assert.Equal(HealthState.Ok, snapshot.Health);
        Assert.Equal(3, snapshot.Windows.Length);
    }

    [Fact]
    public async Task FetchAsync_CallerCancellationPropagates()
    {
        // Catches a provider request that drops the poller's linked cancellation and timeout token.
        var handler = new BlockingHttpMessageHandler();
        using var cancellation = new CancellationTokenSource();

        Task<ProviderFetchResult> fetch = CreateProvider(handler).FetchAsync(cancellation.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
    }

    [Fact]
    public async Task FetchAsync_StopsReadingAfterRequiredCredentialFields()
    {
        // Catches a credential reader that buffers unrelated provider credentials after the OpenCode Go key.
        var handler = new StubHttpMessageHandler(_ => JsonResponse(ReadFixture("opencode-go-usage.json")));
        var credential = new SingleByteCredentialStream(
            """
            {"opencode-go":{"type":"api","key":"unit-test-api-key","metadata":"protected-tail"}}
            """,
            "metadata");
        var provider = new OpenCodeGoProvider(
            "credential-stream.json",
            handler,
            SeverityFromPercent,
            null,
            _ => credential);

        ProviderSnapshot snapshot = await provider.FetchSnapshotAsync(CancellationToken.None);

        Assert.Equal(HealthState.Ok, snapshot.Health);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_MalformedCredentialThrowsSanitizedFailure()
    {
        // Catches a credential parse failure whose exception exposes the API key or document body.
        var handler = new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run"));
        OpenCodeGoProvider provider = CreateProvider(
            handler,
            "{\"opencode-go\":{\"type\":\"api\",\"key\":\"unit-test-api-key\"");

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.FetchSnapshotAsync(CancellationToken.None));

        Assert.DoesNotContain("unit-test-api-key", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_ApiKeyTakesPrecedenceOverConsoleDiscovery()
    {
        // Catches automatic Console discovery replacing the stable API-key contract.
        var handler = new StubHttpMessageHandler(_ => JsonResponse(ReadFixture("opencode-go-usage.json")));
        var accountReader = new StubWorkspaceReader(() => throw new Xunit.Sdk.XunitException(
            "Console discovery must not run when an API key is configured."));
        var consoleClient = new StubConsoleClient(() => throw new Xunit.Sdk.XunitException(
            "Console HTTP must not run when an API key is configured."));
        OpenCodeGoProvider provider = CreateConsoleProvider(
            handler,
            accountReader,
            consoleClient,
            credential: """
                {"opencode-go":{"type":"api","key":"unit-test-api-key"}}
                """);

        ProviderFetchResult result = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.Success, result.Outcome);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_NoApiKeyAndConsoleCommandUnavailable_ReturnsNotConfigured()
    {
        // Catches direct fetches invoking Console account discovery without its local prerequisite.
        string credentialPath = _directory.WriteFile("opencode-no-console-auth.json", "{}");
        var provider = new OpenCodeGoProvider(
            credentialPath,
            new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run")),
            SeverityFromPercent,
            null,
            OpenCodeGoProvider.OpenCredentialStream,
            new StubWorkspaceReader(() => throw new Xunit.Sdk.XunitException("Accounts must not be read")),
            new StubConsoleClient(() => throw new Xunit.Sdk.XunitException("Console HTTP must not run")),
            _ => false,
            _ => false);

        ProviderFetchResult result = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.NotConfigured, result.Outcome);
        Assert.Equal(HealthState.Unreachable, Assert.IsType<ProviderSnapshot>(result.Snapshot).Health);
    }

    [Fact]
    public async Task FetchAsync_NoApiKey_UsesActiveWorkspaceUsage()
    {
        // Catches automatic Console discovery requiring a persisted settings object.
        ImmutableArray<UsageWindow> windows =
            [new("rolling", 42, DateTimeOffset.Parse("2026-08-27T12:00:00Z"), Severity.Normal)];
        OpenCodeGoProvider provider = CreateConsoleProvider(
            new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("API-key HTTP must not run")),
            new StubWorkspaceReader(() => ConsoleWorkspace()),
            new StubConsoleClient(() => new OpenCodeConsoleFetchResult(
                OpenCodeConsoleFetchOutcome.Success,
                windows)));

        ProviderFetchResult result = await provider.FetchAsync(CancellationToken.None);
        ProviderSnapshot snapshot = Assert.IsType<ProviderSnapshot>(result.Snapshot);

        Assert.Equal(ProviderFetchOutcome.Success, result.Outcome);
        Assert.Equal(HealthState.Ok, snapshot.Health);
        Assert.Equal(windows, snapshot.Windows);
    }

    [Fact]
    public async Task FetchAsync_UsesCurrentWorkspaceOnEveryFetch()
    {
        OpenCodeConsoleActiveWorkspace workspace = ConsoleWorkspace();
        int request = 0;
        var client = new StubConsoleClient(() => new OpenCodeConsoleFetchResult(
            OpenCodeConsoleFetchOutcome.Success,
            [new UsageWindow("rolling", ++request == 1 ? 10 : 20, null, Severity.Normal)]));
        OpenCodeGoProvider provider = CreateConsoleProvider(
            new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("Unexpected HTTP")),
            new StubWorkspaceReader(() => workspace), client);

        ProviderSnapshot first = await provider.FetchSnapshotAsync(CancellationToken.None);
        workspace = workspace with { AccountId = "account-next", OrganizationId = "org-next", AccessToken = "access-next" };
        ProviderSnapshot second = await provider.FetchSnapshotAsync(CancellationToken.None);

        Assert.Equal(10, Assert.Single(first.Windows).Percent);
        Assert.Equal(20, Assert.Single(second.Windows).Percent);
        Assert.Equal(["org-test", "org-next"], client.Workspaces.Select(item => item.OrganizationId));
        Assert.Equal(["access-test", "access-next"], client.Workspaces.Select(item => item.AccessToken));
    }

    [Fact]
    public async Task FetchAsync_ExpiredActiveWorkspaceReturnsAuthenticationRequiredWithoutHttp()
    {
        var expired = ConsoleWorkspace() with { ExpiresAt = DateTimeOffset.Parse("2026-08-26T00:00:00Z") };
        var client = new StubConsoleClient(() => throw new Xunit.Sdk.XunitException(
            "Expired Console tokens must not be sent."));
        OpenCodeGoProvider provider = CreateConsoleProvider(
            new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("Unexpected HTTP")),
            new StubWorkspaceReader(() => expired),
            client);

        ProviderFetchResult result = await provider.FetchAsync(CancellationToken.None);
        ProviderSnapshot snapshot = Assert.IsType<ProviderSnapshot>(result.Snapshot);

        Assert.Equal(ProviderFetchOutcome.AuthenticationRequired, result.Outcome);
        Assert.Equal(HealthState.AuthExpired, snapshot.Health);
        Assert.Equal("re-auth: run opencode console login", snapshot.Error);
    }

    [Fact]
    public async Task FetchAsync_ConsoleContractFailurePreservesRetainedData()
    {
        var client = new StubConsoleClient(() => new OpenCodeConsoleFetchResult(
            OpenCodeConsoleFetchOutcome.TransientFailure,
            [],
            HttpStatusCode.NotFound));
        OpenCodeGoProvider provider = CreateConsoleProvider(
            new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("Unexpected HTTP")),
            new StubWorkspaceReader(() => ConsoleWorkspace()),
            client);

        ProviderFetchResult result = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(ProviderFetchOutcome.TransientFailure, result.Outcome);
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        Assert.Null(result.Snapshot);
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("account")]
    [InlineData("token")]
    public async Task FetchAsync_WorkspaceChangeClearsRetainedUsageOnFailure(string change)
    {
        OpenCodeConsoleActiveWorkspace workspace = ConsoleWorkspace() with { ExpiresAt = null };
        int request = 0;
        var provider = new OpenCodeGoProvider(
            _directory.WriteFile("workspace-switch-auth.json", "{}"),
            new StubHttpMessageHandler(_ => throw new InvalidOperationException("Unexpected HTTP")),
            SeverityFromPercent,
            null,
            OpenCodeGoProvider.OpenCredentialStream,
            new StubWorkspaceReader(() => workspace),
            new StubConsoleClient(() => ++request == 1
                ? new OpenCodeConsoleFetchResult(OpenCodeConsoleFetchOutcome.Success,
                    [new UsageWindow("rolling", 25, null, Severity.Normal)])
                : new OpenCodeConsoleFetchResult(OpenCodeConsoleFetchOutcome.TransientFailure, [])),
            File.Exists,
            _ => true);
        var poller = new StatusPoller(
            [provider], () => AppSettings.Default,
            new RollingFileLog(Path.Combine(_directory.Path, "switch-log.txt")));

        ProviderSnapshot first = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);
        workspace = change switch
        {
            "workspace" => workspace with { OrganizationId = "org-next" },
            "account" => workspace with { AccountId = "account-next" },
            _ => workspace with { AccessToken = "access-refreshed" },
        };
        ProviderSnapshot second = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);

        Assert.Equal(25, Assert.Single(first.Windows).Percent);
        if (change == "token")
        {
            Assert.Equal(25, Assert.Single(second.Windows).Percent);
        }
        else
        {
            Assert.Empty(second.Windows);
        }
    }

    public void Dispose() => _directory.Dispose();

    private OpenCodeGoProvider CreateProvider(HttpMessageHandler handler) => CreateProvider(
        handler,
        """
        {"opencode-go":{"type":"api","key":"unit-test-api-key"}}
        """);

    private OpenCodeGoProvider CreateProvider(HttpMessageHandler handler, string credential)
    {
        string credentialPath = _directory.WriteFile(
            "opencode-auth.json",
            credential);
        return new OpenCodeGoProvider(
            credentialPath,
            handler,
            SeverityFromPercent);
    }

    private OpenCodeGoProvider CreateConsoleProvider(
        HttpMessageHandler handler,
        IOpenCodeConsoleActiveWorkspaceReader workspaceReader,
        IOpenCodeConsoleGoClient consoleClient,
        string credential = "{}")
    {
        string credentialPath = _directory.WriteFile("opencode-console-auth.json", credential);
        return new OpenCodeGoProvider(
            credentialPath,
            handler,
            SeverityFromPercent,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-08-27T00:00:00Z")),
            OpenCodeGoProvider.OpenCredentialStream,
            workspaceReader,
            consoleClient,
            File.Exists,
            command => command == "opencode");
    }

    private static OpenCodeConsoleActiveWorkspace ConsoleWorkspace() => new(
        "account-test",
        "access-test",
        "org-test",
        DateTimeOffset.Parse("2026-08-28T00:00:00Z"));

    private static Severity SeverityFromPercent(double? percent) =>
        SeverityPolicy.FromPercent(percent, 80, 95);

    private static HttpResponseMessage JsonResponse(string json, string contentType = "application/json") => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, contentType),
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

    private sealed class SingleByteCredentialStream : Stream
    {
        private readonly byte[] _content;
        private readonly byte[] _protectedTail;
        private int _position;

        public SingleByteCredentialStream(string credential, string protectedTail)
        {
            _content = Encoding.UTF8.GetBytes(credential);
            _protectedTail = Encoding.UTF8.GetBytes(protectedTail);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _content.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _content.Length)
            {
                return 0;
            }

            if (_position + _protectedTail.Length <= _content.Length &&
                _content.AsSpan(_position, _protectedTail.Length).SequenceEqual(_protectedTail))
            {
                throw new Xunit.Sdk.XunitException("Credential reader consumed unrelated credential material.");
            }

            buffer[offset] = _content[_position++];
            return 1;
        }

        public override int ReadByte()
        {
            byte[] buffer = new byte[1];
            return Read(buffer, 0, 1) == 0 ? -1 : buffer[0];
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StubWorkspaceReader(Func<OpenCodeConsoleActiveWorkspace?> read)
        : IOpenCodeConsoleActiveWorkspaceReader
    {
        public Task<OpenCodeConsoleActiveWorkspaceReadResult> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new OpenCodeConsoleActiveWorkspaceReadResult(
                OpenCodeConsoleActiveWorkspaceReadOutcome.Success, read()));
    }

    private sealed class StubConsoleClient(Func<OpenCodeConsoleFetchResult> fetch)
        : IOpenCodeConsoleGoClient
    {
        public List<OpenCodeConsoleActiveWorkspace> Workspaces { get; } = [];

        public Task<OpenCodeConsoleFetchResult> FetchAsync(
            OpenCodeConsoleActiveWorkspace workspace,
            CancellationToken cancellationToken)
        {
            Workspaces.Add(workspace);
            return Task.FromResult(fetch());
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class HeadersOnlyContent : HttpContent
    {
        private readonly byte[] _body;

        public HeadersOnlyContent(string body)
        {
            _body = Encoding.UTF8.GetBytes(body);
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new Xunit.Sdk.XunitException("HttpClient buffered the response before bounded parsing.");

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(_body, writable: false));

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    private sealed class BlockingHttpMessageHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable after cancellation.");
        }
    }
}
