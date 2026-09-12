using ReservePane.Model;

namespace ReservePane.Providers;

public interface IStatusProvider
{
    string Id { get; }

    string Label { get; }

    TimeSpan MinimumRefreshInterval => TimeSpan.Zero;

    Task<ProviderFetchResult> FetchAsync(CancellationToken cancellationToken);
}
