namespace VocaLink.Domain;

/// <summary>
/// 唯一主会话中的一条消息。
/// </summary>
public sealed record ConversationMessage(
    long Id,
    ConversationRole Role,
    string Content,
    DateTimeOffset CreatedAt,
    bool IsOffline = false);
