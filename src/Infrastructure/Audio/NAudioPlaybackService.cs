using NAudio.Wave;
using VocaLink.Application.Abstractions;

namespace VocaLink.Infrastructure.Audio;

/// <summary>
/// 单实例音频播放器，供对白语音和 ACE 导出结果共用。
/// </summary>
public sealed class NAudioPlaybackService : IAudioPlaybackService, IDisposable
{
    private readonly object _gate = new();
    private WaveOutEvent? _output;
    private AudioFileReader? _reader;

    public async Task PlayAsync(
        string audioPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(audioPath))
        {
            throw new FileNotFoundException("找不到要播放的音频。", audioPath);
        }

        Stop();

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            _reader = new AudioFileReader(audioPath);
            _output = new WaveOutEvent();
            _output.Init(_reader);
            _output.PlaybackStopped += (_, eventArgs) =>
            {
                if (eventArgs.Exception is not null)
                {
                    completion.TrySetException(eventArgs.Exception);
                }
                else
                {
                    completion.TrySetResult();
                }
            };
            _output.Play();
        }

        using var registration = cancellationToken.Register(() =>
        {
            Stop();
            completion.TrySetCanceled(cancellationToken);
        });
        await completion.Task;
    }

    public void Pause()
    {
        lock (_gate)
        {
            _output?.Pause();
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            _output?.Play();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _output?.Stop();
            _output?.Dispose();
            _reader?.Dispose();
            _output = null;
            _reader = null;
        }
    }

    public void Dispose() => Stop();
}
