using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using VocaLink.Application;
using VocaLink.Application.Abstractions;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace VocaLink.Desktop;

/// <summary>
/// 常驻桌面的纯角色窗口，功能界面通过右键角色按需打开。
/// </summary>
public partial class MainWindow : Window
{
    private readonly ConversationController _conversationController;
    private readonly SpeechPlaybackQueue _speechQueue;
    private readonly ITtsService _ttsService;
    private readonly ISingingSynthesisService _singingSynthesisService;
    private readonly IAudioPlaybackService _audioPlaybackService;
    private readonly OfflineMusicPlayer _offlineMusicPlayer;
    private readonly ISettingsStore _settingsStore;
    private readonly LocalizationService _localization;
    private readonly DispatcherTimer _mouthTimer;
    private readonly DispatcherTimer _topmostTimer;
    private WinForms.NotifyIcon? _notifyIcon;
    private Drawing.Icon? _trayIcon;
    private WinForms.ToolStripMenuItem? _showPetTrayItem;
    private WinForms.ToolStripMenuItem? _chatTrayItem;
    private WinForms.ToolStripMenuItem? _singingTrayItem;
    private WinForms.ToolStripMenuItem? _settingsTrayItem;
    private WinForms.ToolStripMenuItem? _exitTrayItem;
    private CancellationTokenSource? _touchReset;
    private InteractionWindow? _interactionWindow;
    private System.Windows.Point _dragStartCursor;
    private System.Windows.Point _dragStartWindow;
    private bool _isPointerPressed;
    private bool _isDragging;
    private DateTime _lastFocusUpdateUtc;
    private bool _mouthOpen;
    private bool _isExiting;
    private InteractionMode _activeInteractionMode = InteractionMode.Chat;
    private string _topmostMode = InteractionWindow.TopmostWindow;

    public MainWindow(
        ConversationController conversationController,
        SpeechPlaybackQueue speechQueue,
        ITtsService ttsService,
        ISingingSynthesisService singingSynthesisService,
        IAudioPlaybackService audioPlaybackService,
        OfflineMusicPlayer offlineMusicPlayer,
        ISettingsStore settingsStore,
        LocalizationService localization)
    {
        _conversationController = conversationController;
        _speechQueue = speechQueue;
        _ttsService = ttsService;
        _singingSynthesisService = singingSynthesisService;
        _audioPlaybackService = audioPlaybackService;
        _offlineMusicPlayer = offlineMusicPlayer;
        _settingsStore = settingsStore;
        _localization = localization;

        InitializeComponent();
        InitializeTrayIcon();
        ApplyLanguage();
        _localization.LanguageChanged += Localization_LanguageChanged;
        _speechQueue.StateChanged += SpeechQueue_StateChanged;
        _offlineMusicPlayer.StateChanged += OfflineMusicPlayer_StateChanged;

        _mouthTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(90)
        };
        _mouthTimer.Tick += async (_, _) =>
        {
            _mouthOpen = !_mouthOpen;
            await CharacterView.SetMouthOpenAsync(_mouthOpen ? 0.72 : 0.12);
        };

