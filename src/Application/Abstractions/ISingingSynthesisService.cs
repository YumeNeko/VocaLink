namespace VocaLink.Application.Abstractions;

public enum SingingStage
{
    Preparing,
    LaunchingAce,
    ImportingAudio,
    SeparatingStems,
    ConvertingVocal,
    SelectingSinger,
    SavingProject,
    Completed,
    ManualTakeover,
    AnalyzingTempo
}

public sealed record SingingRequest(
    string SourceAudioPath,
    string Language,
    string? ScorePath = null);

public sealed record SingingProgress(
    SingingStage Stage,
    string Message);

public interface ISingingSynthesisService
{
    Task<string?> CreateCoverAsync(
        SingingRequest request,
        IProgress<SingingProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
