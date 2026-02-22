using Newtonsoft.Json.Linq;

namespace BedwarsBot;

public sealed class NapcatAutoApproveAndWelcome
{
    private readonly NapcatBot _bot;

    public NapcatAutoApproveAndWelcome(NapcatBot bot)
    {
        _bot = bot;
    }

    public void Register()
    {
        _bot.OnRawEvent += HandleRawEventAsync;
    }

    private async Task HandleRawEventAsync(JObject raw)
    {
        var postType = raw["post_type"]?.ToString() ?? string.Empty;

        if (string.Equals(postType, "request", StringComparison.OrdinalIgnoreCase))
        {
            await HandleRequestAsync(raw);
            return;
        }

        if (string.Equals(postType, "notice", StringComparison.OrdinalIgnoreCase))
        {
            await HandleNoticeAsync(raw);
        }
    }

    private async Task HandleRequestAsync(JObject raw)
    {
        var requestType = raw["request_type"]?.ToString() ?? string.Empty;
        var flag = raw["flag"]?.ToString() ?? string.Empty;

        if (string.Equals(requestType, "friend", StringComparison.OrdinalIgnoreCase))
        {
            var ok = await _bot.ApproveFriendRequestAsync(flag);
            Console.WriteLine(ok ? "[NapCat自动处理] 已自动同意好友申请" : "[NapCat自动处理] 同意好友申请失败");
            return;
        }

        if (string.Equals(requestType, "group", StringComparison.OrdinalIgnoreCase))
        {
            var subType = raw["sub_type"]?.ToString() ?? string.Empty;
            if (string.Equals(subType, "invite", StringComparison.OrdinalIgnoreCase))
            {
                var ok = await _bot.ApproveGroupInviteAsync(flag);
                Console.WriteLine(ok ? "[NapCat自动处理] 已自动同意群邀请" : "[NapCat自动处理] 同意群邀请失败");
            }
        }
    }

    private async Task HandleNoticeAsync(JObject raw)
    {
        var noticeType = raw["notice_type"]?.ToString() ?? string.Empty;
        if (!string.Equals(noticeType, "group_increase", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var selfId = raw["self_id"]?.ToString() ?? string.Empty;
        var userId = raw["user_id"]?.ToString() ?? string.Empty;
        var groupId = raw["group_id"]?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(groupId)) return;

        // 只在官方群发欢迎
        if (!string.Equals(groupId, WelcomeMessageBuilder.OfficialGroupId, StringComparison.Ordinal))
        {
            return;
        }

        // 机器人自己入群时不发欢迎词
        if (!string.IsNullOrWhiteSpace(selfId) && string.Equals(selfId, userId, StringComparison.Ordinal))
        {
            return;
        }

        var welcomeText = WelcomeMessageBuilder.Build(userId);
        await Task.Delay(TimeSpan.FromSeconds(1));
        await _bot.SendTextAsync(groupId, welcomeText);
        Console.WriteLine($"[NapCat自动处理] 已发送入群欢迎语 group={groupId}, user={userId}");
    }
}