        _topmostTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(800)
        };
        _topmostTimer.Tick += (_, _) => ApplyTopmostPolicy();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var savedLeft = await _settingsStore.GetAsync("PetLeft");
        var savedTop = await _settingsStore.GetAsync("PetTop");
        if (double.TryParse(
                savedLeft,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var left) &&
            double.TryParse(
                savedTop,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var top) &&
            IsVisiblePosition(left, top))
        {
            Left = left;
            Top = top;
        }
        else
        {
            Left = SystemParameters.WorkArea.Left + 18;
            Top = SystemParameters.WorkArea.Bottom - ActualHeight - 12;
        }

        _topmostMode = InteractionWindow.NormalizeTopmostMode(
            await _settingsStore.GetAsync("TopmostMode"));
        ApplyTopmostPolicy();
        _topmostTimer.Start();
        UpdateTrayTexts();
    }

    private async void CharacterView_CharacterClicked(object? sender, string area)
    {
        _touchReset?.Cancel();
        _touchReset?.Dispose();
        _touchReset = new CancellationTokenSource();

        await CharacterView.SetExpressionAsync("moemoe");
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1500), _touchReset.Token);
            await CharacterView.ResetPoseAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CharacterView_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        ModePopup.IsOpen = false;
        _dragStartCursor = GetCursorInDeviceIndependentPixels();
        _dragStartWindow = new System.Windows.Point(Left, Top);
        _isPointerPressed = true;
        _isDragging = false;
        CharacterView.CaptureMouse();
        e.Handled = true;
    }

    private void CharacterView_PreviewMouseMove(
        object? sender,
        System.Windows.Input.MouseEventArgs e)
    {
        var now = DateTime.UtcNow;
        if (!_isPointerPressed &&
            now - _lastFocusUpdateUtc >= TimeSpan.FromMilliseconds(16))
        {
            _lastFocusUpdateUtc = now;
            var position = e.GetPosition(CharacterView);
            _ = CharacterView.FocusAtAsync(position.X, position.Y);
        }

        if (!_isPointerPressed || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var cursor = GetCursorInDeviceIndependentPixels();
        if (!_isDragging &&
            (cursor - _dragStartCursor).Length < 5)
        {
            e.Handled = true;
            return;
        }

        _isDragging = true;
        var targetLeft = _dragStartWindow.X + cursor.X - _dragStartCursor.X;
        var targetTop = _dragStartWindow.Y + cursor.Y - _dragStartCursor.Y;
        Left = Math.Clamp(
            targetLeft,
            SystemParameters.WorkArea.Left - ActualWidth * 0.35,
            SystemParameters.WorkArea.Right - ActualWidth * 0.65);
        Top = Math.Clamp(
            targetTop,
            SystemParameters.WorkArea.Top,
            SystemParameters.WorkArea.Bottom - ActualHeight * 0.35);
        e.Handled = true;
    }

    private async void CharacterView_PreviewMouseLeftButtonUp(
        object? sender,
        MouseButtonEventArgs e)
    {
        if (!_isPointerPressed)
        {
            return;
        }

        var clickPosition = e.GetPosition(CharacterView);
        var wasDragging = _isDragging;
        _isPointerPressed = false;
        _isDragging = false;
        CharacterView.ReleaseMouseCapture();
        e.Handled = true;

        if (!wasDragging)
        {
            await CharacterView.InteractAtAsync(
                clickPosition.X,
                clickPosition.Y);
            return;
        }

        await SavePetPositionAsync();
    }

    private void CharacterView_PreviewMouseRightButtonUp(
        object? sender,
        MouseButtonEventArgs e)
    {
        ModePopup.IsOpen = true;
        e.Handled = true;
    }

    private async void CharacterView_LostMouseCapture(
        object? sender,
        System.Windows.Input.MouseEventArgs e)
    {
        if (!_isPointerPressed)
        {
            return;
        }

        var shouldSave = _isDragging;
        _isPointerPressed = false;
        _isDragging = false;
        if (shouldSave)
        {
            await SavePetPositionAsync();
        }
    }

    private async Task SavePetPositionAsync()
    {
        await _settingsStore.SetAsync(
            "PetLeft",
            Left.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await _settingsStore.SetAsync(
            "PetTop",
            Top.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private void ChatMenuButton_Click(object sender, RoutedEventArgs e) =>
        OpenInteraction(InteractionMode.Chat);

    private void SingingMenuButton_Click(object sender, RoutedEventArgs e) =>
        OpenInteraction(InteractionMode.Singing);

    private void OpenInteraction(InteractionMode mode)
    {
        ModePopup.IsOpen = false;
        _activeInteractionMode = mode;
        if (mode == InteractionMode.Chat)
        {
            _offlineMusicPlayer.Stop();
        }

        if (_interactionWindow is null)
        {
            _interactionWindow = new InteractionWindow(
                _conversationController,
                _speechQueue,
                _ttsService,
                _singingSynthesisService,
                _audioPlaybackService,
                _offlineMusicPlayer,
                _settingsStore,
                _localization,
                CharacterView);
            _interactionWindow.Closed += (_, _) => _interactionWindow = null;
            _interactionWindow.TopmostModeChanged += (_, mode) =>
            {
                _topmostMode = InteractionWindow.NormalizeTopmostMode(mode);
                ApplyTopmostPolicy();
            };
        }

        _interactionWindow.ShowMode(mode);
        ApplyTopmostPolicy();
        var targetLeft = Left + ActualWidth - 6;
        var targetTop = Top + ActualHeight - _interactionWindow.Height;
        _interactionWindow.Left = Math.Clamp(
            targetLeft,
            SystemParameters.WorkArea.Left + 8,
            SystemParameters.WorkArea.Right - _interactionWindow.Width - 8);
        _interactionWindow.Top = Math.Clamp(
            targetTop,
            SystemParameters.WorkArea.Top + 8,
            SystemParameters.WorkArea.Bottom - _interactionWindow.Height - 8);
        _interactionWindow.Show();
        _interactionWindow.Activate();
    }

    private void ApplyTopmostPolicy()
    {
        var shouldBeTopmost = _topmostMode switch
        {
            InteractionWindow.TopmostAlways => true,
            InteractionWindow.TopmostNever => false,
            _ => !IsForegroundLargeTaskWindow()
        };

        if (Topmost != shouldBeTopmost)
        {
            Topmost = shouldBeTopmost;
        }

        if (_interactionWindow is not null &&
            _interactionWindow.Topmost != shouldBeTopmost)
        {
            _interactionWindow.Topmost = shouldBeTopmost;
        }
    }

    private bool IsForegroundLargeTaskWindow()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        var ownHandle = new WindowInteropHelper(this).Handle;
        if (foreground == ownHandle)
        {
            return false;
        }

        if (_interactionWindow is not null &&
            foreground == new WindowInteropHelper(_interactionWindow).Handle)
        {
            return false;
        }

        if (!GetWindowRect(foreground, out var rect))
        {
            return false;
        }

        var screen = WinForms.Screen.FromHandle(foreground);
        var bounds = screen.Bounds;
        var workArea = screen.WorkingArea;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var foregroundArea = Math.Max(0, width) * Math.Max(0, height);
        var workAreaSize = workArea.Width * workArea.Height;
        var coversScreen = rect.Left <= bounds.Left + 2 &&
                           rect.Top <= bounds.Top + 2 &&
                           width >= bounds.Width - 4 &&
                           height >= bounds.Height - 4;
        var coversWorkArea = rect.Left <= workArea.Left + 4 &&
                             rect.Top <= workArea.Top + 4 &&
                             width >= workArea.Width - 8 &&
                             height >= workArea.Height - 8;
        var dominatesWorkArea =
            workAreaSize > 0 &&
            foregroundArea >= workAreaSize * 0.86;
        return coversScreen || coversWorkArea || dominatesWorkArea;
    }

    private void OfflineMusicPlayer_StateChanged(
        object? sender,
        OfflineMusicStateChanged state)
    {
        if (state.State == OfflineMusicState.Playing)
        {
            Dispatcher.InvokeAsync(
                async () =>
                    await CharacterView.SetVisualModeAsync(
                        _activeInteractionMode == InteractionMode.Singing
                            ? "singing"
                            : "playback"));
        }
    }

    private void SpeechQueue_StateChanged(
        object? sender,
        SpeechQueueStateChanged state)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            if (state.State == SpeechQueueState.Playing)
            {
                _mouthTimer.Start();
            }
            else if (state.State is SpeechQueueState.Idle or SpeechQueueState.Failed)
            {
                _mouthTimer.Stop();
                await CharacterView.SetMouthOpenAsync(0);
            }
        });
    }

    private void Localization_LanguageChanged(object? sender, EventArgs e)
    {
        ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        ChatMenuButton.Content = _localization["Chat"];
        SingingMenuButton.Content = _localization["Singing"];
        UpdateTrayTexts();
    }

    private void InitializeTrayIcon()
    {
        // 应用图标已嵌入 EXE，托盘直接读取可执行文件，避免发布目录重复保存图标。
        _trayIcon = Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);

        _showPetTrayItem = new WinForms.ToolStripMenuItem();
        _showPetTrayItem.Click += (_, _) => Dispatcher.Invoke(TogglePetVisibility);
        _chatTrayItem = new WinForms.ToolStripMenuItem();
        _chatTrayItem.Click += (_, _) =>
            Dispatcher.Invoke(() => OpenInteraction(InteractionMode.Chat));
        _singingTrayItem = new WinForms.ToolStripMenuItem();
        _singingTrayItem.Click += (_, _) =>
            Dispatcher.Invoke(() => OpenInteraction(InteractionMode.Singing));
        _settingsTrayItem = new WinForms.ToolStripMenuItem();
        _settingsTrayItem.Click += (_, _) =>
            Dispatcher.Invoke(() => OpenInteraction(InteractionMode.Settings));
        _exitTrayItem = new WinForms.ToolStripMenuItem();
        _exitTrayItem.Click += (_, _) => Dispatcher.Invoke(ExitApplication);

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.AddRange(
        [
            _showPetTrayItem,
            new WinForms.ToolStripSeparator(),
            _chatTrayItem,
            _singingTrayItem,
            _settingsTrayItem,
            new WinForms.ToolStripSeparator(),
            _exitTrayItem
        ]);
        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = _trayIcon,
            Text = "VocaLink",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowPet);
    }

    private void TogglePetVisibility()
    {
        if (IsVisible)
        {
            Hide();
        }
        else
        {
            ShowPet();
        }

        UpdateTrayTexts();
    }

    private void ShowPet()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        ApplyTopmostPolicy();
        UpdateTrayTexts();
    }

    private void UpdateTrayTexts()
    {
        if (_showPetTrayItem is null)
        {
            return;
        }

        _showPetTrayItem.Text = _localization[
            IsVisible ? "HidePet" : "ShowPet"];
        _chatTrayItem!.Text = _localization["Chat"];
        _singingTrayItem!.Text = _localization["Singing"];
        _settingsTrayItem!.Text = _localization["Settings"];
        _exitTrayItem!.Text = _localization["Exit"];
    }

    private void ExitApplication()
    {
        if (_isExiting) return;
        _isExiting = true;
        _conversationController.CancelActiveRequest();
        _interactionWindow?.Close();
        if (System.Windows.Application.Current is App app) app.RequestFullExit();
        else System.Windows.Application.Current.Shutdown();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _conversationController.CancelActiveRequest();
        _touchReset?.Cancel();
        _touchReset?.Dispose();
        _speechQueue.StateChanged -= SpeechQueue_StateChanged;
        _offlineMusicPlayer.StateChanged -= OfflineMusicPlayer_StateChanged;
        _localization.LanguageChanged -= Localization_LanguageChanged;
        _mouthTimer.Stop();
        _topmostTimer.Stop();
        _interactionWindow?.Close();
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }

        _trayIcon?.Dispose();
        // 即使窗口由系统关机、任务栏或其他正常关闭路径结束，也必须进入完整退出流程。
        if (!_isExiting)
        {
            _isExiting = true;
            if (System.Windows.Application.Current is App app) app.RequestFullExit();
            else System.Windows.Application.Current.Shutdown();
        }
    }

    private System.Windows.Point GetCursorInDeviceIndependentPixels()
    {
        var cursor = WinForms.Cursor.Position;
        var dpi = VisualTreeHelper.GetDpi(this);
        return new System.Windows.Point(
            cursor.X / dpi.DpiScaleX,
            cursor.Y / dpi.DpiScaleY);
    }

    private bool IsVisiblePosition(double left, double top) =>
        left < SystemParameters.WorkArea.Right &&
        top < SystemParameters.WorkArea.Bottom &&
        left + ActualWidth > SystemParameters.WorkArea.Left &&
        top + ActualHeight > SystemParameters.WorkArea.Top;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(
        IntPtr hWnd,
        out NativeRect lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
    }
}
