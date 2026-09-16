namespace VocaLink.Desktop;

public sealed class LocalizationService
{
    public const string Chinese = "zh-CN";
    public const string English = "en-US";

    private static readonly IReadOnlyDictionary<string, string> ChineseTexts =
        new Dictionary<string, string>
        {
            ["Chat"] = "Chat",
            ["Voice"] = "Voice",
            ["Singing"] = "Sing",
            ["Music"] = "本地音乐",
            ["Settings"] = "设置",
            ["Exit"] = "退出",
            ["ShowPet"] = "显示桌宠",
            ["HidePet"] = "隐藏桌宠",
            ["Send"] = "发送",
            ["You"] = "你",
            ["Tianyi"] = "天依",
            ["Welcome"] = "你好呀，今天想和我聊些什么？",
            ["OfflineLabel"] = "离线预设回复",
            ["Ready"] = "准备就绪",
            ["Thinking"] = "天依正在思考中",
            ["Online"] = "在线 · 千问 3.7 Plus",
            ["Offline"] = "当前为离线陪伴模式",
            ["OpenMusicFolder"] = "歌曲文件夹",
            ["NoTrack"] = "还没有正在播放的歌曲",
            ["NowPlaying"] = "正在播放",
            ["MusicPlaying"] = "本地音乐正在播放",
            ["MusicPaused"] = "播放器已暂停",
            ["MusicStopped"] = "播放器已停止",
            ["CreateCover"] = "天依我想听你唱",
            ["ChooseAudio"] = "选择要翻唱的原曲音频",
            ["DropAudio"] = "点击或拖拽上传歌曲文件",
            ["AudioRequired"] = "请先选择一首原曲音频。",
            ["LyricsNeedsAudio"] = "请上传 WAV、MP3、FLAC、M4A、APE、AAC、OGG 或 AIFF 音频文件。",
            ["Clear"] = "清除",
            ["Cancel"] = "取消",
            ["StartCover"] = "开始",
            ["MidiTaskPreparing"] = "正在准备歌唱任务……",
            ["MidiTaskLaunchingAce"] = "正在连接已打开的 ACE Studio……",
            ["MidiTaskImportingAudio"] = "正在导入原曲音频……",
            ["MidiTaskSeparatingStems"] = "正在分离音轨……",
            ["MidiTaskConvertingVocal"] = "正在转换 MIDI……",
            ["MidiTaskCompleted"] = "ACE 工程已准备完成，正在 ACE Studio 中播放。",
            ["AceAnalyzingTempo"] = "正在分析并应用歌曲曲速……",
            ["SongLanguage"] = "原曲语言",
            ["SongLanguagePlaceholder"] = "请选择原曲语言",
            ["SongLanguageRequired"] = "请选择原曲语言后再开始。",
            ["SongChinese"] = "中文",
            ["SongEnglish"] = "英文",
            ["SongJapanese"] = "日文",
            ["AceSelectingSinger"] = "正在加载洛天依音源……",
            ["AceSavingProject"] = "正在保存 ACE 工程……",
            ["AceInstallRequired"] = "需要安装 ACE Studio 才能使用此功能。",
            ["AceOpenRequired"] = "请先打开 ACE Studio，再点击开始。",
            ["AceConnectionRequired"] = "请在 ACE Studio 中启用“外部代理访问”。",
            ["AceUnsavedProject"] = "请先保存当前 ACE 工程，再点击开始。",
            ["AceProjectChanged"] = "ACE 工程已切换，操作已停止。",
            ["AceVoiceUnavailable"] = "找不到可用的洛天依音源，请检查 ACE 音源。",
            ["AceLanguageUnsupported"] = "洛天依音源不支持所选原曲语言。",
            ["AceInvalidAudio"] = "音频无法读取，请尝试 WAV 文件。",
            ["AceCancelled"] = "任务已停止，工程保留在 ACE Studio 中。",
            ["MidiTaskManualTakeover"] = "操作失败，请在 ACE Studio 中手动完成。",
            ["MidiTaskFailed"] = "操作失败，需要人工接管。",
            ["Language"] = "界面语言",
            ["TopmostMode"] = "桌宠置顶模式",
            ["TopmostWindow"] = "仅窗口任务时置顶",
            ["TopmostAlways"] = "始终置于顶层",
            ["TopmostNever"] = "不置顶",
            ["Chinese"] = "简体中文",
            ["English"] = "English",
            ["Save"] = "保存",
            ["Saved"] = "语言设置已保存。",
            ["DeveloperOptions"] = "开发人员选项",
            ["PluginFolderDetected"] = "检测到 config 文件夹中的开发功能配置，演示版会在此显示可调试项目。",
            ["Close"] = "关闭",
            ["Synthesizing"] = "天依正在生成整句语音……",
            ["PlayingSpeech"] = "正在同步显示并播放语音",
            ["SpeechUnavailable"] = "语音暂不可用",
            ["InputHint"] = "输入消息，Enter 发送，Shift+Enter 换行",
            ["VoiceInputHint"] = "输入想让天依说的话，Enter 生成，Shift+Enter 换行",
            ["VoiceGenerationHint"] = "输入想让天依说的话。生成的语音会存放在Voice文件夹",
            ["GenerateVoice"] = "生成并保存",
            ["OpenVoiceFolder"] = "打开 Voice 文件夹",
            ["VoiceGenerating"] = "天依正在生成语音……",
            ["VoiceSaved"] = "语音已保存：{0}",
            ["VoiceFailed"] = "语音生成失败。"
        };

