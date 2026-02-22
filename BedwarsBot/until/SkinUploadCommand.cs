using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace BedwarsBot;

public sealed class SkinUploadCommand
{
    private static readonly TimeSpan PendingUploadTimeout = TimeSpan.FromSeconds(60);
    private readonly InfoPhotoService _infoPhotoService;
    private readonly BindService _bindService;
    private readonly object _lock = new();
    private readonly Dictionary<string, PendingSkinUpload> _pending = new(StringComparer.Ordinal);

    public SkinUploadCommand(InfoPhotoService infoPhotoService, BindService bindService)
    {
        _infoPhotoService = infoPhotoService;
        _bindService = bindService;
    }

    public SkinUploadCommandResult BeginUpload(string qq, string groupId)
    {
        if (!_bindService.TryGetBindingByQq(qq, out _))
        {
            return SkinUploadCommandResult.Handled("❌ 你还没有绑定布吉岛账号，请先执行 !bind <布吉岛用户名>");
        }

        lock (_lock)
        {
            _pending[qq] = new PendingSkinUpload(groupId, DateTimeOffset.UtcNow);
        }

        return SkinUploadCommandResult.Handled("🧩 请发送 MC 皮肤源文件（PNG），收到后将自动提取头像。");
    }

    public async Task<SkinUploadCommandResult> TryHandlePendingAsync(JObject json, string? groupId, string? userId)
    {
        var payload = ExtractImagePayload(json);
        return await TryHandlePendingAsync(groupId, userId, payload);
    }

    public async Task<SkinUploadCommandResult> TryHandlePendingAsync(string? groupId, string? userId, ImagePayload? payload)
    {
        if (string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(userId))
        {
            return SkinUploadCommandResult.NotHandled();
        }

        PendingSkinUpload? pending;
        lock (_lock)
        {
            if (!_pending.TryGetValue(userId, out pending))
            {
                return SkinUploadCommandResult.NotHandled();
            }
        }

        if (pending == null)
        {
            return SkinUploadCommandResult.NotHandled();
        }

        if (!string.Equals(pending.GroupId, groupId, StringComparison.Ordinal))
        {
            return SkinUploadCommandResult.NotHandled();
        }

        if (payload == null)
        {
            if (DateTimeOffset.UtcNow - pending.CreatedAtUtc < PendingUploadTimeout)
            {
                return SkinUploadCommandResult.NotHandled();
            }

            lock (_lock)
            {
                _pending.Remove(userId);
            }

            return SkinUploadCommandResult.Handled("⌛ 皮肤上传已超时（60秒），请重新发送 /skin up 后再上传皮肤源文件。");
        }

        var result = await _infoPhotoService.AddSkinFromUploadAsync(userId, payload.Value, _bindService);
        if (result.Success)
        {
            lock (_lock)
            {
                _pending.Remove(userId);
            }
        }

        return SkinUploadCommandResult.Handled(result.Message);
    }

    private static ImagePayload? ExtractImagePayload(JObject json)
    {
        if (json["message"] is JArray arr)
        {
            foreach (var seg in arr)
            {
                var type = seg?["type"]?.ToString();
                if (!string.Equals(type, "image", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(type, "file", StringComparison.OrdinalIgnoreCase))
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
        if (string.IsNullOrWhiteSpace(rawMessage))
        {
            return null;
        }

        var match = Regex.Match(rawMessage, @"\[CQ:image,(?<params>[^\]]+)\]", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            match = Regex.Match(rawMessage, @"\[CQ:file,(?<params>[^\]]+)\]", RegexOptions.IgnoreCase);
        }
        if (!match.Success)
        {
            return null;
        }

        string? url = null;
        string? file = null;
        string? path = null;
        string? base64 = null;

        var parts = match.Groups["params"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var idx = part.IndexOf('=');
            if (idx <= 0)
            {
                continue;
            }

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
                case "base64":
                    base64 = value;
                    break;
            }
        }

        if (url == null && file == null && path == null && base64 == null)
        {
            return null;
        }

        return new ImagePayload(url, file, path, base64);
    }

    private sealed record PendingSkinUpload(string GroupId, DateTimeOffset CreatedAtUtc);
}

public readonly struct SkinUploadCommandResult
{
    public bool IsHandled { get; }
    public string? Message { get; }

    private SkinUploadCommandResult(bool isHandled, string? message)
    {
        IsHandled = isHandled;
        Message = message;
    }

    public static SkinUploadCommandResult Handled(string message) => new(true, message);
    public static SkinUploadCommandResult NotHandled() => new(false, null);
}
