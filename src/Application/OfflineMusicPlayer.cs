using VocaLink.Application.Abstractions;

namespace VocaLink.Application;

public enum OfflineMusicState
{
    Stopped,
    Playing,
    Paused,
    Failed
}

public sealed record OfflineMusicStateChanged(
    OfflineMusicState State,
    string? TrackPath = null,
    string? Message = null);

/// <summary>
/// 从本地曲库随机循环播放歌曲，并支持上一首、下一首。
/// </summary>
public sealed class OfflineMusicPlayer : IAsyncDisposable
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".wav",
            ".mp3",
            ".aiff",
            ".aif",
            ".flac",
            ".m4a"
        };

    private readonly IAudioPlaybackService _audioPlaybackService;
    private readonly object _navigationGate = new();
    private readonly List<string> _history = [];
    private CancellationTokenSource? _playbackCancellation;
    private Task? _playbackTask;
    private string? _requestedTrack;

    public OfflineMusicPlayer(
        IAudioPlaybackService audioPlaybackService,
        string musicDirectory)
    {
        _audioPlaybackService = audioPlaybackService;
        MusicDirectory = musicDirectory;
        Directory.CreateDirectory(MusicDirectory);
    }

    public event EventHandler<OfflineMusicStateChanged>? StateChanged;

    public string MusicDirectory { get; }

    public bool IsPlaying =>
        _playbackTask is { IsCompleted: false } && !IsPaused;

    public bool IsPaused { get; private set; }

    public string? CurrentTrack { get; private set; }

    public bool Start()
    {
        var tracks = GetTracks();
        if (tracks.Length == 0)
        {
            StateChanged?.Invoke(
                this,
                new OfflineMusicStateChanged(
                    OfflineMusicState.Failed,
                    Message: "歌曲文件夹中没有可播放的音频文件。"));
            return false;
        }

        Stop();
        IsPaused = false;
        _playbackCancellation = new CancellationTokenSource();
        _playbackTask = PlayLoopAsync(_playbackCancellation.Token);
        return true;
    }

    public bool PlayNext()
    {
        var tracks = GetTracks();
        if (tracks.Length == 0)
        {
            return false;
        }

        var candidates = tracks
            .Where(path => !string.Equals(
                path,
                CurrentTrack,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        RequestTrack(
            candidates.Length == 0
                ? tracks[0]
                : candidates[Random.Shared.Next(candidates.Length)]);
        return true;
    }

    public bool PlayTrack(string path)
    {
        if (!File.Exists(path) ||
            !SupportedExtensions.Contains(Path.GetExtension(path)))
        {
            return false;
        }

        if (!IsPlaying)
        {
            if (IsPaused)
            {
                Resume();
                RequestTrack(path);
                return true;
            }

            lock (_navigationGate)
            {
                _requestedTrack = path;
            }

            IsPaused = false;
            _playbackCancellation = new CancellationTokenSource();
            _playbackTask = PlayLoopAsync(_playbackCancellation.Token);
            return true;
        }

        RequestTrack(path);
        return true;
    }

    public bool Pause()
    {
        if (_playbackTask is not { IsCompleted: false } ||
            IsPaused)
        {
            return false;
        }

        IsPaused = true;
        _audioPlaybackService.Pause();
        StateChanged?.Invoke(
            this,
            new OfflineMusicStateChanged(
                OfflineMusicState.Paused,
                CurrentTrack));
        return true;
    }

    public bool Resume()
    {
        if (_playbackTask is not { IsCompleted: false } ||
            !IsPaused)
        {
            return false;
        }

        IsPaused = false;
        _audioPlaybackService.Resume();
        StateChanged?.Invoke(
            this,
            new OfflineMusicStateChanged(
                OfflineMusicState.Playing,
                CurrentTrack));
        return true;
    }

    public bool PlayPrevious()
    {
        string? target;
        lock (_navigationGate)
        {
            target = _history.Count >= 2
                ? _history[^2]
                : _history.LastOrDefault();
        }

        if (target is null)
        {
            return false;
        }

        RequestTrack(target);
        return true;
    }

    public void Stop()
    {
        var wasActive =
            _playbackCancellation is not null ||
            _playbackTask is { IsCompleted: false };
        if (!wasActive)
        {
            return;
        }

        _playbackCancellation?.Cancel();
        _audioPlaybackService.Stop();
        _playbackCancellation?.Dispose();
        _playbackCancellation = null;
        _playbackTask = null;
        IsPaused = false;
        CurrentTrack = null;
        lock (_navigationGate)
        {
            _requestedTrack = null;
            _history.Clear();
        }

        StateChanged?.Invoke(
            this,
            new OfflineMusicStateChanged(OfflineMusicState.Stopped));
    }

    public async ValueTask DisposeAsync()
    {
        var worker = _playbackTask;
        Stop();
        if (worker is not null)
        {
            try
            {
                await worker;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task PlayLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var requested = TakeRequestedTrack();
                var tracks = requested is null
                    ? GetTracks()
                        .OrderBy(_ => Random.Shared.Next())
                        .ToArray()
                    : [requested];
                if (tracks.Length == 0)
                {
                    StateChanged?.Invoke(
                        this,
                        new OfflineMusicStateChanged(
                            OfflineMusicState.Failed,
                            Message: "歌曲文件夹中没有可播放的音频文件。"));
                    return;
                }

                foreach (var track in tracks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    CurrentTrack = track;
                    AddHistory(track);
                    StateChanged?.Invoke(
                        this,
                        new OfflineMusicStateChanged(
                            OfflineMusicState.Playing,
                            track));
                    try
                    {
                        await _audioPlaybackService.PlayAsync(
                            track,
                            cancellationToken);
                    }
                    catch (Exception exception)
                        when (exception is not OperationCanceledException)
                    {
                        StateChanged?.Invoke(
                            this,
                            new OfflineMusicStateChanged(
                                OfflineMusicState.Failed,
                                track,
                                exception.Message));
                    }

                    if (HasRequestedTrack())
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private string[] GetTracks() =>
        Directory.EnumerateFiles(
                MusicDirectory,
                "*.*",
                SearchOption.AllDirectories)
            .Where(path => SupportedExtensions.Contains(
                Path.GetExtension(path)))
            .ToArray();

    private void RequestTrack(string path)
    {
        lock (_navigationGate)
        {
            _requestedTrack = path;
        }

        IsPaused = false;
        _audioPlaybackService.Stop();
    }

    private string? TakeRequestedTrack()
    {
        lock (_navigationGate)
        {
            var value = _requestedTrack;
            _requestedTrack = null;
            return value;
        }
    }

    private bool HasRequestedTrack()
    {
        lock (_navigationGate)
        {
            return _requestedTrack is not null;
        }
    }

    private void AddHistory(string path)
    {
        lock (_navigationGate)
        {
            if (_history.Count == 0 ||
                !string.Equals(
                    _history[^1],
                    path,
                    StringComparison.OrdinalIgnoreCase))
            {
                _history.Add(path);
            }

            if (_history.Count > 50)
            {
                _history.RemoveAt(0);
            }
        }
    }
}
