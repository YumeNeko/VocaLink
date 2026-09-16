using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace VocaLink.Live2D;

/// <summary>
/// Live2D WebView2 宿主与 C#/JavaScript 双向消息桥。
/// </summary>
public partial class Live2DView : UserControl
{
    private const string VirtualHost = "vocalink.local";
    private string _defaultExpression = "normal";
    private string _visualMode = "normal";

    public event EventHandler<string>? CharacterClicked;

    public event EventHandler<bool>? ModelLoaded;

    public Live2DView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public Task SetExpressionAsync(string expression) =>
        ExecuteAsync($"window.vocalink?.setExpression({JsonSerializer.Serialize(expression)});");

    public Task SetDefaultExpressionAsync(string expression)
    {
        _defaultExpression = string.IsNullOrWhiteSpace(expression)
            ? "normal"
            : expression;
        return SetExpressionAsync(_defaultExpression);
    }

    public Task SetVisualModeAsync(string mode)
    {
        _visualMode = mode is "chat" or "singing" or "playback"
            ? mode
            : "normal";
        _defaultExpression = _visualMode == "singing"
            ? "sing"
            : "normal";
        return ExecuteAsync(
            $"window.vocalink?.setVisualMode({JsonSerializer.Serialize(_visualMode)});");
    }

    public Task SetMouthOpenAsync(double value) =>
        ExecuteAsync($"window.vocalink?.setMouth({Math.Clamp(value, 0, 1).ToString(System.Globalization.CultureInfo.InvariantCulture)});");

    public Task PlayMotionAsync(string group) =>
        ExecuteAsync($"window.vocalink?.playMotion({JsonSerializer.Serialize(group)});");

    public async Task ResetPoseAsync()
    {
        var script =
            $"window.vocalink?.resetPose({JsonSerializer.Serialize(_defaultExpression)}, {JsonSerializer.Serialize(_visualMode)});";
        await ExecuteAsync(script);
        await Task.Delay(350);
        await ExecuteAsync(script);
    }

    public Task FocusAtAsync(double x, double y) =>
        ExecuteAsync(
            $"window.vocalink?.focusAt({FormatNumber(x)}, {FormatNumber(y)});");

    public Task InteractAtAsync(double x, double y) =>
        ExecuteAsync(
            $"window.vocalink?.interactAt({FormatNumber(x)}, {FormatNumber(y)});");

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        try
        {
            var dataRoot =
                Environment.GetEnvironmentVariable("VOCALINK_DATA_ROOT")
                ?? Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                    "VocaLink");
            var userDataFolder = Path.Combine(dataRoot, "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: userDataFolder);
            await Browser.EnsureCoreWebView2Async(environment);

            var assetFolder = Path.Combine(AppContext.BaseDirectory, "Live2DAssets");
            if (!Directory.Exists(assetFolder))
            {
                throw new DirectoryNotFoundException(
                    $"Live2D 资源目录不存在：{assetFolder}");
            }

            Browser.CoreWebView2.SetVirtualHostNameToFolderMapping(
                VirtualHost,
                assetFolder,
                CoreWebView2HostResourceAccessKind.Allow);
            Browser.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Browser.Source = new Uri($"https://{VirtualHost}/index.html");
        }
        catch (Exception exception)
        {
            ShowError($"Live2D 初始化失败：{exception.Message}");
            ModelLoaded?.Invoke(this, false);
        }
    }

    private void OnWebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            var type = root.GetProperty("type").GetString();

            switch (type)
            {
                case "modelLoaded":
                    ErrorPanel.Visibility = Visibility.Collapsed;
                    ModelLoaded?.Invoke(this, true);
                    break;
                case "modelError":
                    var message = root.TryGetProperty("message", out var error)
                        ? error.GetString()
                        : "未知错误";
                    ShowError($"Live2D 加载失败：{message}");
                    ModelLoaded?.Invoke(this, false);
                    break;
                case "characterClicked":
                    var area = root.TryGetProperty("area", out var hitArea)
                        ? hitArea.GetString() ?? "body"
                        : "body";
                    CharacterClicked?.Invoke(this, area);
                    break;
            }
        }
        catch (JsonException)
        {
            // 忽略不符合桥接协议的网页消息。
        }
    }

    private async Task ExecuteAsync(string script)
    {
        if (Browser.CoreWebView2 is not null)
        {
            await Browser.CoreWebView2.ExecuteScriptAsync(script);
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private static string FormatNumber(double value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
