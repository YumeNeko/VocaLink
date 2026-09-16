using VocaLink.Domain;

namespace VocaLink.Application.Abstractions;

public interface IConversationRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationMessage>> GetRecentAsync(
        int limit,
        CancellationToken cancellationToken = default);

    Task<ConversationMessage> AddAsync(
        ConversationRole role,
        string content,
        bool isOffline,
        CancellationToken cancellationToken = default);
}
