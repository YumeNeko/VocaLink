using System.Text.Json;

namespace VocaLink.Infrastructure.Qwen;

public sealed record QwenOptions(
    string BaseUrl,
    string Region,
    string Model,
    bool EnableThinking,
    string ApiKeyEnvironmentVariable)
{
    public static QwenOptions Load(string path)
    {
        var json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        var qwen = document.RootElement.GetProperty("Qwen");

        return new QwenOptions(
            qwen.GetProperty("BaseUrl").GetString()
                ?? throw new InvalidDataException("缺少 Qwen.BaseUrl。"),
            qwen.GetProperty("Region").GetString() ?? "China-Beijing",
            qwen.GetProperty("Model").GetString()
                ?? throw new InvalidDataException("缺少 Qwen.Model。"),
            qwen.TryGetProperty("EnableThinking", out var thinking) && thinking.GetBoolean(),
            qwen.GetProperty("ApiKeyEnvironmentVariable").GetString()
                ?? "DASHSCOPE_API_KEY");
    }
}
