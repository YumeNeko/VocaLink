using VocaLink.Domain;

namespace VocaLink.Application.Abstractions;

public interface ILLMService
{
    Task<string> GenerateReplyAsync(
        UserProfile profile,
        IReadOnlyList<ConversationMessage> recentMessages,
        CancellationToken cancellationToken);
}
