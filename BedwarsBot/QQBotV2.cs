using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Websocket.Client;

namespace BedwarsBot;

public class QQBotV2
{
    private readonly string _appId;
    private readonly string _clientSecret;
    private readonly int _intents;
    private readonly ImageHostUploader _imageHostUploader;
    private string _accessToken;
    private readonly HttpClient _http = new();
    private WebsocketClient _ws;
    private int? _lastSequence;
    private int _shardCount = 1;
    private int _heartbeatIntervalMs = 30000;
    private readonly object _heartbeatLock = new();
    private CancellationTokenSource? _heartbeatCts;
    private readonly SemaphoreSlim _tokenRefreshLock = new(1, 1);
    private CancellationTokenSource? _tokenLoopCts;
    private DateTimeOffset _tokenExpiresAtUtc = DateTimeOffset.MinValue;
    private int _consecutiveInvalidSession;

    // 事件：收到群@消息时触发
    public event Func<GroupAtMessage, Task> OnGroupAtMessage;

    public QQBotV2(string appId, string clientSecret, int intents, ImageHostUploader imageHostUploader)
    {
        _appId = appId;
        _clientSecret = clientSecret;
        if (intents <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(intents),
                "官方群机器人 Intents 必须显式配置，建议使用 33554432（GROUP_AT_MESSAGE_CREATE）。");
        }
        _intents = intents;
        _imageHostUploader = imageHostUploader;
    }

    public async Task StartAsync()
    {
        Console.WriteLine("[Bot] 正在获取 Token...");
        await RefreshAccessToken();
        StartAccessTokenRefreshLoop();

        Console.WriteLine("[Bot] 正在获取网关...");
        string gateway = await GetGateway();

        Console.WriteLine("[Bot] 连接 WebSocket...");
        _ws = new WebsocketClient(new Uri(gateway));
        _ws.ReconnectionHappened.Subscribe(info =>
            Console.WriteLine($"[Bot] WS重连: {info}"));
        _ws.DisconnectionHappened.Subscribe(info =>
            Console.WriteLine($"[Bot] WS断开: {info}"));
        _ws.MessageReceived.Subscribe(msg => Task.Run(() => HandleMessage(msg.Text)));
        await _ws.Start();
    }

    public async Task StartHttpOnlyAsync()
    {
        Console.WriteLine("[Bot] 正在获取 Token...");
        await RefreshAccessToken();
        StartAccessTokenRefreshLoop();
    }

    private async Task HandleMessage(string jsonStr)
    {
        if (string.IsNullOrWhiteSpace(jsonStr))
        {
            return; 
        }

        try
        {
            var json = JObject.Parse(jsonStr);
            int op = json["op"]?.Value<int>() ?? -1;
            var seqToken = json["s"];
            if (seqToken != null && seqToken.Type != JTokenType.Null)
            {
                _lastSequence = seqToken.Value<int>();
            }

            if (op == 10)
            {
                _heartbeatIntervalMs = json["d"]?["heartbeat_interval"]?.Value<int>() ?? 30000;
                SendIdentify();
                StartHeartbeatLoop(_heartbeatIntervalMs);
                return;
            }

            if (op == 7)
            {
                Console.WriteLine("[Bot] 收到服务端 RECONNECT(op=7)，等待客户端自动重连。");
                return;
            }

            if (op == 9)
            {
                Console.WriteLine($"[Bot] 收到 Invalid Session(op=9)，d={json["d"]?.ToString(Formatting.None)}");
                _consecutiveInvalidSession++;
                if (_consecutiveInvalidSession >= 5)
                {
                    Console.WriteLine("[Bot] 连续 Invalid Session >= 5。常见原因：");
                    Console.WriteLine("[Bot] 1) 开发者平台已配置 Webhook 回调地址，WebSocket 会被禁用。");
                    Console.WriteLine("[Bot] 2) 当前应用未开通/不在 WebSocket 事件链路范围（需走 Webhook）。");
                    Console.WriteLine("[Bot] 3) Intents 与平台已开通事件不匹配。");
                }
                await Task.Delay(Random.Shared.Next(1200, 3500));
                try
                {
                    await RefreshAccessToken();
                    SendIdentify();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Bot] Invalid Session 后刷新/重试失败: {ex.Message}");
                }
                return;
            }

            if (op != 0)
            {
                return;
            }

            await HandleDispatchEventAsync(json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Bot] HandleMessage异常: {ex.Message}");
        }
    }

    public async Task HandleWebhookPayloadAsync(JObject json)
    {
        await HandleDispatchEventAsync(json);
    }

    private async Task HandleDispatchEventAsync(JObject json)
    {
        var eventType = json["t"]?.ToString();
        if (eventType == "READY")
        {
            _consecutiveInvalidSession = 0;
            Console.WriteLine("[Bot] READY 已收到，官方链路鉴权成功。");
            return;
        }

        if (eventType != "GROUP_AT_MESSAGE_CREATE" && eventType != "GROUP_MESSAGE_CREATE")
        {
            return;
        }

        var d = json["d"];
        string content = d?["content"]?.ToString().Trim() ?? string.Empty;
        var trimmedContent = content.TrimStart();

        if (eventType == "GROUP_MESSAGE_CREATE")
        {
            var hasAtPrefix = trimmedContent.StartsWith("<@");
            var hasBotMentionFlag = false;
            if (d?["mentions"] is JArray mentions)
            {
                foreach (var mention in mentions)
                {
                    if (mention?["bot"]?.Value<bool>() == true)
                    {
                        hasBotMentionFlag = true;
                        break;
                    }
                }
            }

            if (!hasAtPrefix && !hasBotMentionFlag)
            {
                return;
            }
        }

        var groupOpenId = d?["group_openid"]?.ToString()
                          ?? d?["group_id"]?.ToString()
                          ?? string.Empty;
        var msgId = d?["id"]?.ToString() ?? string.Empty;
        var authorId = d?["author"]?["id"]?.ToString() ?? string.Empty;
        var imageUrl = ExtractImageUrl(d);
        var replyMessageId = d?["message_reference"]?["message_id"]?.ToString()
                             ?? d?["message_reference"]?["id"]?.ToString();

        if (OnGroupAtMessage == null)
        {
            return;
        }

        Console.WriteLine($"[官方事件] t={eventType}, group={groupOpenId}, user={authorId}, content={content}");
        await OnGroupAtMessage.Invoke(new GroupAtMessage(content, groupOpenId, msgId, authorId, imageUrl, replyMessageId));
    }

    private void StartHeartbeatLoop(int heartbeatIntervalMs)
    {
        CancellationTokenSource cts;
        lock (_heartbeatLock)
        {
            _heartbeatCts?.Cancel();
            _heartbeatCts?.Dispose();
            _heartbeatCts = new CancellationTokenSource();
            cts = _heartbeatCts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(heartbeatIntervalMs, cts.Token);
                    var seq = _lastSequence;
                    _ws.Send(JsonConvert.SerializeObject(new { op = 1, d = seq }));
                }
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Bot] 心跳循环异常: {ex.Message}");
            }
        });
    }

    private void SendIdentify()
    {
        if (_intents <= 0)
        {
            throw new InvalidOperationException("Intents 未配置，官方群机器人必须显式传入 33554432。");
        }

        var intents = _intents;
        var identifyOs = GetIdentifyOs();
        var payload = new
        {
            op = 2,
            d = new
            {
                token = $"QQBot {_accessToken}",
                intents,
                shard = new[] { 0, Math.Max(1, _shardCount) },
                properties = new Dictionary<string, string>
                {
                    ["$os"] = identifyOs,
                    ["$browser"] = "bedwarsbot",
                    ["$device"] = "bedwarsbot"
                }
            }
        };

        _ws.Send(JsonConvert.SerializeObject(payload));
        Console.WriteLine($"[Bot] Identify 已发送, intents={intents}, os={identifyOs}, hb={_heartbeatIntervalMs}ms");
    }

    private static string GetIdentifyOs()
    {
        if (OperatingSystem.IsWindows()) return "windows";
        if (OperatingSystem.IsLinux()) return "linux";
        if (OperatingSystem.IsMacOS()) return "mac";
        return "unknown";
    }

    private void StartAccessTokenRefreshLoop()
    {
        _tokenLoopCts?.Cancel();
        _tokenLoopCts?.Dispose();
        _tokenLoopCts = new CancellationTokenSource();
        var token = _tokenLoopCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    var targetRefreshTime = _tokenExpiresAtUtc - TimeSpan.FromSeconds(90);
                    if (targetRefreshTime <= now)
                    {
                        targetRefreshTime = now.AddSeconds(30);
                    }

                    var wait = targetRefreshTime - now;
                    if (wait <= TimeSpan.Zero)
                    {
                        wait = TimeSpan.FromSeconds(30);
                    }

                    await Task.Delay(wait, token);
                    await RefreshAccessToken();
                }
                catch (TaskCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Bot] Token 自动刷新失败: {ex.Message}");
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(20), token);
                    }
                    catch (TaskCanceledException)
                    {
                        return;
                    }
                }
            }
        }, token);
    }

    public async Task SendTextAsync(string openId, string msgId, string content, int? msgSeq = null, bool useEventId = false)
    {
        _ = await SendTextAndGetMessageIdAsync(openId, msgId, content, msgSeq, useEventId);
    }

    public async Task<string?> SendTextAndGetMessageIdAsync(string openId, string msgId, string content, int? msgSeq = null, bool useEventId = false)
    {
        var url = $"https://api.sgroup.qq.com/v2/groups/{openId}/messages";
        var basePayload = new Dictionary<string, object>
        {
            ["content"] = content,
            ["msg_type"] = 0
        };

        var payload = new Dictionary<string, object>(basePayload);
        AppendReferencePayload(payload, msgId, msgSeq, useEventId);

        var (statusCode, body) = await PostJsonAsync(url, payload);
        if (statusCode == HttpStatusCode.OK || statusCode == HttpStatusCode.Created)
        {
            return TryParseMessageId(body);
        }

        if (!string.IsNullOrWhiteSpace(msgId))
        {
            var fallbackPayload = new Dictionary<string, object>(basePayload);
            AppendReferencePayload(fallbackPayload, msgId, msgSeq, !useEventId);

            var (retryStatusCode, retryBody) = await PostJsonAsync(url, fallbackPayload);
            if (retryStatusCode == HttpStatusCode.OK || retryStatusCode == HttpStatusCode.Created)
            {
                return TryParseMessageId(retryBody);
            }

            Console.WriteLine($"[Bot] 发送消息失败(重试后): {(int)retryStatusCode} {retryStatusCode}, body={retryBody}");
            return null;
        }

        Console.WriteLine($"[Bot] 发送消息失败: {(int)statusCode} {statusCode}, body={body}");
        return null;
    }

    public async Task SendImageAsync(string openId, string msgId, Stream img, int? msgSeq = null, string? caption = null, bool useEventId = false)
    {
        _ = await SendImageAndGetMessageIdAsync(openId, msgId, img, msgSeq, caption, useEventId);
    }

    public async Task<string?> SendImageAndGetMessageIdAsync(string openId, string msgId, Stream img, int? msgSeq = null, string? caption = null, bool useEventId = false)
    {
        if (!_imageHostUploader.IsEnabled)
        {
            await SendTextAsync(openId, msgId, "未配置图床，无法发送图片。请在 pz/config.json 配置 ImageHost。", msgSeq, useEventId);
            return null;
        }

        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            if (img.CanSeek) img.Position = 0;
            await img.CopyToAsync(ms);
            bytes = ms.ToArray();
        }

        var uploadResult = await _imageHostUploader.UploadAsync(bytes, $"bw_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.jpg");
        if (!uploadResult.Success || string.IsNullOrWhiteSpace(uploadResult.Url))
        {
            await SendTextAsync(openId, msgId, uploadResult.Message, msgSeq, useEventId);
            return null;
        }

        var fileInfoResult = await UploadGroupFileAsync(openId, uploadResult.Url);
        if (!fileInfoResult.Success || string.IsNullOrWhiteSpace(fileInfoResult.FileInfo))
        {
            await SendTextAsync(openId, msgId, $"上传群文件失败，改为发送链接: {uploadResult.Url}", msgSeq, useEventId);
            return null;
        }

        var sentMessageId = await SendRichMediaMessageAndGetMessageIdAsync(openId, msgId, fileInfoResult.FileInfo, msgSeq, caption, useEventId);
        if (sentMessageId == null)
        {
            await SendTextAsync(openId, msgId, $"图片富媒体发送失败，链接: {uploadResult.Url}", msgSeq, useEventId);
            return null;
        }

        return sentMessageId;
    }

    private async Task RefreshAccessToken()
    {
        await _tokenRefreshLock.WaitAsync();
        try
        {
            var json = JsonConvert.SerializeObject(new { appId = _appId, clientSecret = _clientSecret });
            var resp = await _http.PostAsync(
                "https://bots.qq.com/app/getAppAccessToken",
                new StringContent(json, Encoding.UTF8, "application/json"));
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"获取 token 失败: {(int)resp.StatusCode} {resp.StatusCode}, body={body}");
            }

            var obj = JObject.Parse(body);
            var accessToken = obj["access_token"]?.ToString();
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                throw new InvalidOperationException($"获取 token 成功但返回空 access_token, body={body}");
            }

            var expiresInSec = ParseExpiresInSeconds(obj);
            _accessToken = accessToken;
            _tokenExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(expiresInSec);
            Console.WriteLine($"[Bot] Token 刷新成功，expires_in={expiresInSec}s");
        }
        finally
        {
            _tokenRefreshLock.Release();
        }
    }

    private static int ParseExpiresInSeconds(JObject tokenObj)
    {
        var token = tokenObj["expires_in"];
        if (token == null || token.Type == JTokenType.Null)
        {
            return 600;
        }

        if (token.Type == JTokenType.Integer)
        {
            return Math.Max(120, token.Value<int>());
        }

        if (int.TryParse(token.ToString(), out var parsed))
        {
            return Math.Max(120, parsed);
        }

        return 600;
    }

    private async Task<string> GetGateway()
    {
        foreach (var endpoint in new[] { "https://api.sgroup.qq.com/gateway/bot", "https://api.sgroup.qq.com/gateway" })
        {
            var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("QQBot", _accessToken);
            req.Headers.TryAddWithoutValidation("X-Union-Appid", _appId);

            var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"[Bot] 获取网关失败: {(int)resp.StatusCode} {resp.StatusCode}, endpoint={endpoint}, body={body}");
                continue;
            }

            try
            {
                var obj = JObject.Parse(body);
                var url = obj["url"]?.ToString();
                if (!string.IsNullOrWhiteSpace(url))
                {
                    var shards = obj["shards"]?.Value<int?>();
                    if (shards.HasValue && shards.Value > 0)
                    {
                        _shardCount = shards.Value;
                    }

                    Console.WriteLine($"[Bot] 网关就绪: endpoint={endpoint}, shards={_shardCount}");
                    return url;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Bot] 解析网关响应失败: endpoint={endpoint}, ex={ex.Message}, body={body}");
            }
        }

        throw new InvalidOperationException("无法获取可用网关地址。");
    }

    private async Task<(HttpStatusCode StatusCode, string Body)> PostJsonAsync(string url, Dictionary<string, object> payload)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("QQBot", _accessToken);
        req.Headers.TryAddWithoutValidation("X-Union-Appid", _appId);

        var resp = await _http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, body);
    }

    private async Task<(bool Success, string? FileInfo)> UploadGroupFileAsync(string openId, string imageUrl)
    {
        var url = $"https://api.sgroup.qq.com/v2/groups/{openId}/files";
        var payload = new Dictionary<string, object>
        {
            ["file_type"] = 1,
            ["url"] = imageUrl,
            ["srv_send_msg"] = false
        };

        var (statusCode, body) = await PostJsonAsync(url, payload);
        if (statusCode != HttpStatusCode.OK && statusCode != HttpStatusCode.Created)
        {
            Console.WriteLine($"[Bot] 上传群文件失败: {(int)statusCode} {statusCode}, body={body}");
            return (false, null);
        }

        try
        {
            var obj = JObject.Parse(body);
            var fileInfo = obj.SelectToken("data.file_info")?.ToString()
                           ?? obj.SelectToken("file_info")?.ToString();
            return string.IsNullOrWhiteSpace(fileInfo) ? (false, null) : (true, fileInfo);
        }
        catch
        {
            return (false, null);
        }
    }

    private async Task<string?> SendRichMediaMessageAndGetMessageIdAsync(string openId, string msgId, string fileInfo, int? msgSeq, string? caption, bool useEventId)
    {
        var url = $"https://api.sgroup.qq.com/v2/groups/{openId}/messages";
        var content = string.IsNullOrWhiteSpace(caption) ? " " : caption;
        var payload = new Dictionary<string, object>
        {
            ["content"] = content,
            ["msg_type"] = 7,
            ["media"] = new Dictionary<string, object> { ["file_info"] = fileInfo }
        };
        AppendReferencePayload(payload, msgId, msgSeq, useEventId);

        var (statusCode, body) = await PostJsonAsync(url, payload);
        if (statusCode == HttpStatusCode.OK || statusCode == HttpStatusCode.Created)
        {
            return TryParseMessageId(body) ?? string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(msgId))
        {
            var fallback = new Dictionary<string, object>
            {
                ["content"] = content,
                ["msg_type"] = 7,
                ["media"] = new Dictionary<string, object> { ["file_info"] = fileInfo }
            };
            AppendReferencePayload(fallback, msgId, msgSeq, !useEventId);

            var (retryStatusCode, retryBody) = await PostJsonAsync(url, fallback);
            if (retryStatusCode == HttpStatusCode.OK || retryStatusCode == HttpStatusCode.Created)
            {
                return TryParseMessageId(retryBody) ?? string.Empty;
            }

            Console.WriteLine($"[Bot] 富媒体发送失败(重试后): {(int)retryStatusCode} {retryStatusCode}, body={retryBody}");
            return null;
        }

        Console.WriteLine($"[Bot] 富媒体发送失败: {(int)statusCode} {statusCode}, body={body}");
        return null;
    }

    private static void AppendReferencePayload(Dictionary<string, object> payload, string msgId, int? msgSeq, bool useEventId)
    {
        if (string.IsNullOrWhiteSpace(msgId))
        {
            return;
        }

        payload[useEventId ? "event_id" : "msg_id"] = msgId;
        if (msgSeq.HasValue)
        {
            payload["msg_seq"] = msgSeq.Value;
        }
    }

    private static int? TryParseErrCode(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            var obj = JObject.Parse(body);
            var token = obj["err_code"] ?? obj["code"];
            if (token == null) return null;
            if (token.Type == JTokenType.Integer) return token.Value<int>();
            return int.TryParse(token.ToString(), out var parsed) ? parsed : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryParseMessageId(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            var obj = JObject.Parse(body);
            return obj["id"]?.ToString()
                   ?? obj.SelectToken("data.id")?.ToString()
                   ?? obj["message_id"]?.ToString()
                   ?? obj.SelectToken("data.message_id")?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractImageUrl(JToken? payload)
    {
        if (payload?["attachments"] is not JArray attachments) return null;

        foreach (var item in attachments)
        {
            var url = item?["url"]?.ToString();
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }
        }

        return null;
    }
}

public readonly record struct GroupAtMessage(
    string Content,
    string GroupOpenId,
    string MessageId,
    string AuthorId,
    string? ImageUrl,
    string? ReplyMessageId);
