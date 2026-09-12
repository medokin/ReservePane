using System.Net;
using ReservePane.Core;
using ReservePane.Model;
using ReservePane.Providers;
using ReservePane.Tests.Support;

namespace ReservePane.Tests.Providers;

public sealed class ProviderRegistryTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    [Fact]
    public void Create_ReturnsTheSixCompiledProvidersInStableOrder()
    {
        AppSettings settings = AppSettings.Default;

        using ProviderRegistry registry = ProviderRegistry.Create(
            () => settings,
            CreatePaths());

        Assert.Collection(
            registry.Providers,
            provider => Assert.Equal("claude", provider.Id),
            provider => Assert.Equal("codex", provider.Id),
            provider => Assert.Equal("grok", provider.Id),
            provider => Assert.Equal("opencode-go", provider.Id),
            provider =>
            {
                Assert.Equal("opencode-company-seat", provider.Id);
                Assert.Equal("OpenCode", provider.Label);
            },
            provider => Assert.Equal("ollama", provider.Id));
    }

    [Fact]
    public void Create_OwnsExactlyOneConfiguredSocketsHandlerPerHost()
    {
        using ProviderRegistry registry = ProviderRegistry.Create(
            () => AppSettings.Default,
            CreatePaths());

        Assert.Equal(6, registry.Handlers.Count);
        Assert.Equal(6, registry.Handlers.Distinct().Count());
        Assert.All(registry.Handlers, guardedHandler =>
        {
            SocketsHttpHandler handler = Assert.IsType<SocketsHttpHandler>(guardedHandler.InnerHandler);
            Assert.Equal(
                DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                handler.AutomaticDecompression);
            Assert.Equal(TimeSpan.FromMinutes(15), handler.PooledConnectionLifetime);
            Assert.False(handler.AllowAutoRedirect);
        });
    }

    [Fact]
    public void SeverityDelegate_ReadsLatestThresholdsWithoutRecreatingProviders()
    {
        AppSettings settings = AppSettings.Default;
        using ProviderRegistry registry = ProviderRegistry.Create(() => settings, CreatePaths());
        IStatusProvider[] providers = registry.Providers.ToArray();

        Assert.Equal(Severity.Warning, registry.SeverityFromPercent(85));

        settings = settings with { WarningPercent = 90 };

        Assert.Equal(Severity.Normal, registry.SeverityFromPercent(85));
        Assert.Equal(providers, registry.Providers);
    }

    [Fact]
    public async Task Dispose_DisposesEveryOwnedHandlerAndIsIdempotent()
    {
        ProviderRegistry registry = ProviderRegistry.Create(
            () => AppSettings.Default,
            CreatePaths());
        HttpClient[] clients = registry.Handlers
            .Select(handler => new HttpClient(handler, disposeHandler: false))
            .ToArray();

        registry.Dispose();
        registry.Dispose();

        foreach (HttpClient client in clients)
        {
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => client.GetAsync("https://api.anthropic.com/api/oauth/usage", CancellationToken.None));
            client.Dispose();
        }
    }

    [Fact]
    public async Task Create_RejectsInferenceRequestsBeforeEveryProviderTransport()
    {
        // Break caught: a provider's registry transport is created without the usage-only boundary.
        using ProviderRegistry registry = ProviderRegistry.Create(() => AppSettings.Default, CreatePaths());

        foreach (UsageOnlyHttpHandler handler in registry.Handlers)
        {
            handler.InnerHandler!.Dispose();
            var transport = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            handler.InnerHandler = transport;
            using var client = new HttpClient(handler, disposeHandler: false);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.GetAsync("https://ollama.com/api/generate", CancellationToken.None));

            Assert.Equal(0, transport.RequestCount);
        }
    }

    public void Dispose() => _directory.Dispose();

    private AppPaths CreatePaths() => new(
        Path.Combine(_directory.Path, "claude.json"),
        Path.Combine(_directory.Path, "codex.json"),
        Path.Combine(_directory.Path, "opencode.json"),
        Path.Combine(_directory.Path, "grok.json"),
        Path.Combine(_directory.Path, "settings.json"),
        Path.Combine(_directory.Path, "log.txt"));
}
