namespace VocaLink.Application.Abstractions;

public interface IAudioPlaybackService
{
    Task PlayAsync(string audioPath, CancellationToken cancellationToken = default);

    void Pause();

    void Resume();

    void Stop();
}
