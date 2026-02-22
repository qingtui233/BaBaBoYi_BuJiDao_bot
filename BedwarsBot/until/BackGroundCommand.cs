using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace BedwarsBot;

public sealed class BackGroundCommand
{
    private static readonly TimeSpan PendingUploadTimeout = TimeSpan.FromSeconds(30);
    private readonly BackGround _backgroundService;
    private readonly BindService _bindService;
    private readonly object _lock = new();
    private readonly Dictionary<string, PendingBackgroundUpload> _pending = new(StringComparer.Ordinal);

    public BackGroundCommand(BackGround backgroundService, BindService bindService)
    {
        _backgroundService = backgroundService;
        _bindService = bindService;
    }

    public BackgroundCommandResult BeginUpload(string qq, string groupId)
    {
        if (!_bindService.TryGetBindingByQq(qq, out var binding))
        {
            return BackgroundCommandResult.Handled("❌ 你还没有绑定布吉岛账号，请先执行 !bind <布吉岛用户名>");
        }

        lock (_lock)
        {
            _pending[qq] = new PendingBackgroundUpload(groupId, binding, DateTimeOffset.UtcNow);
        }

        return BackgroundCommandResult.Handled("🖼️ 请发送背景图片卡片，发送后将自动保存。");
    }

    public async Task<BackgroundCommandResult> TryHandlePendingAsync(JObject json, string? groupId, string? userId)
    {
        if (string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(userId))
        {
            return BackgroundCommandResult.NotHandled();
        }

        PendingBackgroundUpload pending;
        lock (_lock)
        {
            if (!_pending.TryGetValue(userId, out pending))
            {
                return BackgroundCommandResult.NotHandled();
            }
        }

        var payload = ExtractImagePayload(json);
        if (payload == null)
        {
            if (DateTimeOffset.UtcNow - pending.CreatedAtUtc < PendingUploadTimeout)
            {
                return BackgroundCommandResult.NotHandled();
            }

            lock (_lock)
            {
                _pending.Remove(userId);
            }

            return BackgroundCommandResult.Handled("⌛ 背景上传已超时（30秒），请重新发送 /bg 后再上传图片。");
        }

        var saveResult = await _backgroundService.SaveBackgroundAsync(pending.Binding.BjdUuid, payload.Value);
        if (saveResult.Success)
        {
            lock (_lock)
            {
                _pending.Remove(userId);
            }
        }

        return BackgroundCommandResult.Handled(saveResult.Message);
    }

    public async Task<BackgroundCommandResult> TryHandlePendingAsync(string? groupId, string? userId, ImagePayload? payload)
    {
        if (string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(userId))
        {
            return BackgroundCommandResult.NotHandled();
        }

        PendingBackgroundUpload pending;
        lock (_lock)
        {
            if (!_pending.TryGetValue(userId, out pending))
            {
                return BackgroundCommandResult.NotHandled();
            }
        }

        if (payload == null)
        {
            if (DateTimeOffset.UtcNow - pending.CreatedAtUtc < PendingUploadTimeout)
            {
                return BackgroundCommandResult.NotHandled();
            }

            lock (_lock)
            {
                _pending.Remove(userId);
            }

            return BackgroundCommandResult.Handled("⌛ 背景上传已超时（30秒），请重新发送 /bg 后再上传图片。");
        }

        var saveResult = await _backgroundService.SaveBackgroundAsync(pending.Binding.BjdUuid, payload.Value);
        if (saveResult.Success)
        {
            lock (_lock)
            {
                _pending.Remove(userId);
            }
        }

        return BackgroundCommandResult.Handled(saveResult.Message);
    }

    private static ImagePayload? ExtractImagePayload(JObject json)
    {
        if (json["message"] is JArray arr)
        {
            foreach (var seg in arr)
            {
                if (!string.Equals(seg?["type"]?.ToString(), "image", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var data = seg?["data"] as JObject;
                return new ImagePayload(
                    data?["url"]?.ToString(),
                    data?["file"]?.ToString(),
                    data?["path"]?.ToString(),
                    data?["base64"]?.ToString());
            }
        }

        var rawMessage = json["raw_message"]?.ToString();
        if (string.IsNullOrWhiteSpace(rawMessage)) return null;

        var match = Regex.Match(rawMessage, @"\[CQ:image,(?<params>[^\]]+)\]");
        if (!match.Success) return null;

        string? url = null;
        string? file = null;
        string? path = null;

        var parts = match.Groups["params"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var idx = part.IndexOf('=');
            if (idx <= 0) continue;
            var key = part[..idx];
            var value = part[(idx + 1)..];
            switch (key)
            {
                case "url":
                    url = value;
                    break;
                case "file":
                    file = value;
                    break;
                case "path":
                    path = value;
                    break;
            }
        }

        if (url == null && file == null && path == null) return null;
        return new ImagePayload(url, file, path, null);
    }

    private sealed record PendingBackgroundUpload(string GroupId, QqBinding Binding, DateTimeOffset CreatedAtUtc);
}

public readonly struct BackgroundCommandResult
{
    public bool IsHandled { get; }
    public string? Message { get; }

    private BackgroundCommandResult(bool isHandled, string? message)
    {
        IsHandled = isHandled;
        Message = message;
    }

    public static BackgroundCommandResult Handled(string message) => new(true, message);
    public static BackgroundCommandResult NotHandled() => new(false, null);
}
