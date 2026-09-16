namespace VocaLink.Domain;

/// <summary>
/// 一次角色回复及其生成来源。
/// </summary>
public sealed record ChatReply(string Text, bool IsOffline);
