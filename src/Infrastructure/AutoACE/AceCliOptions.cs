using System.Text.Json;

namespace VocaLink.Infrastructure.AutoACE;

public sealed class AceCliOptions
{
    public string ExecutablePath { get; set; } = @"C:\Program Files\ACE Studio\ACE Studio.exe";
    public string CliPath { get; set; } = @"C:\Program Files\ACE Studio\acestudio-cli.exe";
    public string SingerName { get; set; } = "洛天依";
    public int TaskTimeoutMinutes { get; set; } = 30;
    public int PollIntervalMilliseconds { get; set; } = 2000;

    public static AceCliOptions Load(string path)
    {
        var options = File.Exists(path)
            ? JsonSerializer.Deserialize<AceCliOptions>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("ACE 配置为空。")
            : new AceCliOptions();
        if (options.TaskTimeoutMinutes is < 1 or > 180 ||
            options.PollIntervalMilliseconds is < 100 or > 10000 ||
            string.IsNullOrWhiteSpace(options.SingerName) ||
            !Path.IsPathFullyQualified(options.CliPath) ||
            !Path.IsPathFullyQualified(options.ExecutablePath))
            throw new InvalidDataException("ACE 配置参数无效。");
        return options;
    }
}
