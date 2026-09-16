namespace VocaLink.Application.Abstractions;

public interface ISettingsStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<string?> GetAsync(
        string key,
        CancellationToken cancellationToken = default);

    Task SetAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default);
}
