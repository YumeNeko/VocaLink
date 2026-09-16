namespace VocaLink.Application;

/// <summary>
/// 在线服务不可用时的透明离线回退，不伪装成实时生成。
/// </summary>
public sealed class OfflineResponseService
{
    private const string OfflineReply =
        "网络好像开小差了，等连接恢复后我们再继续聊吧。";

    public string CreateReply(string userText) => OfflineReply;
}
