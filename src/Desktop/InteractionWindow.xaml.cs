using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using VocaLink.Application;
using VocaLink.Application.Abstractions;
using VocaLink.Domain;
using VocaLink.Infrastructure.AutoACE;
using VocaLink.Live2D;

namespace VocaLink.Desktop;

public partial class InteractionWindow : Window
{
    public const string TopmostAlways = "always";
    public const string TopmostWindow = "window";
    public const string TopmostNever = "never";

    private static readonly HashSet<string> AudioExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".wav",
            ".mp3",
            ".flac",
            ".m4a", ".ape", ".aac", ".ogg", ".aif", ".aiff"
        };
    private readonly ConversationController _conversationController;
    private readonly SpeechPlaybackQueue _speechQueue;
    private readonly ITtsService _ttsService;
    private readonly ISingingSynthesisService _singingSynthesisService;
    private readonly IAudioPlaybackService _audioPlaybackService;
    private readonly OfflineMusicPlayer _offlineMusicPlayer;
    private readonly ISettingsStore _settingsStore;
    private readonly LocalizationService _localization;
    private readonly Live2DView _characterView;
    private readonly UserProfile _profile =
        new("朋友", "自动", "喜欢音乐以及温柔、简洁的陪伴式回应");
    private CancellationTokenSource? _expressionReset;
    private CancellationTokenSource? _singingCancellation;
    private CancellationTokenSource? _voiceGenerationCancellation;
    private CancellationTokenSource? _replyRevealCancellation;
    private Task? _replyRevealTask;
    private string? _selectedAudioPath;
    private bool _historyLoaded;
    private bool _isSending;
    private bool _isSinging;
    private bool _isVoiceGenerating;
    private bool _isVoiceGenerationMode;
    private InteractionMode _mode;

    public InteractionWindow(
        ConversationController conversationController,
        SpeechPlaybackQueue speechQueue,
        ITtsService ttsService,
        ISingingSynthesisService singingSynthesisService,
        IAudioPlaybackService audioPlaybackService,
        OfflineMusicPlayer offlineMusicPlayer,
        ISettingsStore settingsStore,
        LocalizationService localization,
        Live2DView characterView)
    {
        _conversationController = conversationController;
        _speechQueue = speechQueue;
        _ttsService = ttsService;
        _singingSynthesisService = singingSynthesisService;
        _audioPlaybackService = audioPlaybackService;
        _offlineMusicPlayer = offlineMusicPlayer;
        _settingsStore = settingsStore;
        _localization = localization;
        _characterView = characterView;

        InitializeComponent();
        DataContext = this;
        _localization.LanguageChanged += Localization_LanguageChanged;
        _speechQueue.StateChanged += SpeechQueue_StateChanged;
        _offlineMusicPlayer.StateChanged += OfflineMusicPlayer_StateChanged;
        Closed += OnClosed;
        ApplyLanguage();
    }

    public ObservableCollection<MessageItem> Messages { get; } = [];

    public event EventHandler<string>? TopmostModeChanged;

    public async void ShowMode(InteractionMode mode)
    {
        if (mode != InteractionMode.Singing)
        {
            CancelSinging();
            CoverPopup.IsOpen = false;
        }

        _mode = mode;
        if (mode == InteractionMode.Chat)
        {
            // 每次从桌宠重新打开聊天时，默认回到普通聊天页。
            _isVoiceGenerationMode = false;
        }

        CancelExpressionReset();
        ChatPanel.Visibility = mode == InteractionMode.Chat
            ? Visibility.Visible
            : Visibility.Collapsed;
        SingingPanel.Visibility = mode == InteractionMode.Singing
            ? Visibility.Visible
            : Visibility.Collapsed;
        SettingsPanel.Visibility = mode == InteractionMode.Settings
            ? Visibility.Visible
            : Visibility.Collapsed;
        StatusText.Visibility = mode == InteractionMode.Singing
            ? Visibility.Collapsed
            : Visibility.Visible;
        Width = mode switch
        {
            InteractionMode.Chat => 338,
            InteractionMode.Singing => 342,
            _ => 300
        };
        Height = mode switch
        {
            InteractionMode.Chat => 380,
            InteractionMode.Singing => 170,
            _ => 300
        };

        await _characterView.SetVisualModeAsync(mode switch
        {
            InteractionMode.Chat => "chat",
            InteractionMode.Singing => "singing",
            _ => "normal"
        });

        ApplyLanguage();
        if (mode == InteractionMode.Chat)
        {
            await EnsureHistoryLoadedAsync();
            UpdateChatSubmodeUi();
            MessageInput.Focus();
        }
        else if (mode == InteractionMode.Settings)
        {
            LanguageCombo.SelectedValue = _localization.CurrentLanguage;
            TopmostModeCombo.SelectedValue =
                await LoadTopmostModeAsync();
        }
        else if (mode == InteractionMode.Singing)
        {
            UpdateMusicDisplay();
        }
    }

    private async Task EnsureHistoryLoadedAsync()
    {
        if (_historyLoaded)
        {
            return;
        }

        _historyLoaded = true;
        var history = await _conversationController.LoadRecentAsync();
        foreach (var message in history)
        {
            Messages.Add(MessageItem.FromDomain(message, _localization));
        }

        if (Messages.Count == 0)
        {
            Messages.Add(new MessageItem(
                _localization["Tianyi"],
                _localization["Welcome"],
                false,
                false,
                _localization["OfflineLabel"]));
        }

        ScrollToLatest();
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e) =>
        await SendCurrentMessageAsync();

    private void ChatModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mode != InteractionMode.Chat || _isSending || _isVoiceGenerating)
        {
            return;
        }

        _isVoiceGenerationMode = !_isVoiceGenerationMode;
        UpdateChatSubmodeUi();
        if (_isVoiceGenerationMode)
        {
            VoiceInput.Focus();
        }
        else
        {
            MessageInput.Focus();
        }
    }

    private async void MessageInput_PreviewKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        var isEnter = e.Key == Key.Enter || e.Key == Key.Return;
        var wantsNewLine =
            (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (isEnter && !wantsNewLine)
        {
            e.Handled = true;
            await SendCurrentMessageAsync();
        }
    }

    private async void VoiceInput_PreviewKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        var isEnter = e.Key == Key.Enter || e.Key == Key.Return;
        var wantsNewLine =
            (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (isEnter && !wantsNewLine)
        {
            e.Handled = true;
            await GenerateVoiceAsync();
        }
    }

    private async Task SendCurrentMessageAsync()
    {
        var text = MessageInput.Text.Trim();
        if (_isSending || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _isSending = true;
        SendButton.IsEnabled = false;
        MessageInput.IsEnabled = false;
        MessageInput.Clear();
        Messages.Add(new MessageItem(
            _localization["You"],
            text,
            true,
            false,
            _localization["OfflineLabel"]));
        var thinkingItem = new MessageItem(
            _localization["Tianyi"],
            _localization["Thinking"],
            false,
            false,
            _localization["OfflineLabel"]);
        Messages.Add(thinkingItem);
        ScrollToLatest();
        StatusText.Text = _localization["Thinking"];

        try
        {
            await _characterView.SetExpressionAsync("ease");
            var reply = await _conversationController.SendAsync(
                text,
                _profile);
            thinkingItem.IsOffline = reply.IsOffline;

            _replyRevealCancellation?.Cancel();
            _replyRevealCancellation?.Dispose();
            _replyRevealCancellation = new CancellationTokenSource();
            var revealToken = _replyRevealCancellation.Token;
            var revealStarted = false;

            try
            {
                await _speechQueue.SpeakAsync(
                    reply.Text,
                    DetectLanguage(reply.Text),
                    (audioPath, _) => Dispatcher.InvokeAsync(() =>
                    {
                        thinkingItem.AudioPath = audioPath;
                        thinkingItem.Text = string.Empty;
                        _replyRevealTask = RevealMessageAsync(
                            thinkingItem,
                            reply.Text,
                            revealToken);
                        revealStarted = true;
                    }).Task,
                    revealToken);
            }
            catch (Exception exception)
                when (exception is not OperationCanceledException)
            {
                if (!revealStarted)
                {
                    thinkingItem.Text = reply.Text;
                }

                StatusText.Text =
                    $"{_localization["SpeechUnavailable"]}: " +
                    exception.Message;
            }

            if (_replyRevealTask is not null)
            {
                await _replyRevealTask;
            }

            StatusText.Text = reply.IsOffline
                ? _localization["Offline"]
                : _localization["Online"];
            await ShowTemporaryExpressionAsync(
                reply.IsOffline ? "normal" : "like");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = _localization["Ready"];
        }
        catch (Exception exception)
        {
            thinkingItem.Text = exception.Message;
            StatusText.Text = exception.Message;
            await ShowTemporaryExpressionAsync("sad");
        }
        finally
        {
            _isSending = false;
            SendButton.IsEnabled = true;
            MessageInput.IsEnabled = true;
            MessageInput.Focus();
            ScrollToLatest();
        }
    }

    private async void GenerateVoiceButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await GenerateVoiceAsync();

    /// <summary>
    /// 纯本地语音生成入口：不调用大语言模型，也不写入对话历史。
    /// </summary>
    private async Task GenerateVoiceAsync()
    {
        var text = VoiceInput.Text.Trim();
        if (_isVoiceGenerating || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _isVoiceGenerating = true;
        GenerateVoiceButton.IsEnabled = false;
        VoiceInput.IsEnabled = false;
        _voiceGenerationCancellation?.Cancel();
        _voiceGenerationCancellation?.Dispose();
        _voiceGenerationCancellation = new CancellationTokenSource();
        var cancellation = _voiceGenerationCancellation;
        StatusText.Text = _localization["VoiceGenerating"];

        try
        {
            await _characterView.SetExpressionAsync("ease");
            var temporaryAudioPath = await _ttsService.SynthesizeAsync(
                text,
                DetectLanguage(text),
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();

            Directory.CreateDirectory(AppPaths.VoiceDirectory);
            var outputPath = Path.Combine(
                AppPaths.VoiceDirectory,
                $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.wav");
            File.Move(temporaryAudioPath, outputPath, overwrite: false);

            VoiceInput.Clear();
            StatusText.Text = string.Format(
                _localization["VoiceSaved"],
                Path.GetFileName(outputPath));
            await ShowTemporaryExpressionAsync("like");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = _localization["Ready"];
        }
        catch
        {
            StatusText.Text = _localization["VoiceFailed"];
            await ShowTemporaryExpressionAsync("sad");
        }
        finally
        {
            _isVoiceGenerating = false;
            GenerateVoiceButton.IsEnabled = true;
            VoiceInput.IsEnabled = true;
            VoiceInput.Focus();
            if (ReferenceEquals(_voiceGenerationCancellation, cancellation))
            {
                _voiceGenerationCancellation.Dispose();
                _voiceGenerationCancellation = null;
            }
        }
    }

    private void OpenVoiceFolderButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.VoiceDirectory);
        Process.Start(new ProcessStartInfo(AppPaths.VoiceDirectory)
        {
            UseShellExecute = true
        });
    }

    private async void ReplayMessageButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MessageItem item } ||
            string.IsNullOrWhiteSpace(item.AudioPath) ||
            !File.Exists(item.AudioPath))
        {
            StatusText.Text = _localization["SpeechUnavailable"];
            return;
        }

        try
        {
            StatusText.Text = _localization["PlayingSpeech"];
            await _audioPlaybackService.PlayAsync(item.AudioPath);
            StatusText.Text = item.IsOffline
                ? _localization["Offline"]
                : _localization["Online"];
        }
        catch (Exception exception)
        {
            StatusText.Text =
                $"{_localization["SpeechUnavailable"]}: {exception.Message}";
        }
    }

    private async Task RevealMessageAsync(
        MessageItem item,
        string fullText,
        CancellationToken cancellationToken)
    {
        var chunkSize = Math.Max(1, fullText.Length / 100);
        for (var index = 0; index < fullText.Length; index += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = Math.Min(chunkSize, fullText.Length - index);
            item.Text += fullText.Substring(index, length);
            ScrollToLatest();
            await Task.Delay(38, cancellationToken);
        }
    }

    private void ShowUploadButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        CoverPopup.IsOpen = !CoverPopup.IsOpen;
    }

    private void AudioDropZone_MouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (FindVisualParent<System.Windows.Controls.Button>(
                e.OriginalSource as DependencyObject)
            is not null)
        {
            return;
        }

        ChooseSongFiles();
    }

    private void AudioDropZone_DragEnter(
        object sender,
        System.Windows.DragEventArgs e)
    {
        e.Effects = CanAcceptDroppedFiles(e.Data)
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void AudioDropZone_Drop(
        object sender,
        System.Windows.DragEventArgs e)
    {
        if (TryGetDroppedFiles(e.Data, out var files))
        {
            ApplySelectedFiles(files);
        }

        e.Handled = true;
    }

    private void ChooseSongFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = _localization["ChooseAudio"],
            Filter = "Audio|*.wav;*.mp3;*.flac;*.m4a;*.ape;*.aac;*.ogg;*.aif;*.aiff",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            ApplySelectedFiles(dialog.FileNames);
        }
    }

    private void ClearUploadButton_Click(
        object sender,
        RoutedEventArgs e) =>
        ClearUploadSelection();

    private async void SubmitCoverButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_isSinging)
        {
            return;
        }

        var sourceLanguage = SongLanguageCombo.SelectedValue as string;
        if (string.IsNullOrWhiteSpace(sourceLanguage))
        {
            System.Windows.MessageBox.Show(
                _localization["SongLanguageRequired"],
                "VocaLink");
            SongLanguageCombo.Focus();
            SongLanguageCombo.IsDropDownOpen = true;
            return;
        }

        if (_selectedAudioPath is null ||
            !File.Exists(_selectedAudioPath))
        {
            System.Windows.MessageBox.Show(
                _localization["AudioRequired"],
                "VocaLink");
            return;
        }

        _offlineMusicPlayer.Stop();
        _audioPlaybackService.Stop();
        _isSinging = true;
        _singingCancellation = new CancellationTokenSource();
        var taskCancellation = _singingCancellation;
        SetSingingControlsEnabled(false);
        SingingProgressBar.Visibility = Visibility.Visible;
        var progress = new Progress<SingingProgress>(value =>
        {
            if (!ReferenceEquals(_singingCancellation, taskCancellation) ||
                taskCancellation.IsCancellationRequested) return;
            var localizedMessage = LocalizeSingingProgress(value);
            SingingProgressText.Text = localizedMessage;
            StatusText.Text = localizedMessage;
        });

        try
        {
            var projectPath = await _singingSynthesisService.CreateCoverAsync(
                new SingingRequest(
                    _selectedAudioPath,
                    Language: sourceLanguage),
                progress,
                taskCancellation.Token);
            if (projectPath is null)
            {
                StatusText.Text = _localization["MidiTaskFailed"];
                return;
            }

            SingingProgressText.Text = _localization["MidiTaskCompleted"];
            StatusText.Text = _localization["MidiTaskCompleted"];
            await _characterView.SetVisualModeAsync("singing");
            ClearUploadSelection();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = _localization["AceCancelled"];
            SingingProgressText.Text = StatusText.Text;
        }
        catch (Exception exception)
        {
            var message = LocalizeSingingException(exception);
            StatusText.Text = message;
            SingingProgressText.Text = message;
        }
        finally
        {
            _isSinging = false;
            SingingProgressBar.Visibility = Visibility.Collapsed;
            SetSingingControlsEnabled(true);
            if (ReferenceEquals(
                    _singingCancellation,
                    taskCancellation))
            {
                _singingCancellation.Dispose();
                _singingCancellation = null;
            }
        }
    }

    private void PreviousTrackButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _offlineMusicPlayer.PlayPrevious();
        UpdateMusicDisplay();
    }

    private async void PlayPauseButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_offlineMusicPlayer.IsPlaying)
        {
            _offlineMusicPlayer.Pause();
            await _characterView.SetVisualModeAsync("singing");
        }
        else if (_offlineMusicPlayer.IsPaused)
        {
            _offlineMusicPlayer.Resume();
            await _characterView.SetVisualModeAsync("singing");
        }
        else if (!_offlineMusicPlayer.Start())
        {
            OpenMusicFolder();
        }
        else
        {
            await _characterView.SetVisualModeAsync("singing");
        }

        UpdateMusicDisplay();
    }

    private void NextTrackButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!_offlineMusicPlayer.IsPlaying)
        {
            _offlineMusicPlayer.Start();
        }
        else
        {
            _offlineMusicPlayer.PlayNext();
        }

        UpdateMusicDisplay();
    }

    private void OpenMusicFolderButton_Click(
        object sender,
        RoutedEventArgs e) =>
        OpenMusicFolder();

    private void OpenMusicFolder()
    {
        Directory.CreateDirectory(_offlineMusicPlayer.MusicDirectory);
        Process.Start(
            new ProcessStartInfo(_offlineMusicPlayer.MusicDirectory)
            {
                UseShellExecute = true
            });
    }

    private async void SaveSettingsButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var language = LanguageCombo.SelectedValue as string
                       ?? LocalizationService.Chinese;
        var topmostMode = NormalizeTopmostMode(
            TopmostModeCombo.SelectedValue as string);
        await _settingsStore.SetAsync("UiLanguage", language);
        await _settingsStore.SetAsync("TopmostMode", topmostMode);
        _localization.SetLanguage(language);
        TopmostModeChanged?.Invoke(this, topmostMode);
        StatusText.Text = _localization["Saved"];
    }

    private void SpeechQueue_StateChanged(
        object? sender,
        SpeechQueueStateChanged state)
    {
        Dispatcher.InvokeAsync(() =>
        {
            StatusText.Text = state.State switch
            {
                SpeechQueueState.Failed =>
                    $"{_localization["SpeechUnavailable"]}: {state.Message}",
                _ => _localization["Thinking"]
            };
        });
    }

    private void OfflineMusicPlayer_StateChanged(
        object? sender,
        OfflineMusicStateChanged state)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            UpdateMusicDisplay();
            if (_mode == InteractionMode.Singing)
            {
                await _characterView.SetVisualModeAsync("singing");
                if (state.State is OfflineMusicState.Stopped or
                    OfflineMusicState.Failed)
                {
                    await _characterView.ResetPoseAsync();
                }
            }
        });
    }

    private void Localization_LanguageChanged(object? sender, EventArgs e)
    {
        foreach (var item in Messages)
        {
            item.Speaker = item.IsUser
                ? _localization["You"]
                : _localization["Tianyi"];
            item.OfflineLabel = _localization["OfflineLabel"];
        }

        ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        TitleText.Text = _mode switch
        {
            InteractionMode.Singing => _localization["Singing"],
            InteractionMode.Settings => _localization["Settings"],
            _ => _localization["Chat"]
        };
        TitleText.Visibility = _mode == InteractionMode.Chat
            ? Visibility.Collapsed
            : Visibility.Visible;
        ChatModeButton.Visibility = _mode == InteractionMode.Chat
            ? Visibility.Visible
            : Visibility.Collapsed;
        ChatModeButton.Content = _isVoiceGenerationMode
            ? _localization["Voice"]
            : _localization["Chat"];
        SendButton.Content = _localization["Send"];
        MessageInput.ToolTip = _localization["InputHint"];
        VoiceInput.ToolTip = _localization["VoiceInputHint"];
        VoiceGenerationDescription.Text = _localization["VoiceGenerationHint"];
        GenerateVoiceButton.Content = _localization["GenerateVoice"];
        OpenVoiceFolderButton.ToolTip = _localization["OpenVoiceFolder"];
        OpenMusicFolderButton.ToolTip = _localization["OpenMusicFolder"];
        ShowUploadButton.Content = _localization["CreateCover"];
        ClearUploadButton.ToolTip = _localization["Clear"];
        SubmitCoverButton.Content = _localization["StartCover"];
        SongLanguagePlaceholder.Content = _localization["SongLanguagePlaceholder"];
        SongChineseOption.Content = _localization["SongChinese"];
        SongEnglishOption.Content = _localization["SongEnglish"];
        SongJapaneseOption.Content = _localization["SongJapanese"];
        LanguageLabel.Text = _localization["Language"];
        TopmostModeLabel.Text = _localization["TopmostMode"];
        TopmostWindowOption.Content = _localization["TopmostWindow"];
        TopmostAlwaysOption.Content = _localization["TopmostAlways"];
        TopmostNeverOption.Content = _localization["TopmostNever"];
        ChineseOption.Content = _localization["Chinese"];
        EnglishOption.Content = _localization["English"];
        SaveSettingsButton.Content = _localization["Save"];
        DeveloperOptionsTitle.Text = _localization["DeveloperOptions"];
        DeveloperOptionsDescription.Text = _localization["PluginFolderDetected"];
        DeveloperOptionsPanel.Visibility = HasDeveloperPlugins()
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateChatSubmodeUi();
        UpdateUploadLabels();
        UpdateMusicDisplay();
        StatusText.Text = _localization["Ready"];
    }

    private void UpdateChatSubmodeUi()
    {
        var showVoiceGeneration =
            _mode == InteractionMode.Chat && _isVoiceGenerationMode;
        VoiceGenerationPanel.Visibility = showVoiceGeneration
            ? Visibility.Visible
            : Visibility.Collapsed;
        MessageList.Visibility = showVoiceGeneration
            ? Visibility.Collapsed
            : Visibility.Visible;
        ChatInputPanel.Visibility = showVoiceGeneration
            ? Visibility.Collapsed
            : Visibility.Visible;
        ChatModeButton.Content = showVoiceGeneration
            ? _localization["Voice"]
            : _localization["Chat"];
    }

    private string LocalizeSingingProgress(SingingProgress progress) =>
        progress.Stage switch
        {
            SingingStage.Preparing => _localization["MidiTaskPreparing"],
            SingingStage.LaunchingAce => _localization["MidiTaskLaunchingAce"],
            SingingStage.ImportingAudio => _localization["MidiTaskImportingAudio"],
            SingingStage.AnalyzingTempo => _localization["AceAnalyzingTempo"],
            SingingStage.SeparatingStems => _localization["MidiTaskSeparatingStems"],
            SingingStage.ConvertingVocal => _localization["MidiTaskConvertingVocal"],
            SingingStage.SelectingSinger => _localization["AceSelectingSinger"],
            SingingStage.SavingProject => _localization["AceSavingProject"],
            SingingStage.Completed => _localization["MidiTaskCompleted"],
            SingingStage.ManualTakeover => _localization["MidiTaskManualTakeover"],
            _ => progress.Message
        };

    private string LocalizeSingingException(Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            return _localization["Ready"];
        }

        return exception is AceCliException ace ? _localization[ace.Code switch
        {
            "ACE_NOT_INSTALLED" => "AceInstallRequired",
            "ACE_NOT_RUNNING" => "AceOpenRequired",
            "ACE_ACCESS_REQUIRED" => "AceConnectionRequired",
            "ACE_UNAVAILABLE" => "AceConnectionRequired",
            "UNSAVED_CHANGES" => "AceUnsavedProject",
            "PROJECT_CHANGED" => "AceProjectChanged",
            "VOICE_UNAVAILABLE" => "AceVoiceUnavailable",
            "VOICE_LANGUAGE" => "AceLanguageUnsupported",
            "AUDIO_INVALID" => "AceInvalidAudio",
            _ => "MidiTaskFailed"
        }] : _localization["MidiTaskFailed"];
    }

    private static bool HasDeveloperPlugins()
    {
        if (!Directory.Exists(AppPaths.ConfigDirectory))
        {
            return false;
        }

        return Directory.EnumerateFiles(AppPaths.ConfigDirectory)
            .Any(path =>
            {
                var name = Path.GetFileName(path);
                return name.EndsWith(
                           ".feature.json",
                           StringComparison.OrdinalIgnoreCase) ||
                       name.EndsWith(
                           ".plugin.json",
                           StringComparison.OrdinalIgnoreCase) ||
                       name.StartsWith(
                           "developer.",
                           StringComparison.OrdinalIgnoreCase);
            });
    }

    private async Task<string> LoadTopmostModeAsync()
    {
        var savedMode = await _settingsStore.GetAsync("TopmostMode");
        return NormalizeTopmostMode(savedMode);
    }

    public static string NormalizeTopmostMode(string? mode) =>
        string.Equals(mode, TopmostAlways, StringComparison.OrdinalIgnoreCase)
            ? TopmostAlways
            : string.Equals(mode, TopmostNever, StringComparison.OrdinalIgnoreCase)
                ? TopmostNever
                : TopmostWindow;

    private void TitleBar_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private async void CloseButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_mode == InteractionMode.Singing && _isSinging)
        {
            CancelSinging();
        }

        CancelVoiceGeneration();

        CoverPopup.IsOpen = false;
        CancelExpressionReset();
        if (_mode != InteractionMode.Singing ||
            !_offlineMusicPlayer.IsPlaying)
        {
            await _characterView.SetVisualModeAsync("normal");
            await _characterView.ResetPoseAsync();
        }

        Hide();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        CancelSinging();
        CancelVoiceGeneration();
        CancelExpressionReset();
        _replyRevealCancellation?.Cancel();
        _replyRevealCancellation?.Dispose();
        if (!_offlineMusicPlayer.IsPlaying)
        {
            _ = _characterView.SetVisualModeAsync("normal");
            _ = _characterView.ResetPoseAsync();
        }

        _localization.LanguageChanged -= Localization_LanguageChanged;
        _speechQueue.StateChanged -= SpeechQueue_StateChanged;
        _offlineMusicPlayer.StateChanged -= OfflineMusicPlayer_StateChanged;
    }

    private void ScrollToLatest()
    {
        if (Messages.Count > 0)
        {
            MessageList.ScrollIntoView(Messages[^1]);
        }
    }

    private static string DetectLanguage(string text) =>
        text.Any(character => character is >= '\u4e00' and <= '\u9fff')
            ? "zh"
            : "en";

    private async Task ShowTemporaryExpressionAsync(
        string expression,
        int durationMilliseconds = 1800)
    {
        CancelExpressionReset();
        _expressionReset = new CancellationTokenSource();
        var token = _expressionReset.Token;
        await _characterView.SetExpressionAsync(expression);
        _ = RestoreDefaultExpressionAfterDelayAsync(
            durationMilliseconds,
            token);
    }

    private async Task RestoreDefaultExpressionAfterDelayAsync(
        int durationMilliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(durationMilliseconds, cancellationToken);
            await _characterView.ResetPoseAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CancelExpressionReset()
    {
        _expressionReset?.Cancel();
        _expressionReset?.Dispose();
        _expressionReset = null;
    }

    private void CancelSinging()
    {
        _singingCancellation?.Cancel();
        _audioPlaybackService.Stop();
    }

    private void CancelVoiceGeneration()
    {
        _voiceGenerationCancellation?.Cancel();
    }

    private void SetSingingControlsEnabled(bool enabled)
    {
        ShowUploadButton.IsEnabled = enabled;
        SubmitCoverButton.IsEnabled = enabled;
        AudioDropZone.IsEnabled = enabled;
        ClearUploadButton.IsEnabled = enabled;
        SongLanguageCombo.IsEnabled = enabled;
        PreviousTrackButton.IsEnabled = enabled;
        NextTrackButton.IsEnabled = enabled;
        PlayPauseButton.IsEnabled = enabled;
    }

    private void ApplySelectedFiles(IEnumerable<string> paths)
    {
        var files = paths
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var audioPath = files.FirstOrDefault(path =>
            AudioExtensions.Contains(Path.GetExtension(path)));

        if (audioPath is null)
        {
            System.Windows.MessageBox.Show(
                _localization["LyricsNeedsAudio"],
                "VocaLink");
            return;
        }

        if (audioPath is not null)
        {
            _selectedAudioPath = audioPath;
        }

        UpdateUploadLabels();
        SubmitCoverButton.IsEnabled = !_isSinging;
    }

    private void ClearUploadSelection()
    {
        _selectedAudioPath = null;
        SongLanguageCombo.SelectedIndex = 0;
        UpdateUploadLabels();
    }

    private void UpdateUploadLabels()
    {
        SelectedAudioText.Text = _selectedAudioPath is null
            ? _localization["DropAudio"]
            : Path.GetFileName(_selectedAudioPath);
        SelectedScoreText.Text = string.Empty;
        ClearUploadButton.Visibility = _selectedAudioPath is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private bool CanAcceptDroppedFiles(
        System.Windows.IDataObject data)
    {
        if (!TryGetDroppedFiles(data, out var files))
        {
            return false;
        }

        var hasAudio = files.Any(path =>
            AudioExtensions.Contains(Path.GetExtension(path)));
        return hasAudio;
    }

    private static bool TryGetDroppedFiles(
        System.Windows.IDataObject data,
        out string[] files)
    {
        files = [];
        if (!data.GetDataPresent(System.Windows.DataFormats.FileDrop) ||
            data.GetData(System.Windows.DataFormats.FileDrop)
            is not string[] droppedFiles)
        {
            return false;
        }

        files = droppedFiles.Where(File.Exists).ToArray();
        return files.Length > 0;
    }

    private static T? FindVisualParent<T>(DependencyObject? child)
        where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match)
            {
                return match;
            }

            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }

    private void UpdateMusicDisplay()
    {
        CurrentTrackText.Text = _offlineMusicPlayer.CurrentTrack is null
            ? _localization["NoTrack"]
            : Path.GetFileNameWithoutExtension(
                _offlineMusicPlayer.CurrentTrack);
        PlayPauseButton.Content = _offlineMusicPlayer.IsPlaying ? "⏸" : "▶";
        if (_mode == InteractionMode.Singing && !_isSinging)
        {
            StatusText.Text = _offlineMusicPlayer.IsPlaying
                ? _localization["MusicPlaying"]
                : _offlineMusicPlayer.IsPaused
                    ? _localization["MusicPaused"]
                : _localization["MusicStopped"];
        }
    }

    public sealed class MessageItem : INotifyPropertyChanged
    {
        private string _speaker;
        private string _text;
        private bool _isOffline;
        private string _offlineLabel;
        private string? _audioPath;

        public MessageItem(
            string speaker,
            string text,
            bool isUser,
            bool isOffline,
            string offlineLabel)
        {
            _speaker = speaker;
            _text = text;
            IsUser = isUser;
            _isOffline = isOffline;
            _offlineLabel = offlineLabel;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Speaker
        {
            get => _speaker;
            set => SetField(ref _speaker, value);
        }

        public string Text
        {
            get => _text;
            set => SetField(ref _text, value);
        }

        public bool IsUser { get; }

        public string? AudioPath
        {
            get => _audioPath;
            set
            {
                if (EqualityComparer<string?>.Default.Equals(_audioPath, value))
                {
                    return;
                }

                _audioPath = value;
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(AudioPath)));
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(HasAudio)));
            }
        }

        public bool HasAudio =>
            !IsUser &&
            !string.IsNullOrWhiteSpace(AudioPath) &&
            File.Exists(AudioPath);

        public bool IsOffline
        {
            get => _isOffline;
            set => SetField(ref _isOffline, value);
        }

        public string OfflineLabel
        {
            get => _offlineLabel;
            set => SetField(ref _offlineLabel, value);
        }

        public static MessageItem FromDomain(
            ConversationMessage message,
            LocalizationService localization) =>
            new(
                message.Role == ConversationRole.User
                    ? localization["You"]
                    : localization["Tianyi"],
                message.Content,
                message.Role == ConversationRole.User,
                message.IsOffline,
                localization["OfflineLabel"]);

        private void SetField<T>(
            ref T field,
            T value,
            [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(propertyName));
        }
    }
}
