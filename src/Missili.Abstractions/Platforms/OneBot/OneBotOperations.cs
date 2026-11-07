using System.Text.Json;

namespace Missili.Abstractions;

/// <summary>OneBot 常用非发送操作的强类型参数辅助，不注册 Agent 工具。
/// 依据：https://github.com/botuniverse/onebot-11/blob/d4456ee706f9ada9c2dfde56a2bcfc69752600e4/api/public.md 。</summary>
public static class OneBotOperations
{
    /// <summary>查询已连接账号身份。</summary>
    public static Task<PlatformOperationResult> GetLoginInfoAsync(this IPlatformOperations service, string adapterId, string botAccountId, CancellationToken token = default) => Call(service, adapterId, botAccountId, "get_login_info", [], token);
    /// <summary>查询平台已知的运行状态，QQ 在线可能未知。</summary>
    public static Task<PlatformOperationResult> GetStatusAsync(this IPlatformOperations service, string adapterId, string botAccountId, CancellationToken token = default) => Call(service, adapterId, botAccountId, "get_status", [], token);
    /// <summary>显式查询目标消息；需要归档和本地优先时使用结构化内容服务。</summary>
    public static Task<PlatformOperationResult> GetMessageAsync(this IPlatformOperations service, MessageAddress address, string messageId, CancellationToken token = default) => Call(service, address.AdapterId, address.BotAccountId, "get_msg", new() { ["message_id"] = messageId }, token);
    /// <summary>显式查询合并转发，适配器兼容和后台归档通过内容服务完成。</summary>
    public static Task<PlatformOperationResult> GetForwardAsync(this IPlatformOperations service, MessageAddress address, string id, CancellationToken token = default) => Call(service, address.AdapterId, address.BotAccountId, "get_forward_msg", new() { ["id"] = id }, token);
    /// <summary>查询图片原件来源，远端路径不代表宿主可读取。</summary>
    public static Task<PlatformOperationResult> GetImageAsync(this IPlatformOperations service, MessageAddress address, string file, CancellationToken token = default) => Call(service, address.AdapterId, address.BotAccountId, "get_image", new() { ["file"] = file }, token);
    /// <summary>请求平台转换语音，结果格式须由调用者明确提供。</summary>
    public static Task<PlatformOperationResult> GetRecordAsync(this IPlatformOperations service, MessageAddress address, string file, string format, CancellationToken token = default) => Call(service, address.AdapterId, address.BotAccountId, "get_record", new() { ["file"] = file, ["out_format"] = format }, token);
    /// <summary>明确请求平台撤回消息，不删除本地原始内容。</summary>
    public static Task<PlatformOperationResult> DeleteMessageAsync(this IPlatformOperations service, MessageAddress address, string messageId, CancellationToken token = default) => Call(service, address.AdapterId, address.BotAccountId, "delete_msg", new() { ["message_id"] = messageId }, token);
    /// <summary>由插件明确处理好友请求，适配器不会自动同意。</summary>
    public static Task<PlatformOperationResult> SetFriendRequestAsync(this IPlatformOperations service, string adapterId, string botAccountId, string flag, bool approve, string remark = "", CancellationToken token = default) => Call(service, adapterId, botAccountId, "set_friend_add_request", new() { ["flag"] = flag, ["approve"] = approve, ["remark"] = remark }, token);
    /// <summary>由插件明确处理入群或邀请请求，拒绝原因保持原值。</summary>
    public static Task<PlatformOperationResult> SetGroupRequestAsync(this IPlatformOperations service, string adapterId, string botAccountId, string flag, string subType, bool approve, string reason = "", CancellationToken token = default) => Call(service, adapterId, botAccountId, "set_group_add_request", new() { ["flag"] = flag, ["sub_type"] = subType, ["approve"] = approve, ["reason"] = reason }, token);
    /// <summary>查询群成员信息，是否绕过远端缓存由调用者明确选择。</summary>
    public static Task<PlatformOperationResult> GetGroupMemberAsync(this IPlatformOperations service, MessageAddress address, string userId, bool noCache = false, CancellationToken token = default) => Call(service, address.AdapterId, address.BotAccountId, "get_group_member_info", new() { ["group_id"] = address.ConversationId, ["user_id"] = userId, ["no_cache"] = noCache }, token);
    /// <summary>查询通知涉及的群荣誉信息。</summary>
    public static Task<PlatformOperationResult> GetGroupHonorAsync(this IPlatformOperations service, MessageAddress address, string type, CancellationToken token = default) => Call(service, address.AdapterId, address.BotAccountId, "get_group_honor_info", new() { ["group_id"] = address.ConversationId, ["type"] = type }, token);

    /// <summary>使用共享字典构造参数，只调用明确 action，不附加异步或限速后缀。</summary>
    private static Task<PlatformOperationResult> Call(IPlatformOperations service, string adapterId, string botAccountId, string action, Dictionary<string, object> parameters, CancellationToken token) =>
        service.InvokeAsync(adapterId, botAccountId, action, JsonSerializer.SerializeToElement(parameters), token);
}