    private static readonly IReadOnlyDictionary<string, string> EnglishTexts =
        new Dictionary<string, string>
        {
            ["Chat"] = "Chat",
            ["Voice"] = "Voice",
            ["Singing"] = "Sing",
            ["Music"] = "Local music",
            ["Settings"] = "Settings",
            ["Exit"] = "Exit",
            ["ShowPet"] = "Show pet",
            ["HidePet"] = "Hide pet",
            ["Send"] = "Send",
            ["You"] = "You",
            ["Tianyi"] = "Tianyi",
            ["Welcome"] = "Hi! What would you like to talk about today?",
            ["OfflineLabel"] = "Offline preset reply",
            ["Ready"] = "Ready",
            ["Thinking"] = "Tianyi is thinking",
            ["Online"] = "Online · Qwen 3.7 Plus",
            ["Offline"] = "Offline companion mode",
            ["OpenMusicFolder"] = "Music folder",
            ["NoTrack"] = "No track is playing",
            ["NowPlaying"] = "Now playing",
            ["MusicPlaying"] = "Local music is playing",
            ["MusicPaused"] = "Player paused",
            ["MusicStopped"] = "Player stopped",
            ["CreateCover"] = "Tianyi, I want to hear you sing",
            ["ChooseAudio"] = "Choose the source audio",
            ["DropAudio"] = "Click or drop song files here",
            ["AudioRequired"] = "Please choose an original song first.",
            ["LyricsNeedsAudio"] = "Please upload WAV, MP3, FLAC, M4A, APE, AAC, OGG, or AIFF audio.",
            ["Clear"] = "Clear",
            ["Cancel"] = "Cancel",
            ["StartCover"] = "Start",
            ["MidiTaskPreparing"] = "Preparing the singing task...",
            ["MidiTaskLaunchingAce"] = "Connecting to the open ACE Studio...",
            ["MidiTaskImportingAudio"] = "Importing the source audio...",
            ["MidiTaskSeparatingStems"] = "Separating tracks...",
            ["MidiTaskConvertingVocal"] = "Converting to MIDI...",
            ["MidiTaskCompleted"] = "The ACE project is ready and playing in ACE Studio.",
            ["AceAnalyzingTempo"] = "Analyzing and applying the song tempo...",
            ["SongLanguage"] = "Source language",
            ["SongLanguagePlaceholder"] = "Select source language",
            ["SongLanguageRequired"] = "Select the source language before starting.",
            ["SongChinese"] = "Chinese",
            ["SongEnglish"] = "English",
            ["SongJapanese"] = "Japanese",
            ["AceSelectingSinger"] = "Loading Luo Tianyi's voice...",
            ["AceSavingProject"] = "Saving the ACE project...",
            ["AceInstallRequired"] = "ACE Studio must be installed to use this feature.",
            ["AceOpenRequired"] = "Open ACE Studio before clicking Start.",
            ["AceConnectionRequired"] = "Enable External Agent Access in ACE Studio.",
            ["AceUnsavedProject"] = "Save your current ACE project before starting.",
            ["AceProjectChanged"] = "ACE project changed. Automation stopped.",
            ["AceVoiceUnavailable"] = "The Luo Tianyi voice is unavailable. Check the ACE voice library.",
            ["AceLanguageUnsupported"] = "The Luo Tianyi voice does not support the selected source language.",
            ["AceInvalidAudio"] = "Cannot read this audio. Please try a WAV file.",
            ["AceCancelled"] = "Task stopped. The project remains in ACE Studio.",
            ["MidiTaskManualTakeover"] = "Operation failed. Please continue manually in ACE Studio.",
            ["MidiTaskFailed"] = "Operation failed. Manual takeover is required.",
            ["Language"] = "Interface language",
            ["TopmostMode"] = "Pet topmost mode",
            ["TopmostWindow"] = "Topmost for windowed tasks",
            ["TopmostAlways"] = "Always on top",
            ["TopmostNever"] = "Not topmost",
            ["Chinese"] = "简体中文",
            ["English"] = "English",
            ["Save"] = "Save",
            ["Saved"] = "Language preference saved.",
            ["DeveloperOptions"] = "Developer options",
            ["PluginFolderDetected"] = "Developer feature configuration was detected in the config folder. Debug options can be shown here in the demo build.",
            ["Close"] = "Close",
            ["Synthesizing"] = "Generating the complete voice line…",
            ["PlayingSpeech"] = "Showing text and playing speech",
            ["SpeechUnavailable"] = "Speech is unavailable",
            ["InputHint"] = "Enter to send; Shift+Enter for a new line",
            ["VoiceInputHint"] = "Type what you want Tianyi to say. Enter generates; Shift+Enter adds a new line.",
            ["VoiceGenerationHint"] = "Type what you want Tianyi to say. Generated speech is saved in the Voice folder.",
            ["GenerateVoice"] = "Generate and save",
            ["OpenVoiceFolder"] = "Open Voice folder",
            ["VoiceGenerating"] = "Tianyi is generating the voice…",
            ["VoiceSaved"] = "Voice saved: {0}",
            ["VoiceFailed"] = "Voice generation failed."
        };

    public LocalizationService(string language)
    {
        CurrentLanguage = Normalize(language);
    }

    public event EventHandler? LanguageChanged;

    public string CurrentLanguage { get; private set; }

    public string this[string key]
    {
        get
        {
            var texts = CurrentLanguage == English
                ? EnglishTexts
                : ChineseTexts;
            return texts.TryGetValue(key, out var value) ? value : key;
        }
    }

    public void SetLanguage(string language)
    {
        var normalized = Normalize(language);
        if (normalized == CurrentLanguage)
        {
            return;
        }

        CurrentLanguage = normalized;
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public static string Normalize(string? language) =>
        string.Equals(
            language,
            English,
            StringComparison.OrdinalIgnoreCase)
            ? English
            : Chinese;
}
