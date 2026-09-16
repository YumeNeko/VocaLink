using System.Text.Json;

namespace VocaLink.Infrastructure.Tts;

/// <summary>解析本地 GPT-SoVITS 所使用的 Python 解释器。</summary>
public sealed class TtsOptions
{
    public string PythonPath { get; set; } = string.Empty;

    public static TtsOptions Load(string path, string rootDirectory)
    {
        var options = File.Exists(path)
            ? JsonSerializer.Deserialize<TtsOptions>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new TtsOptions()
            : new TtsOptions();

        var environmentPath = Environment.GetEnvironmentVariable("VOCALINK_TTS_PYTHON");
        options.PythonPath = ResolvePython(environmentPath ?? options.PythonPath, rootDirectory);
        return options;
    }

    private static string ResolvePython(string configuredPath, string rootDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var expanded = Environment.ExpandEnvironmentVariables(configuredPath.Trim());
            if (File.Exists(expanded)) return Path.GetFullPath(expanded);
        }

        foreach (var localPath in new[]
                 {
                     Path.Combine(rootDirectory, ".venv", "Scripts", "python.exe"),
                     Path.Combine(rootDirectory, "venv", "Scripts", "python.exe")
                 })
        {
            if (File.Exists(localPath)) return localPath;
        }

        foreach (var root in CondaEnvironmentRoots())
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var environment in Directory.EnumerateDirectories(root))
                {
                    var python = Path.Combine(environment, "python.exe");
                    var package = Path.Combine(environment, "Lib", "site-packages", "gsv_tts");
                    if (File.Exists(python) && Directory.Exists(package)) return python;
                }
            }
            catch (UnauthorizedAccessException)
            {
                // 无权读取的环境目录直接跳过，继续尝试用户配置或 PATH。
            }
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var python = Path.Combine(directory.Trim('"'), "python.exe");
                if (File.Exists(python)) return python;
            }
            catch (ArgumentException)
            {
                // PATH 中的异常条目不影响后续候选项。
            }
        }

        return string.IsNullOrWhiteSpace(configuredPath) ? "python.exe" : configuredPath;
    }

    private static IEnumerable<string> CondaEnvironmentRoots()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return @"C:\Applications\Miniconda3\envs";
        yield return Path.Combine(userProfile, "miniconda3", "envs");
        yield return Path.Combine(userProfile, "anaconda3", "envs");
        yield return Path.Combine(localAppData, "miniconda3", "envs");
    }
}
