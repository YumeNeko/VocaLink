using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using VocaLink.Application.Abstractions;
using VocaLink.Domain;

namespace VocaLink.Infrastructure.Qwen;

/// <summary>
/// 使用 OpenAI 兼容协议调用千问。
/// </summary>
public sealed class QwenLlmService : ILLMService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

    private readonly QwenOptions _options;
    private readonly HttpClient _httpClient;

    public QwenLlmService(QwenOptions options, HttpClient? httpClient = null)
    {
        _options = options;
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public async Task<string> GenerateReplyAsync(
        UserProfile profile,
        IReadOnlyList<ConversationMessage> recentMessages,
        CancellationToken cancellationToken)
    {
        var apiKey = Environment.GetEnvironmentVariable(_options.ApiKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"环境变量 {_options.ApiKeyEnvironmentVariable} 未配置。");
        }

        var messages = new List<object>
        {
            new
            {
                role = "system",
                content = BuildSystemPrompt(profile)
            }
        };

        messages.AddRange(recentMessages
            .Where(message => message.Role is ConversationRole.User or ConversationRole.Assistant)
            .Select(message => new
            {
                role = message.Role == ConversationRole.User ? "user" : "assistant",
                content = message.Content
            }));

        var payload = new
        {
            model = _options.Model,
            messages,
            temperature = 0.75,
            top_p = 0.9,
            max_tokens = 2048,
            enable_thinking = _options.EnableThinking
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{_options.BaseUrl.TrimEnd('/')}/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload, JsonOptions),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"千问请求失败：{(int)response.StatusCode} {response.ReasonPhrase}。");
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString()
            ?.Trim()
            ?? string.Empty;
    }

    public void Dispose() => _httpClient.Dispose();

    private static string BuildSystemPrompt(UserProfile profile) =>
        $"""
        你是虚拟歌手洛天依，也是用户桌面上的陪伴型伙伴。
        请先在心里判断用户此刻可能的情绪，再给出自然、温柔且有共情的回应。
        保持洛天依式的亲切、乐观、真诚和一点音乐感，但不要声称自己是真人。
        不要输出情绪标签、分析过程、舞台动作、括号旁白或 Markdown。
        默认用用户正在使用的语言回答；用户说英文时自然地用英文回答。
        根据用户问题本身决定内容详略，完整回答，不要为了语音速度刻意缩短内容。
        用户称呼：{profile.DisplayName}
        偏好语言：{profile.PreferredLanguage}
        用户备注：{profile.Notes}
        """;
}
