using System.Net;
using System.Net.Http;
using ReservePane.Core;
using ReservePane.Model;

namespace ReservePane.Providers;

public sealed class ProviderRegistry : IDisposable
{
    private static readonly TimeSpan ConnectionLifetime = TimeSpan.FromMinutes(15);
    private readonly Func<AppSettings> _settings;
    private readonly UsageOnlyHttpHandler[] _handlers;
    private bool _disposed;

    private ProviderRegistry(
        Func<AppSettings> settings,
        AppPaths paths,
        UsageOnlyHttpHandler[] handlers)
    {
        _settings = settings;
        _handlers = handlers;
        Providers =
        [
            new ClaudeProvider(paths.ClaudeCredentialsPath, handlers[0], SeverityFromPercent),
            new CodexProvider(paths.CodexAuthPath, handlers[1], SeverityFromPercent),
            new GrokProvider(paths.GrokAuthPath, handlers[2], SeverityFromPercent),
            new OpenCodeGoProvider(paths.OpenCodeAuthPath, handlers[3], SeverityFromPercent),
            new OpenCodeCompanySeatProvider(handlers[4], SeverityFromPercent),
            new OllamaProvider(paths.OllamaPrivateKeyPath, handlers[5], SeverityFromPercent),
        ];
    }

    public IReadOnlyList<IStatusProvider> Providers { get; }

    internal IReadOnlyList<UsageOnlyHttpHandler> Handlers => _handlers;

    internal Severity SeverityFromPercent(double? percent)
    {
        AppSettings current = _settings();
        return SeverityPolicy.FromPercent(
            percent,
            current.WarningPercent,
            current.CriticalPercent);
    }

    public static ProviderRegistry Create(Func<AppSettings> settings, AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(paths);

        UsageOnlyHttpHandler[] handlers = Enumerable.Range(0, 6)
            .Select(static _ => CreateHandler())
            .ToArray();

        try
        {
            return new ProviderRegistry(settings, paths, handlers);
        }
        catch
        {
            foreach (UsageOnlyHttpHandler handler in handlers)
            {
                handler.Dispose();
            }

            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (UsageOnlyHttpHandler handler in _handlers)
        {
            handler.Dispose();
        }
    }

    private static UsageOnlyHttpHandler CreateHandler() => new(new SocketsHttpHandler
    {
        AutomaticDecompression =
            DecompressionMethods.GZip |
            DecompressionMethods.Deflate |
            DecompressionMethods.Brotli,
        AllowAutoRedirect = false,
        PooledConnectionLifetime = ConnectionLifetime,
    });
}
