using System.Collections.Concurrent;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Websocket.Client;

namespace BedwarsBot;

public sealed class NapcatBot
{
    private readonly string _wsUrl;
    private readonly string _accessToken;
    private WebsocketClient? _ws;
    private int _echo;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JObject>> _pending =
        new(StringComparer.Ordinal);

    public event Func<NapcatGroupMessage, Task>? OnGroupMessage;
    public event Func<NapcatPrivateMessage, Task>? OnPrivateMessage;
    public event Func<JObject, Task>? OnRawEvent;

    public NapcatBot(string wsUrl, string accessToken)
    {
        _wsUrl = wsUrl;
        _accessToken = accessToken;
    }

    public async Task StartAsync()
    {
        var wsUri = BuildWebSocketUri(_wsUrl, _accessToken);
        _ws = new WebsocketClient(wsUri);
        _ws.MessageReceived.Subscribe(msg => Task.Run(() => HandleMessage(msg.Text)));
        await _ws.Start();
        Console.WriteLine($"[NapCat] 已连接: {wsUri}");
    }

    public Task SendTextAsync(string groupId, string content)
    {
        _ = SendTextAndGetMessageIdAsync(groupId, content);
        return Task.CompletedTask;
    }

    public async Task<string?> SendTextAndGetMessageIdAsync(string groupId, string content)
    {
        if (_ws == null) return null;

        var result = await SendActionAsync("send_group_msg", new
        {
            group_id = ParseGroupId(groupId),
            message = content,
            auto_escape = false
        }, TimeSpan.FromSeconds(8));

        return result?["data"]?["message_id"]?.ToString()
               ?? result?["message_id"]?.ToString();
    }

    public Task SendPrivateTextAsync(string userId, string content)
    {
        if (_ws == null) return Task.CompletedTask;

        var payload = new
        {
            action = "send_private_msg",
            @params = new
            {
                user_id = ParseUserId(userId),
                message = content,
                auto_escape = false
            },
            echo = NextEcho()
        };

        _ws.Send(JsonConvert.SerializeObject(payload));
        return Task.CompletedTask;
    }

    public async Task SendImageAsync(string groupId, Stream imageStream, string? caption = null)
    {
        _ = await SendImageAndGetMessageIdAsync(groupId, imageStream, caption);
    }

    public async Task<string?> SendImageAndGetMessageIdAsync(string groupId, Stream imageStream, string? caption = null)
    {
        using var ms = new MemoryStream();
        if (imageStream.CanSeek) imageStream.Position = 0;
        await imageStream.CopyToAsync(ms);

        var base64 = Convert.ToBase64String(ms.ToArray());
        var cq = $"[CQ:image,file=base64://{base64}]";
        if (!string.IsNullOrWhiteSpace(caption))
        {
            cq += caption;
        }
        return await SendTextAndGetMessageIdAsync(groupId, cq);
    }

    public async Task SendPrivateImageAsync(string userId, Stream imageStream, string? caption = null)
    {
        using var ms = new MemoryStream();
        if (imageStream.CanSeek) imageStream.Position = 0;
        await imageStream.CopyToAsync(ms);

        var base64 = Convert.ToBase64String(ms.ToArray());
        var cq = $"[CQ:image,file=base64://{base64}]";
        if (!string.IsNullOrWhiteSpace(caption))
        {
            cq += caption;
        }
        await SendPrivateTextAsync(userId, cq);
    }

    public async Task<IReadOnlyList<string>> GetGroupIdsAsync(TimeSpan? timeout = null)
    {
        var result = await SendActionAsync("get_group_list", new { }, timeout ?? TimeSpan.FromSeconds(8));
        if (result == null) return Array.Empty<string>();

        var list = result["data"] as JArray;
        if (list == null || list.Count == 0) return Array.Empty<string>();

        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in list)
        {
            var id = item?["group_id"]?.ToString();
            if (!string.IsNullOrWhiteSpace(id))
            {
                groups.Add(id);
            }
        }

