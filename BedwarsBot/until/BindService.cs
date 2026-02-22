using Newtonsoft.Json.Linq;

namespace BedwarsBot;

public class BindService
{
    private readonly BotDataStore _store;

    public BindService(BotDataStore store)
    {
        _store = store;
    }

    public async Task<BindResult> BindAsync(string qq, string playerName, Func<string, Task<ApiCallResult>> requestPlayerAsync)
    {
        var apiResult = await requestPlayerAsync(playerName);
        if (!apiResult.Success)
        {
            return BindResult.Fail($"❌ 绑定失败: {apiResult.ErrorMessage}");
        }

        var playerInfo = ExtractPlayerFromPlayerApi(apiResult.JsonResponse!, playerName);
        if (string.IsNullOrWhiteSpace(playerInfo.Uuid))
        {
            return BindResult.Fail("❌ 绑定失败: API 未返回 UUID，无法建立绑定。");
        }

        _store.UpsertQqBinding(qq, playerInfo.Name, playerInfo.Uuid);
        return BindResult.Ok($"✅ 绑定成功: QQ({qq}) -> {playerInfo.Name} / {playerInfo.Uuid}", playerInfo);
    }

    public bool TryGetBindingByQq(string qq, out QqBinding binding)
    {
        return _store.TryGetQqBinding(qq, out binding!);
    }

    public PlayerInfo ExtractPlayerInfo(string jsonResponse, string fallbackName)
    {
        try
        {
            var obj = JObject.Parse(jsonResponse);

            var uuid = obj.SelectToken("data.uuid")?.ToString()
                       ?? obj.SelectToken("result.uuid")?.ToString()
                       ?? obj.SelectToken("uuid")?.ToString()
                       ?? string.Empty;

            var name = obj.SelectToken("data.name")?.ToString()
                       ?? obj.SelectToken("result.name")?.ToString()
                       ?? obj.SelectToken("name")?.ToString()
                       ?? fallbackName;

            return new PlayerInfo(name, uuid);
        }
        catch
        {
            return new PlayerInfo(fallbackName, string.Empty);
        }
    }

    public PlayerInfo ExtractPlayerFromPlayerApi(string jsonResponse, string fallbackName)
    {
        try
        {
            var obj = JObject.Parse(jsonResponse);
            var uuid = obj.SelectToken("data.uuid")?.ToString() ?? string.Empty;
            var name = obj.SelectToken("data.playername")?.ToString()
                       ?? obj.SelectToken("data.name")?.ToString()
                       ?? fallbackName;
            return new PlayerInfo(name, uuid);
        }
        catch
        {
            return new PlayerInfo(fallbackName, string.Empty);
        }
    }
}

public readonly record struct ApiCallResult(bool Success, string? JsonResponse, string? ErrorMessage)
{
    public static ApiCallResult Ok(string json) => new(true, json, null);
    public static ApiCallResult Fail(string error) => new(false, null, error);
}

public readonly record struct PlayerInfo(string Name, string Uuid);

public readonly record struct BindResult(bool Success, string Message, PlayerInfo PlayerInfo)
{
    public static BindResult Ok(string message, PlayerInfo playerInfo) => new(true, message, playerInfo);
    public static BindResult Fail(string message) => new(false, message, new PlayerInfo(string.Empty, string.Empty));
}
