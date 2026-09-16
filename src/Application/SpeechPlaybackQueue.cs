using System.Threading.Channels;
using VocaLink.Application.Abstractions;

namespace VocaLink.Application;

public enum SpeechQueueState
{
    Idle,
    Synthesizing,
    Playing,
    Failed
}

public sealed record SpeechQueueStateChanged(
    SpeechQueueState State,
    string? Message = null);

/// <summary>
/// 对白语音单消费者队列。每条回复整句合成，避免分句播放造成不自然停顿。
/// </summary>
public sealed class SpeechPlaybackQueue : IAsyncDisposable
{
    private readonly ITtsService _ttsService;
    private readonly IAudioPlaybackService _audioPlaybackService;
    private readonly Channel<SpeechRequest> _channel =
        Channel.CreateUnbounded<SpeechRequest>(
            new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;

    public SpeechPlaybackQueue(
        ITtsService ttsService,
        IAudioPlaybackService audioPlaybackService)
    {
        _ttsService = ttsService;
        _audioPlaybackService = audioPlaybackService;
        _worker = Task.Run(ProcessQueueAsync);
    }

    public event EventHandler<SpeechQueueStateChanged>? StateChanged;

    public ValueTask QueueAsync(
        string text,
        string language = "auto",
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            text,
            language,
            null,
            waitForPlayback: false,
            cancellationToken);

    /// <summary>
    /// 音频完成合成后调用界面回调，再同步开始播放整句语音。
    /// </summary>
    public async Task SpeakAsync(
        string text,
        string language,
        Func<string, CancellationToken, Task>? onAudioReady,
        CancellationToken cancellationToken = default)
    {
        await EnqueueAsync(
            text,
            language,
            onAudioReady,
            waitForPlayback: true,
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _shutdown.Cancel();
        _audioPlaybackService.Stop();

        try
        {
            await _worker;
        }
        catch (OperationCanceledException)
        {
        }

        _shutdown.Dispose();
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (var request in
                       _channel.Reader.ReadAllAsync(_shutdown.Token))
        {
            using var requestCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    _shutdown.Token,
                    request.CancellationToken);
            var requestToken = requestCancellation.Token;
            try
            {
                StateChanged?.Invoke(
                    this,
                    new SpeechQueueStateChanged(
                        SpeechQueueState.Synthesizing));
                var audioPath = await _ttsService.SynthesizeAsync(
                    request.Text,
                    request.Language,
                    requestToken);

                if (request.OnAudioReady is not null)
                {
                    await request.OnAudioReady(audioPath, requestToken);
                }

                StateChanged?.Invoke(
                    this,
                    new SpeechQueueStateChanged(SpeechQueueState.Playing));
                await _audioPlaybackService.PlayAsync(
                    audioPath,
                    requestToken);
                StateChanged?.Invoke(
                    this,
                    new SpeechQueueStateChanged(SpeechQueueState.Idle));
                request.Completion?.TrySetResult();
            }
            catch (OperationCanceledException)
                when (_shutdown.IsCancellationRequested)
            {
                request.Completion?.TrySetCanceled(_shutdown.Token);
                break;
            }
            catch (OperationCanceledException exception)
            {
                request.Completion?.TrySetCanceled(
                    exception.CancellationToken);
            }
            catch (Exception exception)
            {
                StateChanged?.Invoke(
                    this,
                    new SpeechQueueStateChanged(
                        SpeechQueueState.Failed,
                        exception.Message));
                request.Completion?.TrySetException(exception);
            }
        }
    }

    private async ValueTask EnqueueAsync(
        string text,
        string language,
        Func<string, CancellationToken, Task>? onAudioReady,
        bool waitForPlayback,
        CancellationToken cancellationToken)
    {
        var completion = waitForPlayback
            ? new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously)
            : null;
        await _channel.Writer.WriteAsync(
            new SpeechRequest(
                text,
                language,
                onAudioReady,
                completion,
                cancellationToken),
            cancellationToken);

        if (completion is not null)
        {
            await completion.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed record SpeechRequest(
        string Text,
        string Language,
        Func<string, CancellationToken, Task>? OnAudioReady,
        TaskCompletionSource? Completion,
        CancellationToken CancellationToken);
}