        return groups.ToList();
    }

    public async Task<bool> ApproveFriendRequestAsync(string flag)
    {
        if (string.IsNullOrWhiteSpace(flag)) return false;
        var result = await SendActionAsync("set_friend_add_request", new
        {
            flag,
            approve = true,
            remark = ""
        }, TimeSpan.FromSeconds(8));
        return IsActionOk(result);
    }

    public async Task<bool> ApproveGroupInviteAsync(string flag)
    {
        if (string.IsNullOrWhiteSpace(flag)) return false;
        var result = await SendActionAsync("set_group_add_request", new
        {
            flag,
            sub_type = "invite",
            approve = true,
            reason = ""
        }, TimeSpan.FromSeconds(8));
        return IsActionOk(result);
    }

    private async Task<JObject?> SendActionAsync(string action, object parameters, TimeSpan timeout)
    {
        if (_ws == null) return null;

        var echo = NextEcho();
        var tcs = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[echo] = tcs;

        var payload = new
        {
            action,
            @params = parameters,
            echo
        };
        _ws.Send(JsonConvert.SerializeObject(payload));

        using var cts = new CancellationTokenSource(timeout);
        await using var registration = cts.Token.Register(() => tcs.TrySetCanceled());

        try
        {
            return await tcs.Task;
        }
        catch
        {
            return null;
        }
        finally
        {
            _pending.TryRemove(echo, out _);
        }
    }

    private async Task HandleMessage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        JObject json;
        try
        {
            json = JObject.Parse(text);
        }
        catch
        {
            return;
        }

        if (OnRawEvent != null)
        {
            await OnRawEvent.Invoke(json);
        }

        var echo = json["echo"]?.ToString();
        if (!string.IsNullOrWhiteSpace(echo) && _pending.TryRemove(echo, out var tcs))
        {
            tcs.TrySetResult(json);
            return;
        }

        if (!string.Equals(json["post_type"]?.ToString(), "message", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var messageType = json["message_type"]?.ToString() ?? string.Empty;
        var userId = json["user_id"]?.ToString() ?? string.Empty;
        var messageId = json["message_id"]?.ToString() ?? string.Empty;
        var content = json["raw_message"]?.ToString() ?? string.Empty;

        if (string.Equals(messageType, "group", StringComparison.OrdinalIgnoreCase))
        {
            var groupId = json["group_id"]?.ToString() ?? string.Empty;

            if (OnGroupMessage != null)
            {
                await OnGroupMessage.Invoke(new NapcatGroupMessage(content, groupId, messageId, userId, json));
            }
            return;
        }

        if (string.Equals(messageType, "private", StringComparison.OrdinalIgnoreCase))
        {
            if (OnPrivateMessage != null)
            {
                await OnPrivateMessage.Invoke(new NapcatPrivateMessage(content, userId, messageId, json));
            }
        }
    }

    private static Uri BuildWebSocketUri(string wsUrl, string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new Uri(wsUrl);
        }

        var separator = wsUrl.Contains('?') ? '&' : '?';
        return new Uri($"{wsUrl}{separator}access_token={Uri.EscapeDataString(accessToken)}");
    }

    private static long ParseGroupId(string groupId)
    {
        return long.TryParse(groupId, out var id) ? id : 0;
    }

    private static long ParseUserId(string userId)
    {
        return long.TryParse(userId, out var id) ? id : 0;
    }

    private string NextEcho()
    {
        return Interlocked.Increment(ref _echo).ToString();
    }

    private static bool IsActionOk(JObject? result)
    {
        if (result == null) return false;
        var status = result["status"]?.ToString();
        if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)) return true;
        var retCode = result["retcode"]?.Value<int?>() ?? -1;
        return retCode == 0;
    }
}

public readonly record struct NapcatGroupMessage(
    string Content,
    string GroupId,
    string MessageId,
    string UserId,
    JObject Raw);

public readonly record struct NapcatPrivateMessage(
    string Content,
    string UserId,
    string MessageId,
    JObject Raw);
