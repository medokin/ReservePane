using System.Net;
using ReservePane.Providers;
using ReservePane.Tests.Support;

namespace ReservePane.Tests.Providers;

public sealed class UsageOnlyHttpHandlerTests
{
    [Fact]
    public async Task SendAsync_BodylessOllamaAccountMetadataReachesTransport()
    {
        var transport = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(new UsageOnlyHttpHandler(transport));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://ollama.com/api/me?ts=1789142400");

        using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, transport.RequestCount);
    }

    [Theory]
    [InlineData("GET", "https://ollama.com/api/me?ts=1789142400")]
    [InlineData("PUT", "https://ollama.com/api/me?ts=1789142400")]
    [InlineData("PATCH", "https://ollama.com/api/me?ts=1789142400")]
    [InlineData("DELETE", "https://ollama.com/api/me?ts=1789142400")]
    [InlineData("HEAD", "https://ollama.com/api/me?ts=1789142400")]
    [InlineData("POST", "https://ollama.com/api/usage?ts=1789142400")]
    [InlineData("POST", "https://ollama.com/api/chat?ts=1789142400")]
    [InlineData("POST", "https://ollama.com/api/generate?ts=1789142400")]
    [InlineData("POST", "https://api.anthropic.com/api/oauth/usage")]
    [InlineData("POST", "https://ollama.com/api/me")]
    [InlineData("POST", "https://ollama.com/api/me?ts=0")]
    [InlineData("POST", "https://ollama.com/api/me?ts=-1")]
    [InlineData("POST", "https://ollama.com/api/me?ts=abc")]
    [InlineData("POST", "https://ollama.com/api/me?ts=1789142400&ts=1789142401")]
    [InlineData("POST", "https://ollama.com/api/me?ts=1789142400&prompt=sentinel-secret")]
    [InlineData("POST", "https://ollama.com/api/me/extra?ts=1789142400")]
    [InlineData("POST", "https://ollama.com.example.com/api/me?ts=1789142400")]
    [InlineData("POST", "https://api.ollama.com/api/me?ts=1789142400")]
    [InlineData("POST", "http://ollama.com/api/me?ts=1789142400")]
    [InlineData("POST", "https://ollama.com:444/api/me?ts=1789142400")]
    [InlineData("POST", "https://sentinel-secret@ollama.com/api/me?ts=1789142400")]
    [InlineData("POST", "https://ollama.com/api/me?ts=1789142400#sentinel-secret")]
    public async Task SendAsync_OllamaAccountExceptionDoesNotBroadenOtherRequests(string method, string url)
    {
        var transport = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(new UsageOnlyHttpHandler(transport));
        using var request = new HttpRequestMessage(new HttpMethod(method), url);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SendAsync(request, CancellationToken.None));

        Assert.Equal(0, transport.RequestCount);
        Assert.DoesNotContain("sentinel-secret", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sentinel-secret")]
    public async Task SendAsync_OllamaAccountWithAnyContentNeverReachesTransport(string body)
    {
        var transport = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(new UsageOnlyHttpHandler(transport));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://ollama.com/api/me?ts=1789142400")
        {
            Content = new StringContent(body),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(request, CancellationToken.None));

        Assert.Equal(0, transport.RequestCount);
    }

    [Theory]
    [InlineData("https://api.anthropic.com/api/oauth/usage")]
    [InlineData("https://api.anthropic.com/api/oauth/profile")]
    [InlineData("https://chatgpt.com/backend-api/wham/usage")]
    [InlineData("https://cli-chat-proxy.grok.com/v1/billing?format=credits")]
    [InlineData("https://opencode.ai/console/api/orgs")]
    [InlineData("https://opencode.ai/console/api/orgs/current")]
    [InlineData("https://opencode.ai/console/api/budgets/users/member-1")]
    [InlineData("https://opencode.ai/console/api/budgets/users/member%20one")]
    [InlineData("https://opencode.ai/console/api/go/status")]
    [InlineData("https://opencode.ai/zen/go/v1/usage")]
    [InlineData("https://ollama.com/api/usage?ts=1789142400")]
    public async Task SendAsync_KnownUsageAndAccountMetadataReachesTransport(string url)
    {
        // Break caught: blocking an existing provider's read-only usage or account lookup.
        var transport = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(new UsageOnlyHttpHandler(transport));

        using HttpResponseMessage response = await client.GetAsync(url, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, transport.RequestCount);
    }

    [Theory]
    [InlineData("https://api.anthropic.com/v1/messages")]
    [InlineData("https://chatgpt.com/backend-api/responses")]
    [InlineData("https://cli-chat-proxy.grok.com/v1/chat/completions")]
    [InlineData("https://opencode.ai/zen/go/v1/responses")]
    [InlineData("https://opencode.ai/zen/go/v1/chat/completions")]
    [InlineData("https://ollama.com/api/chat")]
    [InlineData("https://ollama.com/api/generate")]
    [InlineData("http://localhost:11434/api/generate")]
    [InlineData("http://localhost:11434/api/version")]
    [InlineData("https://api.anthropic.com/api/oauth/usage/extra")]
    [InlineData("https://api.anthropic.com/api/oauth/usage?prompt=hello")]
    [InlineData("https://api.anthropic.com:444/api/oauth/usage")]
    [InlineData("http://api.anthropic.com/api/oauth/usage")]
    [InlineData("https://api.anthropic.com.example.com/api/oauth/usage")]
    [InlineData("https://sentinel-secret@api.anthropic.com/api/oauth/usage")]
    [InlineData("https://api.anthropic.com/api/oauth/usage#sentinel-secret")]
    [InlineData("https://cli-chat-proxy.grok.com/v1/billing")]
    [InlineData("https://cli-chat-proxy.grok.com/v1/billing?format=credits&prompt=hello")]
    [InlineData("https://opencode.ai/console/api/budgets/users/")]
    [InlineData("https://opencode.ai/console/api/budgets/users/member-1/extra")]
    [InlineData("https://opencode.ai/console/api/budgets/users/member%2Fextra")]
    [InlineData("https://opencode.ai/console/api/budgets/users/member%5Cextra")]
    [InlineData("https://opencode.ai/console/api/budgets/users/member%252Fextra")]
    [InlineData("https://opencode.ai/console/api/budgets/users/member-1?prompt=hello")]
    [InlineData("https://ollama.com/api/usage")]
    [InlineData("https://ollama.com/api/usage?ts=1789142400&prompt=hello")]
    [InlineData("https://ollama.com/api/usage?ts=abc")]
    [InlineData("https://ollama.com/api/usage?ts=")]
    [InlineData("https://ollama.com/api/usage?ts=-1")]
    [InlineData("https://ollama.com/api/me")]
    public async Task SendAsync_UnapprovedEndpointNeverReachesTransport(string url)
    {
        // Break caught: an inference route, changed origin, or extra parameters bypassing the boundary.
        var transport = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(new UsageOnlyHttpHandler(transport));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetAsync(url, CancellationToken.None));

        Assert.Equal(0, transport.RequestCount);
        Assert.DoesNotContain("sentinel-secret", exception.Message);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("HEAD")]
    public async Task SendAsync_NonGetMethodNeverReachesTransport(string method)
    {
        // Break caught: accepting a write merely because its URL matches a usage endpoint.
        var transport = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(new UsageOnlyHttpHandler(transport));
        using var request = new HttpRequestMessage(new HttpMethod(method), "https://api.anthropic.com/api/oauth/usage");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(request, CancellationToken.None));

        Assert.Equal(0, transport.RequestCount);
    }

    [Fact]
    public async Task SendAsync_GetWithBodyNeverReachesTransport()
    {
        // Break caught: permitting prompts or mutation payloads on an otherwise approved GET route.
        var transport = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(new UsageOnlyHttpHandler(transport));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage")
        {
            Content = new StringContent("sentinel-secret"),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(request, CancellationToken.None));

        Assert.Equal(0, transport.RequestCount);
    }

    [Fact]
    public void Send_SynchronousInferenceRequestNeverReachesTransport()
    {
        // Break caught: bypassing the same boundary by switching HttpClient to synchronous requests.
        var transport = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(new UsageOnlyHttpHandler(transport));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://ollama.com/api/generate");

        Assert.Throws<InvalidOperationException>(() => client.Send(request, CancellationToken.None));

        Assert.Equal(0, transport.RequestCount);
    }
}
