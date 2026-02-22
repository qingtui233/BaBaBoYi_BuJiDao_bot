using Newtonsoft.Json.Linq;

namespace BedwarsBot;

public sealed class ImageHostUploader
{
    private readonly ImageHostConfig _config;
    private readonly HttpClient _http;

    public ImageHostUploader(ImageHostConfig? config, HttpClient httpClient)
    {
        _config = config ?? new ImageHostConfig();
        _http = httpClient;
    }

    public bool IsEnabled
    {
        get
        {
            var provider = (_config.Provider ?? string.Empty).Trim().ToLowerInvariant();
            if (provider == "smms") return !string.IsNullOrWhiteSpace(_config.Token);
            if (provider == "custom") return !string.IsNullOrWhiteSpace(_config.UploadUrl);
            return false;
        }
    }

    public async Task<(bool Success, string? Url, string Message)> UploadAsync(byte[] bytes, string fileName)
    {
        var provider = (_config.Provider ?? string.Empty).Trim().ToLowerInvariant();
        if (provider == "smms")
        {
            return await UploadSmmsAsync(bytes, fileName);
        }

        if (provider == "custom")
        {
            return await UploadCustomAsync(bytes, fileName);
        }

        return (false, null, "未配置图床，无法发送图片。请在 pz/config.json 配置 ImageHost。");
    }

    private async Task<(bool Success, string? Url, string Message)> UploadSmmsAsync(byte[] bytes, string fileName)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://sm.ms/api/v2/upload");
        req.Headers.TryAddWithoutValidation("Authorization", _config.Token?.Trim());

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        form.Add(fileContent, "smfile", string.IsNullOrWhiteSpace(fileName) ? "bw.png" : fileName);
        req.Content = form;

        using var resp = await _http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            return (false, null, $"图床上传失败: {(int)resp.StatusCode}, body={TrimBody(body)}");
        }

        try
        {
            var obj = JObject.Parse(body);
            var success = obj["success"]?.Value<bool>() ?? false;
            var url = obj.SelectToken("data.url")?.ToString();
            if (success && !string.IsNullOrWhiteSpace(url))
            {
                return (true, url, "ok");
            }

            var msg = obj["message"]?.ToString() ?? "图床返回异常";
            return (false, null, $"图床上传失败: {msg}");
        }
        catch
        {
            return (false, null, "图床返回解析失败");
        }
    }

    private async Task<(bool Success, string? Url, string Message)> UploadCustomAsync(byte[] bytes, string fileName)
    {
        if (string.IsNullOrWhiteSpace(_config.UploadUrl))
        {
            return (false, null, "自定义图床 UploadUrl 为空");
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, _config.UploadUrl);
        if (!string.IsNullOrWhiteSpace(_config.AuthHeaderName) && !string.IsNullOrWhiteSpace(_config.Token))
        {
            var value = $"{_config.AuthHeaderValuePrefix}{_config.Token}".Trim();
            req.Headers.TryAddWithoutValidation(_config.AuthHeaderName, value);
        }

        using var form = new MultipartFormDataContent();
        var fieldName = string.IsNullOrWhiteSpace(_config.FileFieldName) ? "file" : _config.FileFieldName;
        var fileContent = new ByteArrayContent(bytes);
        form.Add(fileContent, fieldName, string.IsNullOrWhiteSpace(fileName) ? "bw.png" : fileName);
        req.Content = form;

        using var resp = await _http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            return (false, null, $"图床上传失败: {(int)resp.StatusCode}, body={TrimBody(body)}");
        }

        try
        {
            var obj = JObject.Parse(body);
            if (!string.IsNullOrWhiteSpace(_config.SuccessJsonPath))
            {
                var successToken = obj.SelectToken(_config.SuccessJsonPath)?.ToString();
                var expected = _config.SuccessExpected ?? "true";
                if (!string.Equals(successToken, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return (false, null, "图床返回状态为失败");
                }
            }

            var urlPath = string.IsNullOrWhiteSpace(_config.UrlJsonPath) ? "data.url" : _config.UrlJsonPath;
            var url = obj.SelectToken(urlPath)?.ToString();
            if (string.IsNullOrWhiteSpace(url))
            {
                return (false, null, "图床返回中未找到图片 URL");
            }

            return (true, url, "ok");
        }
        catch
        {
            return (false, null, "图床返回解析失败");
        }
    }

    private static string TrimBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "<empty>";
        var text = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 300 ? text : text[..300];
    }
}
