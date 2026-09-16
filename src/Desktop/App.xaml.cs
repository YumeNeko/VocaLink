using System.IO;
using System.Windows;
using VocaLink.Application;
using VocaLink.Infrastructure.Audio;
using VocaLink.Infrastructure.Persistence;
using VocaLink.Infrastructure.Qwen;
using VocaLink.Infrastructure.AutoACE;
using VocaLink.Infrastructure.Tts;

namespace VocaLink.Desktop;

public partial class App : System.Windows.Application
{
    private static string StartupLogPath = string.Empty;

    private SpeechPlaybackQueue? _speechQueue;
    private GptSovitsTtsService? _ttsService;
    private NAudioPlaybackService? _audioPlaybackService;
    private OfflineMusicPlayer? _offlineMusicPlayer;
    private int _fullExitRequested;

    /// <summary>执行完整退出；若第三方音频调用阻塞，最后由看门线程结束当前进程。</summary>
    public void RequestFullExit()
    {
        if (Interlocked.Exchange(ref _fullExitRequested, 1) != 0) return;

        // 先启动退出兜底；即使第三方音频释放发生阻塞，用户点击退出后进程也不会长期残留。
        var exitWatchdog = new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(1500));
            Environment.Exit(0);
        })
        {
            IsBackground = true,
            Name = "VocaLink Exit Watchdog"
        };
        exitWatchdog.Start();

        // 明确终止自有 Python 侧车；Windows 作业对象也会在主进程结束时兜底清理。
        try { _ttsService?.Dispose(); }
        catch (Exception exception) { WriteStartupLog($"提前关闭语音侧车失败：{exception.Message}"); }
        Shutdown();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppPaths.Initialize();
        StartupLogPath = AppPaths.StartupLogPath;
        DispatcherUnhandledException += (_, args) =>
        {
            WriteStartupLog($"未处理的界面异常：{args.Exception}");
        };
        WriteStartupLog("开始启动。");

        try
        {
            var repository = new SqliteConversationRepository(
                AppPaths.DatabasePath);
            var settingsStore = new SqliteSettingsStore(
                AppPaths.DatabasePath);
            var qwenOptions = QwenOptions.Load(
                Path.Combine(AppPaths.ConfigDirectory, "qwen.settings.json"));
            var llmService = new QwenLlmService(qwenOptions);
            var controller = new ConversationController(
                repository,
                llmService,
                new OfflineResponseService());

            await controller.InitializeAsync();
            WriteStartupLog("对话数据库初始化完成。");
            await settingsStore.InitializeAsync();
            WriteStartupLog("设置数据库初始化完成。");
            var savedLanguage = await settingsStore.GetAsync("UiLanguage");
            var defaultLanguage = savedLanguage
                                  ?? Environment.GetEnvironmentVariable(
                                      "VOCALINK_DEFAULT_LANGUAGE")
                                  ?? LocalizationService.Chinese;
            var localization = new LocalizationService(defaultLanguage);
            var ttsOptions = TtsOptions.Load(
                Path.Combine(AppPaths.ConfigDirectory, "TTS.settings.json"),
                AppPaths.RootDirectory);
            var isDevelopmentWorkspace = File.Exists(
                Path.Combine(AppPaths.RootDirectory, "VocaLink.sln"));
            var sidecarScript = Path.Combine(
                AppPaths.RootDirectory,
                isDevelopmentWorkspace
                    ? Path.Combine("src", "TTS", "server.py")
                    : Path.Combine("TTS", "server.py"));
            if (!File.Exists(sidecarScript))
                throw new FileNotFoundException($"找不到运行文件：{sidecarScript}");
            _ttsService = new GptSovitsTtsService(
                ttsOptions.PythonPath,
                sidecarScript,
                AppPaths.RootDirectory);
            _audioPlaybackService = new NAudioPlaybackService();
            _offlineMusicPlayer = new OfflineMusicPlayer(
                _audioPlaybackService,
                AppPaths.MusicDirectory);
            _speechQueue = new SpeechPlaybackQueue(
                _ttsService,
                _audioPlaybackService);
            WriteStartupLog("语音服务初始化完成。");
            _ = Task.Run(async () =>
            {
                try
                {
                    await _ttsService.WarmUpAsync();
                }
                catch
                {
                    // 预热失败不阻塞主界面，首次合成会再次尝试并显示错误。
                }
            });

            var singingService = new AceCliSynthesisService(
                AppPaths.SingingProjectsDirectory,
                AceCliOptions.Load(Path.Combine(AppPaths.ConfigDirectory, "ace.settings.json")));

            var mainWindow = new MainWindow(
                controller,
                _speechQueue,
                _ttsService,
                singingService,
                _audioPlaybackService,
                _offlineMusicPlayer,
                settingsStore,
                localization);
            MainWindow = mainWindow;
            mainWindow.Show();
            WriteStartupLog("主窗口已显示。");
        }
        catch (Exception exception)
        {
            WriteStartupLog($"启动失败：{exception}");
            System.Windows.MessageBox.Show(
                $"VocaLink 启动失败：{exception.Message}",
                "VocaLink",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _speechQueue?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        catch (Exception exception) { WriteStartupLog($"关闭语音队列失败：{exception.Message}"); }
        try { _offlineMusicPlayer?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        catch (Exception exception) { WriteStartupLog($"关闭播放器失败：{exception.Message}"); }
        try { _ttsService?.Dispose(); }
        catch (Exception exception) { WriteStartupLog($"关闭语音侧车失败：{exception.Message}"); }
        try { _audioPlaybackService?.Dispose(); }
        catch (Exception exception) { WriteStartupLog($"关闭音频服务失败：{exception.Message}"); }
        base.OnExit(e);
    }

    private static void WriteStartupLog(string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(StartupLogPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(
                StartupLogPath,
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // 启动日志失败不能阻止主程序运行。
        }
    }
}
