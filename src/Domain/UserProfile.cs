namespace VocaLink.Domain;

/// <summary>
/// 单用户画像。第一版只保存称呼、语言和简短备注。
/// </summary>
public sealed record UserProfile(
    string DisplayName,
    string PreferredLanguage,
    string Notes);
