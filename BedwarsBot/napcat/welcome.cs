namespace BedwarsBot;

public static class WelcomeMessageBuilder
{
    public const string OfficialGroupId = "1081992954";

    public static string Build(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return "欢迎来到巴巴博一布吉岛数据查询机器人官方群，如想将机器人拉入小群，请直接加机器人好友（发消息的这个号）会自动同意加群和入群。帮助指令：/帮助。欢迎您使用巴巴博一机器人！";
        }

        return $"[CQ:at,qq={userId}] 欢迎来到巴巴博一布吉岛数据查询机器人官方群，如想将机器人拉入小群，请直接加机器人好友（发消息的这个号）会自动同意加群和入群。帮助指令：/帮助。欢迎您使用巴巴博一机器人！";
    }
}
