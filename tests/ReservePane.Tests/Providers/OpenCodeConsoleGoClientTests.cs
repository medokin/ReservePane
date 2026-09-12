using System.Net;
using System.Text;
using ReservePane.Model;
using ReservePane.Providers;
using ReservePane.Tests.Support;

namespace ReservePane.Tests.Providers;

public sealed class OpenCodeConsoleGoClientTests
{
    private static readonly OpenCodeConsoleActiveWorkspace Workspace = new(
        "account-test",
        "access-test",
        "org-test",
        DateTimeOffset.Parse("2026-08-28T00:00:00Z"));

    [Fact]
    public async Task FetchAsync_KnownActiveWorkspaceDoesNotDependOnOrganizationDiscovery()
    {
        // Catches organization discovery hiding valid usage for the active workspace.
        var handler = new StubHttpMessageHandler(request =>
            request.RequestUri?.AbsolutePath == "/console/api/go/status"
                ? JsonResponse(ValidStatus)
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var client = new OpenCodeConsoleGoClient(handler, SeverityFromPercent);

        OpenCodeConsoleFetchResult result = await client.FetchAsync(
            Workspace,
            CancellationToken.None);

        Assert.Equal(OpenCodeConsoleFetchOutcome.Success, result.Outcome);
        Assert.Equal(3, result.Windows.Length);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_UsesActiveWorkspaceAndMapsGoMeters()
    {
        // Catches incorrect active workspace routing or private-contract meter mapping.
        var requests = new List<(Uri? Uri, string? Bearer, string? OrgId, bool? NoStore)>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add((
                request.RequestUri,
                request.Headers.Authorization?.Parameter,
                request.Headers.TryGetValues("x-org-id", out IEnumerable<string>? values)
                    ? Assert.Single(values)
                    : null,
                request.Headers.CacheControl?.NoStore));
            return JsonResponse(ValidStatus);
        });
        var client = new OpenCodeConsoleGoClient(handler, SeverityFromPercent);

        OpenCodeConsoleFetchResult result = await client.FetchAsync(Workspace, CancellationToken.None);

        Assert.Equal(OpenCodeConsoleFetchOutcome.Success, result.Outcome);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Collection(
            result.Windows,
            rolling => AssertWindow(rolling, "rolling", 25, "2026-08-27T13:00:00Z"),
            weekly => AssertWindow(weekly, "weekly", 50, "2026-09-01T00:00:00Z"),
            monthly => AssertWindow(monthly, "monthly", 75, "2026-09-27T00:00:00Z"));
        var request = Assert.Single(requests);
        Assert.Equal("https://opencode.ai/console/api/go/status", request.Uri?.ToString());
        Assert.Equal("access-test", request.Bearer);
        Assert.Equal("org-test", request.OrgId);
        Assert.True(request.NoStore);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"access\":null}")]
    public async Task FetchAsync_NonGoWorkspaceIsSuccessfulButIneligible(string status)
    {
        var handler = Handler(JsonResponse(status));
        var client = new OpenCodeConsoleGoClient(handler, SeverityFromPercent);

        OpenCodeConsoleFetchResult result = await client.FetchAsync(Workspace, CancellationToken.None);

        Assert.Equal(OpenCodeConsoleFetchOutcome.Success, result.Outcome);
        Assert.Empty(result.Windows);
    }

    [Fact]
    public async Task FetchAsync_NullFiveHourResetKeepsAvailableWindow()
    {
        // Catches the nullable upstream rolling reset invalidating an otherwise active Go subscription.
        string status = ValidStatus.Replace(
            "\"resetsAt\": \"2026-08-27T13:00:00Z\"",
            "\"resetsAt\": null",
            StringComparison.Ordinal);
        var client = new OpenCodeConsoleGoClient(Handler(JsonResponse(status)), SeverityFromPercent);

        OpenCodeConsoleFetchResult result = await client.FetchAsync(Workspace, CancellationToken.None);

        UsageWindow rolling = Assert.Single(result.Windows, window => window.Label == "rolling");
        Assert.Null(rolling.ResetsAt);
    }

    [Fact]
    public async Task FetchAsync_ComputesPercentageBeforeFloatingPointConversion()
    {
        // Catches arbitrarily large micro-cent counters becoming infinity divided by infinity.
        string used = "1" + new string('0', 400);
        string limit = "2" + new string('0', 400);
        string status = ValidStatus
            .Replace("\"limitMicroCents\": \"400\"", $"\"limitMicroCents\": \"{limit}\"", StringComparison.Ordinal)
            .Replace("\"usedMicroCents\": \"100\"", $"\"usedMicroCents\": \"{used}\"", StringComparison.Ordinal);
        var client = new OpenCodeConsoleGoClient(Handler(JsonResponse(status)), SeverityFromPercent);

        OpenCodeConsoleFetchResult result = await client.FetchAsync(Workspace, CancellationToken.None);

        UsageWindow rolling = Assert.Single(
            result.Windows,
            window => window.Label == "rolling");
        Assert.Equal(50, rolling.Percent);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task FetchAsync_RejectedConsoleTokenReturnsAuthenticationRequired(HttpStatusCode statusCode)
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("sensitive response"),
        });
        var client = new OpenCodeConsoleGoClient(handler, SeverityFromPercent);

        OpenCodeConsoleFetchResult result = await client.FetchAsync(Workspace, CancellationToken.None);

        Assert.Equal(OpenCodeConsoleFetchOutcome.AuthenticationRequired, result.Outcome);
        Assert.Equal(statusCode, result.StatusCode);
        Assert.Empty(result.Windows);
    }

    [Fact]
    public async Task FetchAsync_MissingPrivateRouteReturnsTransientFailure()
    {
        var client = new OpenCodeConsoleGoClient(
            Handler(new HttpResponseMessage(HttpStatusCode.NotFound)),
            SeverityFromPercent);

        OpenCodeConsoleFetchResult result = await client.FetchAsync(Workspace, CancellationToken.None);

        Assert.Equal(OpenCodeConsoleFetchOutcome.TransientFailure, result.Outcome);
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
    }

    [Fact]
    public async Task FetchAsync_RateLimitReturnsBoundedRetryDelay()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", "120");
        var client = new OpenCodeConsoleGoClient(Handler(response), SeverityFromPercent);

        OpenCodeConsoleFetchResult result = await client.FetchAsync(Workspace, CancellationToken.None);

        Assert.Equal(OpenCodeConsoleFetchOutcome.RateLimited, result.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(2), result.RetryAfter);
    }

    [Theory]
    [InlineData("{\"access\":")]
    [InlineData("{\"access\":{\"startsAt\":\"2026-08-27T00:00:00Z\",\"endsAt\":\"2026-09-27T00:00:00Z\",\"meters\":{}}}")]
    public async Task FetchAsync_InvalidStatusReturnsSanitizedInvalidResponse(string status)
    {
        var client = new OpenCodeConsoleGoClient(Handler(JsonResponse(status)), SeverityFromPercent);

        OpenCodeConsoleFetchResult result = await client.FetchAsync(Workspace, CancellationToken.None);

        Assert.Equal(OpenCodeConsoleFetchOutcome.InvalidResponse, result.Outcome);
        Assert.Empty(result.Windows);
    }

    [Fact]
    public async Task FetchAsync_CallerCancellationPropagates()
    {
        var handler = new BlockingHandler();
        var client = new OpenCodeConsoleGoClient(handler, SeverityFromPercent);
        using var cancellation = new CancellationTokenSource();

        Task<OpenCodeConsoleFetchResult> fetch = client.FetchAsync(Workspace, cancellation.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
    }

    private static StubHttpMessageHandler Handler(HttpResponseMessage response) => new(_ => response);

    private static void AssertWindow(UsageWindow window, string label, double percent, string resetsAt)
    {
        Assert.Equal(label, window.Label);
        Assert.Equal(percent, window.Percent);
        Assert.Equal(DateTimeOffset.Parse(resetsAt), window.ResetsAt);
    }

    private static Severity SeverityFromPercent(double? percent) =>
        SeverityPolicy.FromPercent(percent, 80, 95);

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private const string ValidStatus =
        """
        {
          "subscriberUserId": "subscriber-test",
          "useBalance": true,
          "access": {
            "startsAt": "2026-08-27T00:00:00Z",
            "endsAt": "2026-09-27T00:00:00Z",
            "cancelAtPeriodEnd": false,
            "meters": {
              "fiveHour": {
                "startsAt": "2026-08-27T08:00:00Z",
                "resetsAt": "2026-08-27T13:00:00Z",
                "limitMicroCents": "400",
                "usedMicroCents": "100"
              },
              "week": {
                "startsAt": "2026-08-25T00:00:00Z",
                "resetsAt": "2026-09-01T00:00:00Z",
                "limitMicroCents": "1000",
                "usedMicroCents": "500"
              },
              "month": {
                "limitMicroCents": "2000",
                "usedMicroCents": "1500"
              }
            }
          },
          "renewalPaymentAttemptId": null
        }
        """;

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new Xunit.Sdk.XunitException("Unreachable");
        }
    }
}
