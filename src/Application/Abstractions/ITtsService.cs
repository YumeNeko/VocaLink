namespace VocaLink.Application.Abstractions;

public interface ITtsService
{
    Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default);

    Task<string> SynthesizeAsync(
        string text,
        string language,
        CancellationToken cancellationToken = default);
}
