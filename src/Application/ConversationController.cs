using VocaLink.Application.Abstractions;
using VocaLink.Domain;

namespace VocaLink.Application;

/// <summary>
/// 对话主链路控制器：保存输入、组织历史、调用模型并处理离线回退。
/// </summary>
public sealed class ConversationController
{
    private const int RecentMessageLimit = 20;

    private readonly IConversationRepository _repository;
    private readonly ILLMService _llmService;
    private readonly OfflineResponseService _offlineResponseService;
    private CancellationTokenSource? _activeRequest;

    public ConversationController(
        IConversationRepository repository,
        ILLMService llmService,
        OfflineResponseService offlineResponseService)
    {
        _repository = repository;
        _llmService = llmService;
        _offlineResponseService = offlineResponseService;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        _repository.InitializeAsync(cancellationToken);

    public Task<IReadOnlyList<ConversationMessage>> LoadRecentAsync(
        CancellationToken cancellationToken = default) =>
        _repository.GetRecentAsync(RecentMessageLimit, cancellationToken);

    public async Task<ChatReply> SendAsync(
        string userText,
        UserProfile profile,
        CancellationToken cancellationToken = default)
    {
        var normalizedText = userText.Trim();
        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            throw new ArgumentException("消息不能为空。", nameof(userText));
        }

        CancelActiveRequest();
        _activeRequest = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var requestToken = _activeRequest.Token;

        await _repository.AddAsync(
            ConversationRole.User,
            normalizedText,
            false,
            requestToken);

        var history = await _repository.GetRecentAsync(RecentMessageLimit, requestToken);

        string replyText;
        var isOffline = false;

        try
        {
            replyText = await _llmService.GenerateReplyAsync(profile, history, requestToken);
            if (string.IsNullOrWhiteSpace(replyText))
            {
                throw new InvalidOperationException("模型返回了空回复。");
            }
        }
        catch (OperationCanceledException) when (requestToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            replyText = _offlineResponseService.CreateReply(normalizedText);
            isOffline = true;
        }

        await _repository.AddAsync(
            ConversationRole.Assistant,
            replyText,
            isOffline,
            requestToken);

        return new ChatReply(replyText, isOffline);
    }

    public void CancelActiveRequest()
    {
        _activeRequest?.Cancel();
        _activeRequest?.Dispose();
        _activeRequest = null;
    }
}
