using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace VocaLink.Infrastructure.AutoACE;

public interface IAceCliClient
{
    Task EnsureRunningAsync(CancellationToken cancellationToken);
    Task<JsonElement> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

public sealed class AceCliException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>直接调用 ACE 官方命令，不启动第三方代理，也不模拟鼠标或快捷键。</summary>
public sealed class AceCliClient(AceCliOptions options) : IAceCliClient
{
    public async Task EnsureRunningAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(options.CliPath) || !File.Exists(options.ExecutablePath))
            throw new AceCliException("ACE_NOT_INSTALLED", "找不到 ACE Studio 或官方 CLI，请安装 ACE Studio。");

        // 自动化只连接用户主动打开的编辑器，避免 ACE 冷启动阶段窗口与代理状态不同步。
        if (!AceWindowVisibility.HasOpenEditor())
            throw new AceCliException("ACE_NOT_RUNNING", "请先打开 ACE Studio，再返回 VocaLink 开始任务。");

        try
        {
            await ExecuteAsync(["project", "info"], cancellationToken);
        }
        catch (AceCliException ex) when (ex.Code == "ACE_UNAVAILABLE")
        {
            throw new AceCliException("ACE_ACCESS_REQUIRED", "ACE Studio 已打开，但外部代理访问尚不可用。");
        }
    }

    public async Task<JsonElement> ExecuteAsync(IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(options.CliPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        // ArgumentList 防止含空格、引号的路径被当作额外命令；不使用 --yes 或丢弃工程选项。
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--json");
        using var process = Process.Start(start)
            ?? throw new AceCliException("ACE_UNAVAILABLE", "无法启动 ACE 官方 CLI。");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            // 这里只结束本次 CLI 子进程；后台 ACE 作业由服务按自身 jobId 取消。
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await Task.WhenAll(stdout, stderr);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new AceCliException("COMMAND_TIMEOUT", "ACE 命令未响应，请人工检查工程。");
        }
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
        {
            var code = process.ExitCode == 3 || error.Contains("bridge not reachable", StringComparison.OrdinalIgnoreCase)
                ? "ACE_UNAVAILABLE" : "ACE_COMMAND_FAILED";
            foreach (var known in new[] { "UNSAVED_CHANGES", "USER_BUSY", "STALE_WRITE", "NOT_FOUND" })
                if (error.Contains(known, StringComparison.Ordinal)) code = known;
            throw new AceCliException(code, error.Length > 0 ? error : output);
        }
        try
        {
            using var document = JsonDocument.Parse(output);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new AceCliException("INVALID_RESPONSE", $"ACE 返回格式与预期不一致：{ex.Message}");
        }
    }
}
