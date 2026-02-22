namespace BedwarsBot;

public class BotConfig
{
    // official / napcat
    public string Platform { get; set; } = "official";

    public string AppId { get; set; } = "请在此填入AppID";
    public string ClientSecret { get; set; } = "请在此填入ClientSecret";
    // 注意：这里填你 Node.js 生成的那个 .db 文件的绝对路径
    public string ShoutLogDbPath { get; set; } = @"D:\你的Node项目路径\chat_logs.db";
    // 事件订阅 Intents（按官方文档填写）
    // 群聊@消息事件: intents 1<<25
    public int Intents { get; set; } = 33554432;

    // 图片图床配置（用于官方机器人发送图片）
    public ImageHostConfig ImageHost { get; set; } = new();

    // NapCat 配置
    public NapcatConfig Napcat { get; set; } = new();

    // 官方 Webhook 配置（启用后官方机器人不走 WS）
    public WebhookConfig Webhook { get; set; } = new();
}

public class ImageHostConfig
{
    // none/smms/custom
    public string Provider { get; set; } = "none";

    // sm.ms 的 token，Provider=smms 时使用
    public string Token { get; set; } = "";

    // Provider=custom 时使用
    public string UploadUrl { get; set; } = "";
    public string FileFieldName { get; set; } = "file";
    public string UrlJsonPath { get; set; } = "data.url";
    public string SuccessJsonPath { get; set; } = "";
    public string SuccessExpected { get; set; } = "true";
    public string AuthHeaderName { get; set; } = "Authorization";
    public string AuthHeaderValuePrefix { get; set; } = "Bearer ";
}

public class NapcatConfig
{
    // 示例: ws://127.0.0.1:3001
    public string WsUrl { get; set; } = "ws://127.0.0.1:3001";

    // 如果 NapCat 开了 access_token，这里填
    public string AccessToken { get; set; } = "";
}

public class WebhookConfig
{
    // true=启用官方 Webhook 接入（禁用官方 WS）
    public bool Enabled { get; set; } = false;

    // Webhook 回调签名使用的 secret。
    // 留空时默认回退为 ClientSecret。
    public string Secret { get; set; } = "";

    // HttpListener 监听前缀，支持多个
    // 示例: http://+:5001/
    public List<string> ListenPrefixes { get; set; } = new()
    {
        "http://+:5001/"
    };

    // 回调路径，仅匹配此路径
    // 示例: /api/qqbot/webhook
    public string CallbackPath { get; set; } = "/api/qqbot/webhook";

    // 是否校验 X-Signature-Ed25519/X-Signature-Timestamp
    public bool VerifySignature { get; set; } = true;

    // 反向代理场景：是否信任 X-Forwarded-For / X-Forwarded-Proto / X-Real-IP
    public bool TrustForwardedHeaders { get; set; } = true;

    // 可选：可信代理 IP 或 CIDR（如 "203.0.113.10"、"203.0.113.0/24"）
    // 留空时表示接受任意来源的转发头（仅建议在已做好外网访问控制时使用）
    public List<string> TrustedProxyIps { get; set; } = new();
}
