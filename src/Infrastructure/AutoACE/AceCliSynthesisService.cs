using System.Globalization;
using System.Text;
using System.Text.Json;
using VocaLink.Application.Abstractions;

namespace VocaLink.Infrastructure.AutoACE;

/// <summary>用 ACE 官方任务回执创建可继续编辑的翻唱工程，失败时保留工程供人工接管。</summary>
public sealed class AceCliSynthesisService : ISingingSynthesisService
{
    private static readonly SemaphoreSlim GenerationGate = new(1, 1);
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".wav", ".mp3", ".flac", ".m4a", ".ape", ".aac", ".ogg", ".aif", ".aiff" };
    private static readonly HashSet<string> Languages = new(StringComparer.Ordinal)
        { "chinese", "english", "japanese" };
    private readonly string _projectsDirectory;
    private readonly AceCliOptions _options;
    private readonly IAceCliClient _client;

    public AceCliSynthesisService(string projectsDirectory, AceCliOptions options,
        IAceCliClient? client = null)
    {
        _projectsDirectory = projectsDirectory;
        _options = options;
        _client = client ?? new AceCliClient(options);
    }

    public async Task<string?> CreateCoverAsync(SingingRequest request,
        IProgress<SingingProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!Extensions.Contains(Path.GetExtension(request.SourceAudioPath)))
            throw new NotSupportedException("不支持此音频扩展名。");
        if (!File.Exists(request.SourceAudioPath)) throw new FileNotFoundException("找不到原曲音频。");
        if (!Languages.Contains(request.Language)) throw new ArgumentException("歌曲语言无效。");
        await GenerationGate.WaitAsync(cancellationToken);
        try { return await RunCoverAsync(request, progress, cancellationToken); }
        finally { GenerationGate.Release(); }
    }

    private async Task<string> RunCoverAsync(SingingRequest request,
        IProgress<SingingProgress>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_projectsDirectory);
        var projectName = CreateProjectName(request.SourceAudioPath);
        string? ownedProjectPath = null;
        string? logPath = null;
        var ownedJobs = new HashSet<string>(StringComparer.Ordinal);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(_options.TaskTimeoutMinutes));
        var token = deadline.Token;

        void Report(SingingStage stage) => progress?.Report(new(stage, stage.ToString()));
        async Task LogAsync(string message)
        {
            if (logPath is null) return;
            try { await File.AppendAllTextAsync(logPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        async Task CheckProjectAsync(CancellationToken ct)
        {
            if (ownedProjectPath is null) return;
            var current = await _client.ExecuteAsync(["project", "dirty"], ct);
            if (!SamePath(Text(current, "projectPath"), ownedProjectPath))
                throw new AceCliException("PROJECT_CHANGED", "当前 ACE 工程已切换，已停止自动化以保护其他工程。");
        }

        async Task<JsonElement> RunAsync(CancellationToken ct, params string[] args)
        {
            ct.ThrowIfCancellationRequested();
            await CheckProjectAsync(ct);
            var result = await _client.ExecuteAsync(args, ct);
            await LogAsync($"{string.Join(' ', args)} => {result}");
            return result;
        }

        async Task<JsonElement> LaunchAsync(params string[] args)
        {
            token.ThrowIfCancellationRequested();
            await CheckProjectAsync(token);
            // 先收集回执再响应取消，确保只取消本任务真正启动的作业。
            var result = await _client.ExecuteAsync(args, CancellationToken.None);
            ownedJobs.Add(RequiredText(result, "jobId"));
            await LogAsync($"{string.Join(' ', args)} => {result}");
            token.ThrowIfCancellationRequested();
            return result;
        }

        async Task<JsonElement> WaitJobAsync(JsonElement launch)
        {
            var id = RequiredText(launch, "jobId");
            while (true)
            {
                var state = await RunAsync(token, "job", "get", id);
                var lifecycle = RequiredText(state, "lifecycle");
                if (lifecycle == "succeeded")
                {
                    ownedJobs.Remove(id);
                    return state;
                }
                if (lifecycle is "failed" or "cancelled" or "canceled" or "partially_succeeded")
                    throw new AceCliException("JOB_FAILED", $"ACE 作业 {id} 未成功完成：{state}");
                if (lifecycle is not ("running" or "pending" or "queued" or "created"))
                    throw new AceCliException("INVALID_RESPONSE", $"无法识别 ACE 作业状态：{lifecycle}");
                await Task.Delay(_options.PollIntervalMilliseconds, token);
            }
        }

        async Task<JsonElement> AudioClipAsync(string trackUuid)
        {
            var list = await RunAsync(token, "clip", "list", "--track-uuid", trackUuid);
            var clips = Items(list, "clips").Where(x => Text(x, "clipType") == "audio").ToArray();
            if (clips.Length != 1)
                throw new AceCliException("MISSING_RESULT", "ACE 没有返回唯一的分轨音频片段。");
            return clips[0];
        }

        async Task WaitAudioAsync(string trackUuid, string clipUuid)
        {
            while (true)
            {
                var tracks = await RunAsync(token, "track", "list");
                var track = Items(tracks, "tracks").SingleOrDefault(x => Text(x, "trackUuid") == trackUuid);
                if (track.ValueKind != JsonValueKind.Object)
                    throw new AceCliException("MISSING_RESULT", "音轨已被移除。");
                var clips = Items(await RunAsync(token, "clip", "list", "--track-uuid", trackUuid), "clips");
                var clipIndex = Array.FindIndex(clips, x => Text(x, "clipUuid") == clipUuid);
                if (clipIndex < 0) throw new AceCliException("MISSING_RESULT", "音频片段已被移除。");
                var state = await RunAsync(token, "clip", "audio-content", "--track-index",
                    track.GetProperty("trackIndex").GetInt32().ToString(CultureInfo.InvariantCulture),
                    "--clip-index", clipIndex.ToString(CultureInfo.InvariantCulture));
                var loading = RequiredText(state, "loadingState");
                if (loading == "loaded_success") return;
                if (loading != "not_loaded")
                    throw new AceCliException("AUDIO_INVALID", "ACE 无法解码音频，请更换音频或转为 WAV。");
                await Task.Delay(_options.PollIntervalMilliseconds, token);
            }
        }

        try
        {
            Report(SingingStage.Preparing);
            Report(SingingStage.LaunchingAce);
            await _client.EnsureRunningAsync(token);
            JsonElement[] sources = [];
            // 冷启动时代理可能先可连接、音源目录稍后才加载；按真实目录状态等待目标音源出现。
            for (var attempt = 0; attempt < 90 && sources.Length == 0; attempt++)
            {
                var catalog = await _client.ExecuteAsync(["sound-source", "list"], token);
                sources = Items(catalog, "soundSources").Where(x =>
                    Text(x, "kind") == "voice" &&
                    string.Equals(Text(x, "name"), _options.SingerName, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (sources.Length == 0)
                    await Task.Delay(_options.PollIntervalMilliseconds, token);
            }
            if (sources.Length != 1)
                throw new AceCliException("VOICE_UNAVAILABLE", "找不到唯一的目标歌声音源，请检查 ACE 音源和配置。");
            if (!Items(sources[0], "supportedLanguages").Any(x =>
                    string.Equals(x.GetString(), request.Language, StringComparison.OrdinalIgnoreCase)))
                throw new AceCliException("VOICE_LANGUAGE", "当前歌声音源不支持所选歌曲语言。");
            var singerRef = RequiredText(sources[0], "ref");

            // 官方 project new 会保护未保存工程；绝不丢弃用户修改。
            await RunAsync(token, "project", "new");
            var saved = await RunAsync(token, "project", "save-as",
                Path.Combine(_projectsDirectory, projectName + ".acep"));
            ownedProjectPath = Path.GetFullPath(RequiredText(saved, "savedPath"));
            var projectsRoot = Path.GetFullPath(_projectsDirectory) + Path.DirectorySeparatorChar;
            if (!ownedProjectPath.StartsWith(projectsRoot, StringComparison.OrdinalIgnoreCase))
                throw new AceCliException("PROJECT_CHANGED", "ACE 工程保存路径超出 VocaLink 工程目录。");
            logPath = Path.Combine(Path.GetDirectoryName(ownedProjectPath)!, "vocalink-automation.log");
            await LogAsync($"Source: {request.SourceAudioPath}");

            Report(SingingStage.ImportingAudio);
            var imported = await RunAsync(token, "import", "file", "--path", request.SourceAudioPath);
            var originalTrack = RequiredText(imported, "trackUuid");
            var originalClip = RequiredText(imported, "clipUuid");
            await WaitAudioAsync(originalTrack, originalClip);

            Report(SingingStage.AnalyzingTempo);
            var tempoLaunch = await LaunchAsync("tempo", "analyze", "--clip-uuid", originalClip);
            var tempoResult = await WaitJobAsync(tempoLaunch);
            var analysisId = FindText(tempoLaunch, "analysisId") ?? FindText(tempoResult, "analysisId")
                ?? throw new AceCliException("MISSING_RESULT", "ACE 曲速分析没有返回可应用的结果。");
            await RunAsync(token, "tempo", "apply-beat-analysis", "--analysis-id", analysisId);
            var tempo = await RunAsync(token, "tempo", "get");
            if (Items(tempo, "points").Length == 0)
                throw new AceCliException("MISSING_RESULT", "ACE 未将分析曲速写入工程。");
            await RunAsync(token, "project", "save");

            Report(SingingStage.SeparatingStems);
            var split = await LaunchAsync("generative", "stem-split", "--clip-uuid", originalClip, "--mode", "basic");
            await WaitJobAsync(split);
            var stemTracks = Items(split, "trackUuids").Select(x => x.GetString()!).ToArray();
            if (stemTracks.Length != 2 || stemTracks.Distinct().Count() != 2)
                throw new AceCliException("MISSING_RESULT", "基础分轨没有返回两条不同音轨。");
            // basic 的官方顺序为人声、伴奏；只使用本次回执 UUID。
            var vocal = await AudioClipAsync(stemTracks[0]);
            var backing = await AudioClipAsync(stemTracks[1]);
            await WaitAudioAsync(stemTracks[0], RequiredText(vocal, "clipUuid"));
            await WaitAudioAsync(stemTracks[1], RequiredText(backing, "clipUuid"));

            Report(SingingStage.ConvertingVocal);
            // 官方接口要求明确语言；直接使用用户选择，不启动额外语言模型。
            var conversion = await LaunchAsync("generative", "vocal2midi", "--clip-uuid",
                RequiredText(vocal, "clipUuid"), "--language", request.Language, "--apply-pitch", "true");
            await WaitJobAsync(conversion);
            var singingTrack = RequiredText(conversion, "trackUuid");
            var notes = await RunAsync(token, "clip", "list", "--track-uuid", singingTrack);
            if (!Items(notes, "clips").Any(x => Text(x, "clipType") == "sing" &&
                    x.TryGetProperty("noteCount", out var count) && count.GetInt32() > 0))
                throw new AceCliException("MISSING_RESULT", "转换任务结束但没有生成可用的歌唱音符。");

            Report(SingingStage.SelectingSinger);
            var loaded = await RunAsync(token, "sound-source", "load", "--track-uuid", singingTrack,
                "--source", singerRef);
            if (Text(loaded, "ref") != singerRef)
                throw new AceCliException("VOICE_UNAVAILABLE", "歌声音源加载结果与目标不一致。");
            var voiceState = await RunAsync(token, "sound-source", "get", "--track-uuid", singingTrack);
            if (Text(voiceState, "state") != "ready" ||
                !voiceState.TryGetProperty("soundSource", out var loadedSource) || Text(loadedSource, "ref") != singerRef)
                throw new AceCliException("VOICE_UNAVAILABLE", "歌声音源尚不可用。");

            // 不自动平衡音量：静音原曲与提取人声，歌声和伴奏保持 unity，交给用户在 ACE 调整。
            foreach (var track in new[] { originalTrack, stemTracks[0] })
                await RunAsync(token, "track", "set", "--track-uuid", track, "--mute", "true", "--solo", "false");
            foreach (var track in new[] { stemTracks[1], singingTrack })
                await RunAsync(token, "track", "set", "--track-uuid", track, "--mute", "false", "--solo", "false", "--gain", "1");

            Report(SingingStage.SavingProject);
            await RunAsync(token, "project", "save");
            AceWindowVisibility.BringToFront();
            await RunAsync(token, "transport", "seek", "--time", "0s");
            await RunAsync(token, "transport", "play");
            await LogAsync($"Completed project: {ownedProjectPath}");
            Report(SingingStage.Completed);
            return ownedProjectPath;
        }
        catch (Exception ex)
        {
            await LogAsync($"Stopped: {ex}");
            foreach (var id in ownedJobs)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await CheckProjectAsync(cleanup.Token);
                    await _client.ExecuteAsync(["job", "cancel", id], cleanup.Token);
                }
                catch (Exception cleanupError) { await LogAsync($"Cancel {id}: {cleanupError.Message}"); }
            }
            if (ownedProjectPath is not null) AceWindowVisibility.BringToFront();
            Report(SingingStage.ManualTakeover);
            if (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                throw new AceCliException("TASK_TIMEOUT", "处理未完成，请在 ACE Studio 中人工接管。");
            throw;
        }
    }

    private string CreateProjectName(string sourcePath)
    {
        var raw = Path.GetFileNameWithoutExtension(sourcePath).Normalize(NormalizationForm.FormKC);
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var builder = new StringBuilder();
        var previousSpace = false;
        foreach (var character in raw)
        {
            var value = invalid.Contains(character) || char.IsControl(character) ? '_' : character;
            if (char.IsWhiteSpace(value))
            {
                if (!previousSpace) builder.Append(' ');
                previousSpace = true;
            }
            else
            {
                builder.Append(value);
                previousSpace = false;
            }
        }
        var songName = builder.ToString().Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(songName)) songName = "未命名歌曲";
        if (songName.Length > 80) songName = songName[..80].TrimEnd(' ', '.');
        var baseName = songName;
        var candidate = baseName;
        for (var suffix = 1; ProjectNameExists(candidate); suffix++)
            candidate = $"{baseName} ({suffix})";
        return candidate;
    }

    private bool ProjectNameExists(string projectName) =>
        Directory.Exists(Path.Combine(_projectsDirectory, projectName)) ||
        File.Exists(Path.Combine(_projectsDirectory, projectName + ".acep"));

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string RequiredText(JsonElement element, string name) => Text(element, name)
        ?? throw new AceCliException("INVALID_RESPONSE", $"ACE 回执缺少字段 {name}。");
    private static JsonElement[] Items(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToArray() : [];
    private static bool SamePath(string? left, string right) => !string.IsNullOrEmpty(left) &&
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    private static string? FindText(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(name) && property.Value.ValueKind == JsonValueKind.String)
                    return property.Value.GetString();
                var nested = FindText(property.Value, name);
                if (nested is not null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindText(item, name);
                if (nested is not null) return nested;
            }
        return null;
    }
}
