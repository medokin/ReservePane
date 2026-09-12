using System.Net;
using System.Text;
using Renci.SshNet;
using ReservePane.Providers;
using ReservePane.Tests.Support;

namespace ReservePane.Tests.Providers;

public sealed class OllamaIdentityTests
{
    [Theory]
    [InlineData(false, "GET", "/api/usage")]
    [InlineData(true, "POST", "/api/me")]
    public async Task CreateRequest_SignsFixedMethodPathAndTimestampWithoutContent(
        bool accountRequest, string method, string endpoint)
    {
        using var directory = new TemporaryDirectory();
        string keyPath = OllamaTestIdentity.Write(directory);
        using var key = new PrivateKeyFile(keyPath);
        using OllamaIdentity identity = OllamaIdentity.Read(keyPath);
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_789_142_400);

        using HttpRequestMessage request = accountRequest
            ? identity.CreateAccountRequest(now)
            : identity.CreateUsageRequest(now);

        Assert.Equal(method, request.Method.Method);
        Assert.Equal("https://ollama.com" + endpoint + "?ts=1789142400", request.RequestUri!.AbsoluteUri);
        Assert.Null(request.Content);
        Assert.True(request.Headers.CacheControl?.NoStore);
        Assert.Contains(request.Headers.Accept, accept => accept.MediaType == "application/json");
        string[] authorization = Assert.Single(request.Headers.GetValues("Authorization")).Split(':');
        Assert.Equal(2, authorization.Length);
        Assert.Equal(Convert.ToBase64String(key.HostKeyAlgorithms.Single().Data), authorization[0]);
        Assert.True(key.Key.VerifySignature(
            Encoding.UTF8.GetBytes(method + "," + endpoint + "?ts=1789142400"),
            Convert.FromBase64String(authorization[1])));

        var transport = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(new UsageOnlyHttpHandler(transport));
        using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, transport.RequestCount);
    }
}
