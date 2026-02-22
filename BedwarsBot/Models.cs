using Newtonsoft.Json;

namespace BedwarsBot;

// 1. API 顶层结构
public class ApiResponse
{
    [JsonProperty("code")]
    public int Code { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; }

    [JsonProperty("data")]
    public PlayerData Data { get; set; }
}

// 2. 玩家数据
public class PlayerData
{
    [JsonProperty("uuid")]
    public string? Uuid { get; set; }

    [JsonProperty("playername")]
    public string? PlayerName { get; set; }

    [JsonProperty("username")]
    public string? Username { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; }

    [JsonProperty("banned")]
    public bool IsBanned { get; set; }

    // 总数据 (API直接提供的)
    [JsonProperty("total_game")]
    public int TotalGame { get; set; }
    
    [JsonProperty("total_win")]
    public int TotalWin { get; set; }
    
    [JsonProperty("total_fk")]
    public int TotalFinalKills { get; set; }

    [JsonProperty("total_kills")]
    public int TotalKills { get; set; }

    [JsonProperty("total_deaths")]
    public int TotalDeaths { get; set; }
    
    [JsonProperty("total_bed_destroy")]
    public int TotalBedDestroy { get; set; }

    // 详细模式数据 (bw1, bw16...)
    [JsonProperty("bedwars")]
    public Dictionary<string, BedwarsModeStats> BedwarsModes { get; set; }
}

// 3. 单个模式详情
public class BedwarsModeStats
{
    [JsonProperty("game")]
    public int Game { get; set; }

    [JsonProperty("win")]
    public int Win { get; set; }

    [JsonProperty("lose")]
    public int Lose { get; set; }

    [JsonProperty("final_kills")]
    public int FinalKills { get; set; }

    [JsonProperty("final_deaths")]
    public int FinalDeaths { get; set; }

    [JsonProperty("deaths")]
    public int Deaths { get; set; } // 普通死亡

    [JsonProperty("kills")]
    public int Kills { get; set; } // 普通击杀

    [JsonProperty("bed_destory")]
    public int BedDestroy { get; set; }

    [JsonProperty("bed_lose")]
    public int BedLose { get; set; }

    [JsonProperty("use_item")]
    public Dictionary<string, int>? UseItem { get; set; }

    [JsonProperty("upgrade")]
    public Dictionary<string, int>? Upgrade { get; set; }
}
