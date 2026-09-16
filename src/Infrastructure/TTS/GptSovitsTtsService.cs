using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Text.Json;
using VocaLink.Application.Abstractions;

namespace VocaLink.Infrastructure.Tts;

/// <summary>
/// 管理本地 GPT-SoVITS Python 侧车并调用其 HTTP 接口。
/// </summary>
public sealed class GptSovitsTtsService : ITtsService, IDisposable
{
    private readonly Uri _baseUri;
    private readonly string _pythonPath;
    private readonly string _sidecarScript;
    private readonly string _rootDirectory;
    private readonly HttpClient _httpClient;
    private readonly int _port;
    private readonly SemaphoreSlim _startupGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _processGate = new();
    private Process? _sidecarProcess;
    private SafeFileHandle? _sidecarJob;
    private bool _disposed;

    public GptSovitsTtsService(
        string pythonPath,
        string sidecarScript,
        string rootDirectory)
    {
        _pythonPath = pythonPath;
        _sidecarScript = sidecarScript;
        _rootDirectory = rootDirectory;
        _port = FindAvailablePort();
        _baseUri = new Uri($"http://127.0.0.1:{_port}/");
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public async Task<bool> IsHealthyAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync(
                new Uri(_baseUri, "health"),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var body = await response.Content.ReadAsStringAsync(
                cancellationToken);
            using var document = JsonDocument.Parse(body);
            var outputDirectory = document.RootElement.TryGetProperty(
                "output_dir",
                out var output)
                ? output.GetString()
                : null;
            var expected = Path.GetFullPath(
                Path.Combine(_rootDirectory, "Chat", "Output"));
            return string.Equals(
                Path.GetFullPath(outputDirectory ?? string.Empty),
                expected,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception)
            when (exception is HttpRequestException or JsonException or
                  ArgumentException)
        {
            return false;
        }
    }

    public async Task<string> SynthesizeAsync(
        string text,
        string language,
        CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        using var response = await _httpClient.PostAsJsonAsync(
            new Uri(_baseUri, "synthesize"),
            new { text, language },
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"GPT-SoVITS 合成失败：{response.StatusCode}。");
        }

        using var document = JsonDocument.Parse(body);
        var audioPath = document.RootElement
            .GetProperty("audio_path")
            .GetString();
        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
        {
            throw new InvalidDataException("TTS 侧车没有返回有效音频文件。");
        }

        return audioPath;
    }

    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        using var response = await _httpClient.PostAsync(
            new Uri(_baseUri, "load"),
            null,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose()
    {
        lock (_processGate)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            // 关闭作业句柄会先终止其全部子进程，防止主程序退出后遗留 Python。
            _sidecarJob?.Dispose();
            _sidecarJob = null;
            if (_sidecarProcess is { HasExited: false })
            {
                try
                {
                    _sidecarProcess.Kill(true);
                    _sidecarProcess.WaitForExit(5000);
                }
                catch (InvalidOperationException) { }
            }
            _sidecarProcess?.Dispose();
            _sidecarProcess = null;
        }
        _httpClient.Dispose();
        // 启动预热可能仍在响应取消；这些轻量同步对象由进程回收，避免并发释放竞态。
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        var token = linked.Token;
        await _startupGate.WaitAsync(token);
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(GptSovitsTtsService));
            if (await IsHealthyAsync(token)) return;

            if (!File.Exists(_pythonPath))
                throw new FileNotFoundException("找不到 TTS Python。", _pythonPath);

            if (!File.Exists(_sidecarScript))
                throw new FileNotFoundException("找不到 TTS 侧车脚本。", _sidecarScript);

            var startInfo = new ProcessStartInfo
            {
                FileName = _pythonPath,
                WorkingDirectory = Path.GetDirectoryName(_sidecarScript),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add(_sidecarScript);
            startInfo.Environment["VOCALINK_ROOT"] = _rootDirectory;
            startInfo.Environment["VOCALINK_TTS_PORT"] = _port.ToString();
            lock (_processGate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(GptSovitsTtsService));
                _sidecarProcess = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("无法启动 GPT-SoVITS 侧车。");
                _sidecarJob = CreateKillOnCloseJob(_sidecarProcess);
            }

            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(500, token);
                if (await IsHealthyAsync(token)) return;
                if (_sidecarProcess is { HasExited: true })
                    throw new InvalidOperationException("GPT-SoVITS 侧车提前退出。");
            }

            throw new TimeoutException("GPT-SoVITS 侧车启动超时。");
        }
        finally { _startupGate.Release(); }
    }

    private static int FindAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static SafeFileHandle? CreateKillOnCloseJob(Process process)
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) return null;
        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = 0x00002000
            }
        };
        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(information, pointer, false);
            if (!SetInformationJobObject(job, 9, pointer, (uint)size) ||
                !AssignProcessToJobObject(job, process.Handle))
            {
                job.Dispose();
                return null;
            }
            return job;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObject(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        int informationClass,
        IntPtr information,
        uint informationLength);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
