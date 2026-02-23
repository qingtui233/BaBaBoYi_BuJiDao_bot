using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Globalization;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace BedwarsBot;

class Program
{
    // 布吉岛 API v2
    private static string _apiUrl = "https://api.mcbjd.net/v2/gamestats";
    private static string _leaderboardApiUrl = "https://api.mcbjd.net/v2/leaderboard";
    private static string _playerApiUrl = "https://api.mcbjd.net/v2/player";
    private static string _apiKey = string.Empty;
    private static List<string> _apiKeys = new();
    private static readonly object _apiKeyLock = new();
    private static int _apiKeyCursor;
    private static string _gameType = "bedwars";
    private static string _adminQq = string.Empty;
    private static readonly Regex AtPrefixRegex = new(@"^<@!?[0-9A-Za-z]+>\s*", RegexOptions.Compiled);
    private static readonly Regex CqAtPrefixRegex = new(@"^\[CQ:at,[^\]]+\]\s*", RegexOptions.Compiled);
    private static readonly Regex CqReplyRegex = new(@"\[CQ:reply,[^\]]*id=(?<id>\d+)[^\]]*\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CqCodeRegex = new(@"\[CQ:[^\]]+\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ShoutTimeRegex = new(
        @"^(?<m>\d{1,2})(月|[./])(?<d>\d{1,2})日?(?<h>\d{1,2})(点|[.:])(?<min>\d{1,2})$",
        RegexOptions.Compiled);
    private static readonly HashSet<string> BwModeAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "all", "total", "overall", "总览", "全部",
        "solo", "1s", "bw1", "单八",
        "duo", "double", "doubles", "2s", "双八", "bw8",
        "4s", "squad", "bw16", "44", "46", "64",
        "xp", "xp32", "48", "xp64", "xp8x4",
        "bwxp32", "bwxp64", "bwxp8x4",
        "bw999"
    };

    private static BedwarsService? _bwService;
    private static SkaywarsService? _swService;
    private static LeaderboardRankings? _lbService;
    private static ShoutLogService? _shoutLogService;
    private static HelpService? _helpService;
    private static SessionService? _sessionService;
    private static BwHistorySnapshotStore? _bwHistoryStore;
    private static LeaderboardDailySnapshotStore? _leaderboardSnapshotStore;
    private static QQBotV2 _qqBot;
    private static NapcatBot? _napcatBot;
    private static NapcatAutoApproveAndWelcome? _napcatAuto;
    private static OfficialWebhookServer? _officialWebhookServer;
    private static WebApplication? _aspNetWebhookApp;
    private static BotConfig? _botConfig;
    private static readonly HttpClient _httpClient = new HttpClient();
    private static BotDataStore _dataStore;
    private static BindService _bindService;
    private static InfoPhotoService _infoPhotoService;
    private static BackGround _backgroundService;
    private static BackGroundCommand _backgroundCommand;
    private static SkinUploadCommand _skinUploadCommand;
    private static Shout? _shout;
    private static UserTracker? _userTracker;
    private static bool _sessionInitialized;
    private static int _shutdownOnce;
    private static readonly string NapcatDailyReportUserId = "2242501795";
    private const string CustomTitleReviewGroupId = "1081992954";
    private const string NapcatQuickReplyAtBotQq = "1224299555";
    private static readonly object _msgSeqLock = new();
    private static readonly Dictionary<string, int> _msgSeqMap = new(StringComparer.Ordinal);
    private static readonly object _customTitleApprovalLock = new();
    private static readonly Dictionary<string, PendingCustomTitleRequest> _pendingCustomTitleRequests = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, PendingSkinAddRequest> _pendingSkinAddRequests = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, PendingPlayerIdFontSizeRequest> _pendingPlayerIdFontSizeRequests = new(StringComparer.Ordinal);
    private static readonly object _pendingUpdateLock = new();
    private static string? _pendingUpdateText;
    private static readonly HashSet<string> _pendingUpdateDeliveredUsers = new(StringComparer.Ordinal);
    private const string DeepSeekApiUrl = "https://api.deepseek.com/chat/completions";
    private const string DeepSeekModelName = "deepseek-chat";
    private const string DeepSeekApiKey = "sk-23056eb740b34f86ad2ab3b562bbae4b";
    private static readonly ConcurrentDictionary<string, bool> _callModerationCache = new(StringComparer.OrdinalIgnoreCase);
    private static bool _aiModerationEnabled = true;
    private static readonly ConcurrentDictionary<string, BwQuickReplyContext> _bwQuickReplyContexts = new(StringComparer.Ordinal);
    private static readonly object _bwQuickReplyContextFileLock = new();
    private static string? _bwQuickReplyContextPath;
    private static readonly object _runtimeStateFileLock = new();
    private static string? _runtimeStatePath;
    private static readonly ConcurrentDictionary<string, IdiomChainSession> _idiomChainSessions = new(StringComparer.Ordinal);
    private static readonly string[] LocalBlockedCallKeywords =
    {
        "习近平", "共产党", "中共", "六四", "天安门事件", "法轮功", "台独", "港独", "藏独", "疆独", "颠覆国家政权",
        "性交", "做爱", "口交", "肛交", "强奸", "轮奸", "乱伦", "幼女", "未成年性", "性交易", "嫖娼", "援交", "约炮",
        "成人视频", "黄片", "裸聊", "av女优", "porn",
        "性暗示", "性器官", "阴茎", "阴道", "乳房", "胸部", "下体", "龟头", "精液", "射精", "勃起", "呻吟", "开房", "湿了"
    };
    private static readonly string[] LocalBlockedCallVentingKeywords =
    {
        "发癫", "癫了", "崩溃", "我快疯了", "我要疯了", "受不了了", "毁灭吧", "不想活了", "活不下去", "想死", "气炸了", "炸了"
    };
    private static readonly string[] CallTextFamilyAllowKeywords =
    {
        "爸爸", "妈妈", "爸爸妈妈", "爸妈", "父母"
    };
    private static readonly string[] BenignGeoTerms =
    {
        "中国", "中华人民共和国"
    };
    private static readonly string[] PoliticalEscalationSignals =
    {
        "共产党", "中共", "台独", "港独", "藏独", "疆独", "颠覆", "政权", "领导人", "天安门", "六四", "法轮功", "煽动", "分裂"
    };
    private static readonly ConcurrentDictionary<string, ApiResultCacheEntry> _apiResultCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan GameStatsCacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LeaderboardCacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PlayerInfoCacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan BwPlayerInfoFastWaitTimeout = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan ApiCacheMaintenanceInterval = TimeSpan.FromMinutes(2);
    private const int ApiCacheMaxEntries = 600;
    private const int CallModerationCacheMaxEntries = 1200;
    private static readonly TimeSpan BwQuickReplyContextTtl = TimeSpan.FromHours(12);
    private const int BwQuickReplyContextMaxEntries = 8000;
    private static readonly TimeSpan IdiomChainSessionTimeout = TimeSpan.FromMinutes(20);
    private const int IdiomChainMaxUsedCount = 300;
    private const int MsgSeqMapMaxEntries = 4096;
    private const int GroupCallRateLimitPerMinute = 8;
    private const int GroupCallRepeatedContentBlockThreshold = 3;
    private static readonly TimeSpan GroupCallRateLimitWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan GroupCallRateLimitStateTtl = TimeSpan.FromMinutes(15);
    private static readonly object _groupCallRateLimitLock = new();
    private static readonly Dictionary<string, GroupCallRateLimitState> _groupCallRateLimitStates = new(StringComparer.Ordinal);
    private static readonly Regex RepeatedEmotionalCharRegex = new(@"([啊呀哇哈呜哦嗯])\1{5,}", RegexOptions.Compiled);
    private static readonly Regex RepeatedEmotionPunctuationRegex = new(@"([!！?？~～])\1{5,}", RegexOptions.Compiled);
    private static long _lastApiCacheMaintenanceTicksUtc;
    private static readonly object _leaderboardUrlLock = new();
    private static string? _lastWorkingLeaderboardUrl;
    private static string _lastBwSnapshotScanDate = string.Empty;
    private static string _lastLeaderboardSnapshotScanDate = string.Empty;
    private static readonly TimeSpan LeaderboardNightScanStartTime = new(21, 0, 0);
    private static readonly TimeSpan LeaderboardNightScanStopTime = new(23, 58, 0);
    private static readonly SemaphoreSlim _swInitLock = new(1, 1);
    private static readonly SemaphoreSlim _lbInitLock = new(1, 1);
    private static readonly SemaphoreSlim _shoutInitLock = new(1, 1);
    private static readonly SemaphoreSlim _helpInitLock = new(1, 1);
    private static readonly SemaphoreSlim _sessionInitLock = new(1, 1);
    private static bool _swInitialized;
    private static bool _lbInitialized;
    private static bool _shoutInitialized;
    private static bool _helpInitialized;
    private static readonly TimeSpan RendererIdleTimeout = TimeSpan.FromMinutes(8);
    private static long _lastSessionRendererUseTicksUtc;
    private static long _lastSwRendererUseTicksUtc;
    private static long _lastLbRendererUseTicksUtc;
    private static long _lastShoutRendererUseTicksUtc;
    private static long _lastHelpRendererUseTicksUtc;
    private static TextWriter? _startupLogWriter;
    private static TextWriter? _originalConsoleOut;
    private static TextWriter? _originalConsoleError;
    private static string? _startupLogPath;

    private enum MessageSource
    {
        Unknown = 0,
        OfficialGroup = 1,
        NapcatGroup = 2,
        NapcatPrivate = 3
    }

    private static readonly AsyncLocal<MessageSource> _currentMessageSource = new();
    private static readonly AsyncLocal<string?> _currentGroupRequesterUserId = new();

    private sealed class PendingCustomTitleRequest
    {
        public string ApplicantQq { get; init; } = string.Empty;
        public string ApplicantBjdName { get; init; } = string.Empty;
        public string ApplicantBjdUuid { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string ColorHex { get; init; } = "FFFFFF";
        public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    }

    private sealed class PendingSkinAddRequest
    {
        public string ApplicantQq { get; init; } = string.Empty;
        public string ApplicantBjdName { get; init; } = string.Empty;
        public string ApplicantBjdUuid { get; init; } = string.Empty;
        public string OfficialId { get; init; } = string.Empty;
        public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    }

    private sealed class PendingPlayerIdFontSizeRequest
    {
        public string ApplicantQq { get; init; } = string.Empty;
        public string ApplicantBjdName { get; init; } = string.Empty;
        public int IdFontSize { get; init; }
        public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    }

    private sealed record BwQuickReplyContext(string PlayerName, DateTimeOffset CreatedAtUtc);
    private sealed class PersistedBwQuickReplyContext
    {
        public string Key { get; set; } = string.Empty;
        public string PlayerName { get; set; } = string.Empty;
        public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    private sealed class PersistedPendingCustomTitleRequest
    {
        public string ReviewMessageId { get; set; } = string.Empty;
        public string ApplicantQq { get; set; } = string.Empty;
        public string ApplicantBjdName { get; set; } = string.Empty;
        public string ApplicantBjdUuid { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string ColorHex { get; set; } = "FFFFFF";
        public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    private sealed class PersistedPendingSkinAddRequest
    {
        public string ReviewMessageId { get; set; } = string.Empty;
        public string ApplicantQq { get; set; } = string.Empty;
        public string ApplicantBjdName { get; set; } = string.Empty;
        public string ApplicantBjdUuid { get; set; } = string.Empty;
        public string OfficialId { get; set; } = string.Empty;
        public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    private sealed class PersistedPendingPlayerIdFontSizeRequest
    {
        public string ReviewMessageId { get; set; } = string.Empty;
        public string ApplicantQq { get; set; } = string.Empty;
        public string ApplicantBjdName { get; set; } = string.Empty;
        public int IdFontSize { get; set; }
        public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    private sealed class PersistedIdiomChainSession
    {
        public string GroupId { get; set; } = string.Empty;
        public List<string> UsedIdioms { get; set; } = new();
        public string LastIdiom { get; set; } = string.Empty;
        public string ExpectedStartChar { get; set; } = string.Empty;
        public DateTimeOffset LastUpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    private sealed class RuntimeStateSnapshot
    {
        public List<PersistedPendingCustomTitleRequest> PendingCustomTitleRequests { get; set; } = new();
        public List<PersistedPendingSkinAddRequest> PendingSkinAddRequests { get; set; } = new();
        public List<PersistedPendingPlayerIdFontSizeRequest> PendingPlayerIdFontSizeRequests { get; set; } = new();
        public string PendingUpdateText { get; set; } = string.Empty;
        public List<string> PendingUpdateDeliveredUsers { get; set; } = new();
        public List<PersistedIdiomChainSession> IdiomChainSessions { get; set; } = new();
    }

    private sealed class GroupCallRateLimitState
    {
        public Queue<DateTimeOffset> MessageTimesUtc { get; } = new();
        public string LastContentKey { get; set; } = string.Empty;
        public int LastContentStreak { get; set; }
        public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    private enum GroupCallLimitReason
    {
        None = 0,
        TooFrequent = 1,
        RepeatedContent = 2
    }

    private sealed class IdiomChainSession
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public HashSet<string> UsedIdioms { get; } = new(StringComparer.Ordinal);
        public string LastIdiom { get; set; } = string.Empty;
        public string ExpectedStartChar { get; set; } = string.Empty;
        public DateTimeOffset LastUpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    private sealed class TeeTextWriter : TextWriter
    {
        private readonly TextWriter _left;
        private readonly TextWriter _right;

        public TeeTextWriter(TextWriter left, TextWriter right)
        {
            _left = left;
            _right = right;
        }

        public override Encoding Encoding => _left.Encoding;

        public override void Write(char value)
        {
            _left.Write(value);
            _right.Write(value);
        }

        public override void Write(string? value)
        {
            _left.Write(value);
            _right.Write(value);
        }

        public override void WriteLine(string? value)
        {
            _left.WriteLine(value);
            _right.WriteLine(value);
        }

        public override void Flush()
        {
            _left.Flush();
            _right.Flush();
        }
    }

    private sealed record ApiResultCacheEntry(ApiCallResult Result, DateTimeOffset ExpiresAtUtc);

    static async Task Main(string[] args)
    {
        using var exitCts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            exitCts.Cancel();
        };

        var rootDir = ResolveRootDirectory();
        InitializeStartupLog(rootDir, DateTime.Now);
        Console.WriteLine("=== Bedwars Bot (中文版) 启动中 ===");
        try
        {
            var config = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .Build();

            _apiUrl = config["Bedwars:ApiUrlTemplate"] ?? _apiUrl;
            _leaderboardApiUrl = config["Bedwars:LeaderboardApiUrl"] ?? _leaderboardApiUrl;
            _playerApiUrl = config["Bedwars:PlayerApiUrl"] ?? _playerApiUrl;
            _apiKey = config["Bedwars:ApiKey"] ?? string.Empty;
            _apiKeys = LoadApiKeys(config, _apiKey);
            if (_apiKeys.Count > 0)
            {
                Console.WriteLine($"[API] 已加载 {_apiKeys.Count} 个 Token（轮询+失败切换）");
            }
            else
            {
                Console.WriteLine("[API] 未配置 Token，将以匿名请求模式运行");
            }
            _gameType = config["Bedwars:GameType"] ?? _gameType;
            _adminQq = config["Bot:AdminQQ"] ?? string.Empty;

            var configPath = ConfigManager.GetConfigFilePath();
            var botConfig = ConfigManager.LoadConfig();
            if (botConfig == null)
            {
                Console.WriteLine("配置文件未填写，程序已退出。");
                return;
            }

            Console.WriteLine($"[配置] 使用文件: {configPath}");

            var hasOfficialConfig =
                !string.IsNullOrWhiteSpace(botConfig.AppId)
                && !botConfig.AppId.Contains("请在此填入", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(botConfig.ClientSecret)
                && !botConfig.ClientSecret.Contains("请在此填入", StringComparison.Ordinal);

            var napcatWsUrl = botConfig.Napcat?.WsUrl ?? string.Empty;
            var enableNapcat = !string.IsNullOrWhiteSpace(napcatWsUrl);
            var enableOfficial = hasOfficialConfig;
            var enableOfficialWebhook = enableOfficial && (botConfig.Webhook?.Enabled ?? false);
            const int groupAtIntent = 1 << 25;
            var officialIntents = botConfig.Intents;
            if (enableOfficial && officialIntents <= 0)
            {
                officialIntents = groupAtIntent;
                Console.WriteLine("⚠️ 官方 Intents<=0，已自动改为 33554432（GROUP_AT_MESSAGE_CREATE）。");
            }
            if (enableOfficial)
            {
                Console.WriteLine(enableOfficialWebhook
                    ? "[官方] 启动模式: Webhook（不连接 WebSocket）"
                    : "[官方] 启动模式: WebSocket（如出现 op=9 循环，请在 pz/config.json 开启 Webhook.Enabled=true）");
            }

            if (!enableNapcat && !enableOfficial)
            {
                Console.WriteLine("❌ 未检测到可用机器人配置：请配置 NapCat.WsUrl 或官方 AppId/ClientSecret。");
                return;
            }

            if (enableOfficial)
            {
                if ((officialIntents & groupAtIntent) == 0)
                {
                    Console.WriteLine("⚠️ 当前 Intents 未包含群聊@消息事件(1<<25)。将收不到群聊消息，请在 pz/config.json 中设置 Intents=33554432。");
                }
            }
            else
            {
                Console.WriteLine("[官方] 未配置 AppId/ClientSecret，已跳过官方机器人启动。");
            }

            if (!enableNapcat)
            {
                Console.WriteLine("[NapCat] 未配置 WsUrl，已跳过 NapCat 启动。");
            }

            _botConfig = botConfig;

            _dataStore = new BotDataStore(rootDir);
            _dataStore.Initialize();
            _runtimeStatePath = Path.Combine(_dataStore.ConfigDirectory, "runtime_state.json");
            LoadRuntimeStateFromDisk();
            _bwQuickReplyContextPath = Path.Combine(_dataStore.ConfigDirectory, "bw_quick_reply_contexts.json");
            LoadBwQuickReplyContextsFromDisk();
            _bindService = new BindService(_dataStore);
            _infoPhotoService = new InfoPhotoService(_dataStore, _httpClient);
            _backgroundService = new BackGround(_dataStore, _httpClient);
            _backgroundCommand = new BackGroundCommand(_backgroundService, _bindService);
            _skinUploadCommand = new SkinUploadCommand(_infoPhotoService, _bindService);
            _shout = new Shout(rootDir);
            _userTracker = new UserTracker();
            Console.WriteLine($"数据目录: {rootDir}");
            var shoutDbPath = ResolveShoutLogDbPath(config, rootDir, botConfig);
            var sessionDbPath = ResolveSessionDbPath(config, rootDir);
            var bwHistoryDbPath = ResolveBwHistoryDbPath(config, rootDir);
            var leaderboardSnapshotDbPath = ResolveLeaderboardSnapshotDbPath(config, rootDir);
            Console.WriteLine($"喊话数据库: {shoutDbPath}");
            Console.WriteLine($"Session数据库: {sessionDbPath}");
            Console.WriteLine($"BW历史数据库: {bwHistoryDbPath}");
            Console.WriteLine($"排行榜历史数据库: {leaderboardSnapshotDbPath}");

            try
            {
                _bwHistoryStore = new BwHistorySnapshotStore(bwHistoryDbPath);
                _bwHistoryStore.Initialize();
            }
            catch (Exception ex)
            {
                _bwHistoryStore = null;
                Console.WriteLine($"[BW历史] 初始化失败: {ex.Message}");
            }

            try
            {
                _leaderboardSnapshotStore = new LeaderboardDailySnapshotStore(leaderboardSnapshotDbPath);
                _leaderboardSnapshotStore.Initialize();
            }
            catch (Exception ex)
            {
                _leaderboardSnapshotStore = null;
                Console.WriteLine($"[排行榜历史] 初始化失败: {ex.Message}");
            }

            _bwService = new BedwarsService();
            try
            {
                await _bwService.InitializeAsync();
            }
            catch (Exception ex)
            {
                _bwService = null;
                Console.WriteLine($"[渲染器] BW 初始化失败: {ex.Message}");
            }

            _swService = new SkaywarsService();
            _swInitialized = false;
            Console.WriteLine("[低内存] SW 渲染器懒加载已启用（首次使用时初始化）。");

            _lbService = new LeaderboardRankings();
            _lbInitialized = false;
            Console.WriteLine("[低内存] 排行榜渲染器懒加载已启用（首次使用时初始化）。");

            _shoutLogService = new ShoutLogService(shoutDbPath, _dataStore);
            _shoutInitialized = false;
            Console.WriteLine("[低内存] 喊话渲染器懒加载已启用（首次使用时初始化）。");

            _helpService = new HelpService();
            _helpInitialized = false;
            Console.WriteLine("[低内存] 帮助渲染器懒加载已启用（首次使用时初始化）。");

            _sessionService = new SessionService(sessionDbPath);

            if (enableNapcat)
            {
                _napcatBot = new NapcatBot(napcatWsUrl, botConfig.Napcat?.AccessToken ?? string.Empty);
                _napcatAuto = new NapcatAutoApproveAndWelcome(_napcatBot);
                _napcatAuto.Register();
                _napcatBot.OnGroupMessage += HandleNapcatGroupMessageAsync;
                _napcatBot.OnPrivateMessage += HandleNapcatPrivateMessageAsync;
                await _napcatBot.StartAsync();
                _ = Task.Run(() => RunNapcatDailyUsageReporterAsync(exitCts.Token));
                Console.WriteLine(">>> NapCat 机器人已就绪！可用指令: !bw <ID> [模式] / !bw <ID> <x年x月x日> / !sw <ID> / !lb <ID> / !sess bw [玩家名] [t天数] / /喊话 [几月几日几点几分] / !bind <布吉岛名> / !skin add <正版ID> / /skin up / !bg / !bg set <透明度> / !bg icon <像素> / !bg id <像素> / !bg cl <颜色ID> / !help / 菜单 / /群发 / /群发编辑 <文本> / 成语接龙 [开局成语] / 结束接龙 / 接龙提示");
            }

            if (enableOfficial)
            {
                var imageHostUploader = new ImageHostUploader(botConfig.ImageHost, _httpClient);
                _qqBot = new QQBotV2(botConfig.AppId, botConfig.ClientSecret, officialIntents, imageHostUploader);
                _qqBot.OnGroupAtMessage += HandleGroupAtMessageAsync;
                if (enableOfficialWebhook)
                {
                    await _qqBot.StartHttpOnlyAsync();
                    await StartAspNetWebhookHostAsync(botConfig, exitCts.Token);
                    Console.WriteLine(">>> 官方机器人(Webhook)已就绪！（群内必须 @机器人 触发）可用指令: !bw <ID> [模式] / !bw <ID> <x年x月x日> / !sw <ID> / !lb <ID> / !sess bw [玩家名] [t天数] / /喊话 [几月几日几点几分] / !bind <布吉岛名> / !skin add <正版ID> / /skin up / !bg / !bg set <透明度> / !bg icon <像素> / !bg id <像素> / !bg cl <颜色ID> / !help / 菜单");
                }
                else
                {
                    await _qqBot.StartAsync();
                    Console.WriteLine(">>> 官方机器人已就绪！（群内必须 @机器人 触发）可用指令: !bw <ID> [模式] / !bw <ID> <x年x月x日> / !sw <ID> / !lb <ID> / !sess bw [玩家名] [t天数] / /喊话 [几月几日几点几分] / !bind <布吉岛名> / !skin add <正版ID> / /skin up / !bg / !bg set <透明度> / !bg icon <像素> / !bg id <像素> / !bg cl <颜色ID> / !help / 菜单");
                }
            }

            _ = Task.Run(() => RunBwDailySnapshotSchedulerAsync(exitCts.Token));
            _ = Task.Run(() => RunLeaderboardNightSnapshotSchedulerAsync(exitCts.Token));
            _ = Task.Run(() => RunLowMemoryMaintenanceAsync(exitCts.Token));
            await Task.Delay(Timeout.Infinite, exitCts.Token);
        }
        catch (TaskCanceledException)
        {
            Console.WriteLine("收到退出信号，正在关闭...");
        }
        finally
        {
            await ShutdownAsync();
            CloseStartupLog();
        }
    }

    private static async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdownOnce, 1) == 1)
        {
            return;
        }

        try { if (_officialWebhookServer != null) await _officialWebhookServer.StopAsync(); } catch { }
        try { if (_aspNetWebhookApp != null) await _aspNetWebhookApp.StopAsync(); } catch { }
        try { if (_sessionService != null) await _sessionService.CloseAsync(); } catch { }
        try { if (_helpService != null) await _helpService.CloseAsync(); } catch { }
        try { if (_shoutLogService != null) await _shoutLogService.CloseAsync(); } catch { }
        try { if (_lbService != null) await _lbService.CloseAsync(); } catch { }
        try { if (_swService != null) await _swService.CloseAsync(); } catch { }
        try { if (_bwService != null) await _bwService.CloseAsync(); } catch { }
    }

    internal static Task DispatchOfficialWebhookAsync(JObject payload)
    {
        if (payload == null)
        {
            return Task.CompletedTask;
        }

        if (_qqBot == null)
        {
            Console.WriteLine("[Webhook] _qqBot 尚未初始化，忽略本次事件。");
            return Task.CompletedTask;
        }

        return _qqBot.HandleWebhookPayloadAsync(payload);
    }

    private static async Task HandleGroupAtMessageAsync(GroupAtMessage message)
    {
        var previousSource = _currentMessageSource.Value;
        var previousGroupRequesterUserId = _currentGroupRequesterUserId.Value;
        _currentMessageSource.Value = MessageSource.OfficialGroup;
        try
        {
            if (string.IsNullOrWhiteSpace(message.GroupOpenId)) return;

            var groupId = message.GroupOpenId;
            var userId = message.AuthorId;
            var msgId = message.MessageId;
            _currentGroupRequesterUserId.Value = userId;

            var content = message.Content ?? string.Empty;
            var trimmedContent = content.TrimStart();
            if (AtPrefixRegex.IsMatch(trimmedContent))
            {
                content = AtPrefixRegex.Replace(trimmedContent, string.Empty);
            }
            else
            {
                content = trimmedContent;
            }
            var normalizedMsg = content.Replace('！', '!').Trim();

            var payload = string.IsNullOrWhiteSpace(message.ImageUrl)
                ? (ImagePayload?)null
                : new ImagePayload(message.ImageUrl, null, null, null);

            var backgroundResult = await _backgroundCommand.TryHandlePendingAsync(groupId, userId, payload);
            if (backgroundResult.IsHandled)
            {
                if (!string.IsNullOrWhiteSpace(backgroundResult.Message))
                {
                    await SendGroupMessageAsync(groupId, msgId, backgroundResult.Message);
                }
                return;
            }

            var skinUploadResult = await _skinUploadCommand.TryHandlePendingAsync(groupId, userId, payload);
            if (skinUploadResult.IsHandled)
            {
                if (!string.IsNullOrWhiteSpace(skinUploadResult.Message))
                {
                    await SendGroupMessageAsync(groupId, msgId, skinUploadResult.Message);
                }
                return;
            }

            if (string.IsNullOrWhiteSpace(normalizedMsg)) return;

            if (await TryHandleCustomTitleApprovalReplyAsync(groupId, msgId, userId, normalizedMsg, message.ReplyMessageId))
            {
                return;
            }

            if (await TryHandleBwQuickReplyAsync(groupId, msgId, userId, normalizedMsg, message.ReplyMessageId))
            {
                return;
            }

            if (await TryHandleCallEchoInGroupAsync(normalizedMsg, groupId, msgId, userId))
            {
                return;
            }

            string raw;
            string[] parts;
            if (TryMapBjdBwShortcut(normalizedMsg, userId, out raw, out parts, out var shouldSilentlyIgnore))
            {
                // mapped to bw command
            }
            else
            {
                if (shouldSilentlyIgnore)
                {
                    return;
                }

                var firstChar = normalizedMsg[0];
                if (firstChar != '/' && firstChar != '!' && firstChar != '=' && firstChar != '／' && firstChar != '！' && firstChar != '＝')
                {
                    return;
                }

                raw = normalizedMsg.TrimStart('/', '／');
                parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) return;
            }

            var cmd = NormalizeCommand(parts[0]);
            LogCommandInvocation("group", groupId, userId, raw);

            if (cmd == "update" || cmd == "更新")
            {
                await HandleUpdateCommandAsync(raw, parts, groupId, msgId, userId);
                return;
            }

            if (cmd == "开关ai" || cmd == "ai开关")
            {
                await HandleAiModerationToggleCommandAsync(raw, parts, groupId, msgId, userId);
                return;
            }

            if (cmd == "起床文本")
            {
                await HandleBwCaptionCommandAsync(raw, parts, groupId, msgId, userId);
                return;
            }

            if (cmd == "叫")
            {
                await HandleCallWhitelistCommandAsync(raw, parts, groupId, msgId, userId);
                return;
            }

            if (cmd == "群发编辑" || cmd == "群发编辑文本" || (cmd == "群发" && parts.Length >= 2 && parts[1] == "编辑"))
            {
                if (!IsAdminUser(userId))
                {
                    await SendGroupMessageAsync(groupId, msgId, "❌ 无权限：仅管理员可使用 /群发编辑。");
                    return;
                }

                if (_shout == null)
                {
                    await SendGroupMessageAsync(groupId, msgId, "❌ 群发服务未初始化。");
                    return;
                }

                string text;
                if (raw.StartsWith("群发编辑", StringComparison.Ordinal))
                {
                    text = raw["群发编辑".Length..].Trim();
                }
                else
                {
                    text = cmd == "群发"
                        ? raw[(parts[0].Length + parts[1].Length)..].Trim()
                        : raw[(parts[0].Length)..].Trim();
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    await SendGroupMessageAsync(groupId, msgId, "❌ 用法: /群发编辑 文本");
                    return;
                }

                var result = _shout.UpdateText(text);
                await SendGroupMessageAsync(groupId, msgId, $"{result}\n当前群发内容: {text}");
                return;
            }

            if (cmd == "群发")
            {
                if (!IsAdminUser(userId))
                {
                    await SendGroupMessageAsync(groupId, msgId, "❌ 无权限：仅管理员可使用 /群发。");
                    return;
                }

                if (_shout == null || _napcatBot == null)
                {
                    await SendGroupMessageAsync(groupId, msgId, "❌ 群发服务未初始化。");
                    return;
                }

                var result = await _shout.StartBroadcastAsync(_napcatBot);
                await SendGroupMessageAsync(groupId, msgId, result);
                return;
            }

            if (cmd == "bind")
            {
                await HandleBindCommandAsync(parts, groupId, msgId, userId);
                return;
            }

            if (cmd == "skin")
            {
                await HandleSkinCommandAsync(parts, groupId, msgId, userId);
                return;
            }

            if (cmd == "bg")
            {
                await HandleBgCommandAsync(parts, groupId, msgId, userId);
                return;
            }

            if (cmd == "ch")
            {
                await HandleCustomTitleCommandAsync(parts, groupId, msgId, userId);
                return;
            }

            if (cmd == "bw")
            {
                await HandleBwCommandAsync(parts, groupId, msgId, userId);
                return;
            }

            if (cmd == "sw")
            {
                await HandleSwCommandAsync(parts, groupId, msgId, userId);
                return;
            }

            if (cmd == "lb")
            {
                await HandleLbCommandAsync(parts, groupId, msgId, userId);
                return;
            }

            if (cmd == "sess" || cmd == "session")
            {
                await HandleSessionCommandAsync(parts, groupId, msgId, userId);
                return;
            }

            if (cmd == "喊话")
            {
                await HandleShoutLogCommandAsync(parts, groupId, msgId, userId);
                return;
            }

            if (cmd == "help" || cmd == "帮助" || cmd == "菜单")
            {
                await HandleHelpCommandAsync(groupId, msgId);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[错误] {ex.Message}");
        }
        finally
        {
            _currentMessageSource.Value = previousSource;
            _currentGroupRequesterUserId.Value = previousGroupRequesterUserId;
        }
    }

    private static async Task HandleNapcatGroupMessageAsync(NapcatGroupMessage message)
    {
        var previousSource = _currentMessageSource.Value;
        var previousGroupRequesterUserId = _currentGroupRequesterUserId.Value;
        _currentMessageSource.Value = MessageSource.NapcatGroup;
        try
        {
            if (string.IsNullOrWhiteSpace(message.GroupId)) return;

            var groupId = message.GroupId;
            var userId = message.UserId;
            var msgId = message.MessageId;
            _currentGroupRequesterUserId.Value = userId;

            var backgroundResult = await _backgroundCommand.TryHandlePendingAsync(message.Raw, groupId, userId);
            if (backgroundResult.IsHandled)
            {
                if (!string.IsNullOrWhiteSpace(backgroundResult.Message))
                {
                    await SendGroupMessageAsync(groupId, msgId, backgroundResult.Message);
                }
                return;
            }

            var skinUploadResult = await _skinUploadCommand.TryHandlePendingAsync(message.Raw, groupId, userId);
            if (skinUploadResult.IsHandled)
            {
                if (!string.IsNullOrWhiteSpace(skinUploadResult.Message))
                {
                    await SendGroupMessageAsync(groupId, msgId, skinUploadResult.Message);
                }
                return;
            }

            var normalizedMsg = (message.Content ?? string.Empty).Replace('！', '!');
            normalizedMsg = CqAtPrefixRegex.Replace(normalizedMsg, string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalizedMsg)) return;

            var napcatReplyMessageId = TryExtractNapcatReplyMessageId(message.Raw);
            var napcatAtQuickReplyBot = IsNapcatAtQuickReplyBot(message.Raw);
            if (await TryHandleCustomTitleApprovalReplyAsync(groupId, msgId, userId, normalizedMsg, napcatReplyMessageId))
            {
                return;
            }

            if (await TryHandleBwQuickReplyAsync(
                    groupId,
                    msgId,
                    userId,
                    normalizedMsg,
                    napcatReplyMessageId,
                    allowGroupLatestFallback: napcatAtQuickReplyBot))
            {
                return;
            }

            if (await TryHandleCallEchoInGroupAsync(normalizedMsg, groupId, msgId, userId))
            {
                return;
            }

            if (await TryHandleNapcatIdiomChainAsync(groupId, msgId, userId, normalizedMsg))
            {
                return;
            }

            string raw;
            string[] parts;
            if (TryMapBjdBwShortcut(normalizedMsg, userId, out raw, out parts, out var shouldSilentlyIgnore))
            {
                // mapped to bw command
            }
            else
            {
                if (shouldSilentlyIgnore)
                {
                    return;
                }

                var firstChar = normalizedMsg[0];
                if (firstChar != '/' && firstChar != '!' && firstChar != '=' && firstChar != '／' && firstChar != '！' && firstChar != '＝')
                {
                    return;
                }

                normalizedMsg = normalizedMsg[1..].TrimStart();
                if (string.IsNullOrWhiteSpace(normalizedMsg)) return;

                parts = normalizedMsg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) return;
                raw = normalizedMsg.TrimStart('/', '／');
            }

            var cmd = NormalizeCommand(parts[0]);
            LogCommandInvocation("group", groupId, userId, raw);
            CountNapcatCommandInvocationIfNeeded(cmd, parts);

            if (cmd == "update" || cmd == "更新")
            {
                await HandleUpdateCommandAsync(raw, parts, groupId, msgId, userId);
                return;
            }

            if (cmd == "开关ai" || cmd == "ai开关")
            {
                await HandleAiModerationToggleCommandAsync(raw, parts, groupId, msgId, userId);
                return;
            }

            if (cmd == "起床文本")
            {
                await HandleBwCaptionCommandAsync(raw, parts, groupId, msgId, userId);
                return;
            }

            if (cmd == "叫")
            {
                await HandleCallWhitelistCommandAsync(raw, parts, groupId, msgId, userId);
                return;
            }

            if (cmd == "群发编辑" || cmd == "群发编辑文本" || (cmd == "群发" && parts.Length >= 2 && parts[1] == "编辑"))
            {
                if (!IsAdminUser(userId))
                {
                    await SendGroupMessageAsync(groupId, msgId, "❌ 无权限：仅管理员可使用 /群发编辑。");
                    return;
                }

                if (_shout == null)
                {
                    await SendGroupMessageAsync(groupId, msgId, "❌ 群发服务未初始化。");
                    return;
                }

                string text;
                if (raw.StartsWith("群发编辑", StringComparison.Ordinal))
                {
                    text = raw["群发编辑".Length..].Trim();
                }
                else
                {
                    text = cmd == "群发"
                        ? raw[(parts[0].Length + parts[1].Length)..].Trim()
                        : raw[(parts[0].Length)..].Trim();
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    await SendGroupMessageAsync(groupId, msgId, "❌ 用法: /群发编辑 文本");
                    return;
                }

                var result = _shout.UpdateText(text);
                await SendGroupMessageAsync(groupId, msgId, $"{result}\n当前群发内容: {text}");
                return;
            }

            if (cmd == "群发")
            {
                if (!IsAdminUser(userId))
                {
                    await SendGroupMessageAsync(groupId, msgId, "❌ 无权限：仅管理员可使用 /群发。");
                    return;
                }

                if (_shout == null || _napcatBot == null)
                {
                    await SendGroupMessageAsync(groupId, msgId, "❌ 群发服务未初始化。");
                    return;
                }

                var result = await _shout.StartBroadcastAsync(_napcatBot);
                await SendGroupMessageAsync(groupId, msgId, result);
                return;
            }

            if (cmd == "bind")
            {
                await HandleBindCommandAsync(parts, groupId, msgId, userId);
                return;
            }

            if (cmd == "skin")
            {
                await HandleSkinCommandAsync(parts, groupId, msgId, userId);
                return;
            }

            if (cmd == "bg")
            {
                await HandleBgCommandAsync(parts, groupId, msgId, userId);
                return;
            }

            if (cmd == "ch")
            {
                await HandleCustomTitleCommandAsync(parts, groupId, msgId, userId);
                return;
            }

            if (cmd == "bw")
            {
                await HandleBwCommandAsync(parts, groupId, msgId, userId);
                return;
            }

            if (cmd == "sw")
            {
                await HandleSwCommandAsync(parts, groupId, msgId, userId);
                return;
            }

            if (cmd == "lb")
            {
                await HandleLbCommandAsync(parts, groupId, msgId, userId);
                return;
            }

            if (cmd == "sess" || cmd == "session")
            {
                await HandleSessionCommandAsync(parts, groupId, msgId, userId);
                return;
            }

            if (cmd == "喊话")
            {
                await HandleShoutLogCommandAsync(parts, groupId, msgId, userId);
                return;
            }

            if (cmd == "help" || cmd == "帮助" || cmd == "菜单")
            {
                await HandleHelpCommandAsync(groupId, msgId);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NapCat错误] {ex.Message}");
        }
        finally
        {
            _currentMessageSource.Value = previousSource;
            _currentGroupRequesterUserId.Value = previousGroupRequesterUserId;
        }
    }

    private static async Task HandleNapcatPrivateMessageAsync(NapcatPrivateMessage message)
    {
        var previousSource = _currentMessageSource.Value;
        var previousGroupRequesterUserId = _currentGroupRequesterUserId.Value;
        _currentMessageSource.Value = MessageSource.NapcatPrivate;
        _currentGroupRequesterUserId.Value = null;
        try
        {
            var userId = message.UserId;
            if (string.IsNullOrWhiteSpace(userId)) return;

            var normalizedMsg = (message.Content ?? string.Empty).Replace('！', '!').Trim();
            if (string.IsNullOrWhiteSpace(normalizedMsg)) return;

            if (await TryHandleCallEchoInPrivateAsync(normalizedMsg, userId))
            {
                return;
            }

            string raw;
            string[] parts;
            if (TryMapBjdBwShortcut(normalizedMsg, userId, out raw, out parts, out var shouldSilentlyIgnore))
            {
                // mapped to bw command
            }
            else
            {
                if (shouldSilentlyIgnore)
                {
                    return;
                }

                var firstChar = normalizedMsg[0];
                if (firstChar != '/' && firstChar != '!' && firstChar != '=' && firstChar != '／' && firstChar != '！' && firstChar != '＝')
                {
                    return;
                }

                normalizedMsg = normalizedMsg[1..].TrimStart();
                if (string.IsNullOrWhiteSpace(normalizedMsg)) return;
                raw = normalizedMsg;

                parts = normalizedMsg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) return;
            }

            var cmd = NormalizeCommand(parts[0]);
            LogCommandInvocation("private", null, userId, raw);
            CountNapcatCommandInvocationIfNeeded(cmd, parts);

            if (cmd == "update" || cmd == "更新")
            {
                await HandleUpdatePrivateCommandAsync(raw, parts, userId);
                return;
            }

            if (cmd == "开关ai" || cmd == "ai开关")
            {
                await HandleAiModerationTogglePrivateCommandAsync(raw, parts, userId);
                return;
            }

            if (cmd == "起床文本")
            {
                await HandleBwCaptionPrivateCommandAsync(raw, parts, userId);
                return;
            }

            if (cmd == "叫")
            {
                await HandleCallWhitelistPrivateCommandAsync(raw, parts, userId);
                return;
            }

            if (cmd == "bw")
            {
                await HandleBwPrivateCommandAsync(parts, userId);
                return;
            }

            if (cmd == "sw")
            {
                await HandleSwPrivateCommandAsync(parts, userId);
                return;
            }

            if (cmd == "lb")
            {
                await HandleLbPrivateCommandAsync(parts, userId);
                return;
            }

            if (cmd == "sess" || cmd == "session")
            {
                await HandleSessionPrivateCommandAsync(parts, userId);
                return;
            }

            if (cmd == "help" || cmd == "帮助" || cmd == "菜单")
            {
                await HandleHelpPrivateCommandAsync(userId);
                return;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NapCat私聊错误] {ex.Message}");
        }
        finally
        {
            _currentMessageSource.Value = previousSource;
            _currentGroupRequesterUserId.Value = previousGroupRequesterUserId;
        }
    }

    private static async Task HandleBindCommandAsync(string[] parts, string? groupId, string? msgId, string? userId)
    {
        if (string.IsNullOrWhiteSpace(groupId) || NeedMsgIdButMissing(msgId) || string.IsNullOrWhiteSpace(userId)) return;

        if (parts.Length < 2)
        {
            await SendGroupMessageAsync(groupId, msgId, "❌ 用法: !bind <布吉岛用户名>");
            return;
        }

        var playerName = parts[1];
        await SendGroupMessageAsync(groupId, msgId, $"🔗 正在绑定 {playerName}...");

        var bindResult = await _bindService.BindAsync(userId, playerName, RequestPlayerInfoAsync);
        await SendGroupMessageAsync(groupId, msgId, bindResult.Message);
    }

    private static async Task HandleSkinCommandAsync(string[] parts, string? groupId, string? msgId, string? userId)
    {
        if (string.IsNullOrWhiteSpace(groupId) || NeedMsgIdButMissing(msgId) || string.IsNullOrWhiteSpace(userId)) return;
        var safeMsgId = msgId ?? string.Empty;

        if (parts.Length < 2)
        {
            await SendGroupMessageAsync(groupId, safeMsgId, "❌ 用法: !skin add <正版ID> 或 /skin up");
            return;
        }

        if (parts[1].Equals("add", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length < 3)
            {
                await SendGroupMessageAsync(groupId, safeMsgId, "❌ 用法: !skin add <正版ID>");
                return;
            }

            var officialId = parts[2].Trim();
            if (string.IsNullOrWhiteSpace(officialId))
            {
                await SendGroupMessageAsync(groupId, safeMsgId, "❌ 正版ID不能为空。");
                return;
            }

            if (IsOfficialGroupMessageSource())
            {
                if (!_bindService.TryGetBindingByQq(userId, out var binding) || string.IsNullOrWhiteSpace(binding.BjdUuid))
                {
                    await SendGroupMessageAsync(groupId, safeMsgId, "❌ 未检测到你的绑定信息，请先执行 !bind <布吉岛用户名>");
                    return;
                }

                if (!string.Equals(groupId, CustomTitleReviewGroupId, StringComparison.Ordinal))
                {
                    await SendGroupMessageAsync(groupId, safeMsgId, $"""
📝 皮肤绑定审核已迁移到官方群：{CustomTitleReviewGroupId}。
请先加入该群，再在群内发送：!skin add <正版ID>
管理员在审核群发送“同意”后即生效。
""");
                    return;
                }

                var displayOfficialId = MaskPlayerId(officialId);
                var reviewContent = $"""
【皮肤绑定申请】
申请QQ: {userId}
布吉岛ID: {binding.BjdName}
UUID: {binding.BjdUuid}
正版ID: {displayOfficialId}
管理员在本群发送“同意”即可通过（回复本条可精准通过该申请）。
""";

                var reviewMessageId = await SendGroupMessageWithIdAsync(CustomTitleReviewGroupId, safeMsgId, reviewContent);
                if (string.IsNullOrWhiteSpace(reviewMessageId))
                {
                    await SendGroupMessageAsync(groupId, safeMsgId, "❌ 申请提交失败：无法发送到审核群。");
                    return;
                }

                lock (_customTitleApprovalLock)
                {
                    _pendingSkinAddRequests[reviewMessageId] = new PendingSkinAddRequest
                    {
                        ApplicantQq = userId,
                        ApplicantBjdName = binding.BjdName,
                        ApplicantBjdUuid = binding.BjdUuid,
                        OfficialId = officialId,
                        CreatedAtUtc = DateTimeOffset.UtcNow
                    };
                }

                PersistRuntimeStateToDisk();
                await SendGroupMessageAsync(groupId, safeMsgId, "✅ 已提交皮肤绑定申请，等待管理员在本群发送“同意”通过。");
                return;
            }

            await SendGroupMessageAsync(groupId, safeMsgId, $"🖼️ 正在绑定皮肤: {officialId}");
            var skinResult = await _infoPhotoService.AddSkinAsync(userId, officialId, _bindService);
            await SendGroupMessageAsync(groupId, safeMsgId, skinResult.Message);
            return;
        }

        if (parts[1].Equals("up", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(_adminQq))
            {
                await SendGroupMessageAsync(groupId, safeMsgId, "❌ 未配置管理员QQ，无法使用 /skin up。");
                return;
            }

            if (!IsAdminUser(userId))
            {
                await SendGroupMessageAsync(groupId, safeMsgId, "❌ 无权限：仅管理员可使用 /skin up。");
                return;
            }

            var result = _skinUploadCommand.BeginUpload(userId, groupId);
            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                await SendGroupMessageAsync(groupId, safeMsgId, result.Message);
            }
            return;
        }

        await SendGroupMessageAsync(groupId, safeMsgId, "❌ 用法: !skin add <正版ID> 或 /skin up");
    }

    private static async Task HandleBwCaptionCommandAsync(string raw, string[] parts, string? groupId, string? msgId, string? userId)
    {
        if (string.IsNullOrWhiteSpace(groupId) || NeedMsgIdButMissing(msgId))
        {
            return;
        }

        var safeMsgId = msgId ?? string.Empty;
        if (!IsAdminUser(userId))
        {
            await SendGroupMessageAsync(groupId, safeMsgId, "❌ 无权限：仅管理员可使用 /起床文本。");
            return;
        }

        var text = ExtractCommandTail(raw, parts);
        if (string.IsNullOrWhiteSpace(text))
        {
            await SendGroupMessageAsync(groupId, safeMsgId, "❌ 用法: /起床文本 文本");
            return;
        }

        _dataStore.SetBwImageCaption(text);
        await SendGroupMessageAsync(groupId, safeMsgId, $"✅ BW战绩附带文本已更新：{text}");
    }

    private static async Task HandleBwCaptionPrivateCommandAsync(string raw, string[] parts, string userId)
    {
        if (!IsAdminUser(userId))
        {
            await SendPrivateMessageAsync(userId, "❌ 无权限：仅管理员可使用 /起床文本。");
            return;
        }

        var text = ExtractCommandTail(raw, parts);
        if (string.IsNullOrWhiteSpace(text))
        {
            await SendPrivateMessageAsync(userId, "❌ 用法: /起床文本 文本");
            return;
        }

        _dataStore.SetBwImageCaption(text);
        await SendPrivateMessageAsync(userId, $"✅ BW战绩附带文本已更新：{text}");
    }

    private static bool IsAdminUser(string? userId)
    {
        return !string.IsNullOrWhiteSpace(_adminQq)
               && !string.IsNullOrWhiteSpace(userId)
               && string.Equals(userId, _adminQq, StringComparison.Ordinal);
    }

    private static async Task HandleUpdateCommandAsync(string raw, string[] parts, string? groupId, string? msgId, string? userId)
    {
        if (string.IsNullOrWhiteSpace(groupId) || NeedMsgIdButMissing(msgId))
        {
            return;
        }

        var safeMsgId = msgId ?? string.Empty;

        if (string.IsNullOrWhiteSpace(_adminQq))
        {
            await SendGroupMessageAsync(groupId, safeMsgId, "❌ 未配置管理员QQ，无法设置更新播报。");
            return;
        }

        if (string.IsNullOrWhiteSpace(userId) || !string.Equals(userId, _adminQq, StringComparison.Ordinal))
        {
            await SendGroupMessageAsync(groupId, safeMsgId, "❌ 无权限：仅管理员可使用 /update。");
            return;
        }

        var updateText = ExtractCommandTail(raw, parts);
        if (string.IsNullOrWhiteSpace(updateText))
        {
            await SendGroupMessageAsync(groupId, safeMsgId, "❌ 用法: /update 文本");
            return;
        }

        SetPendingUpdateText(updateText);
        await SendGroupMessageAsync(groupId, safeMsgId, $"✅ 已设置下一次查询播报。\n更新内容：{updateText}");
    }

    private static async Task HandleUpdatePrivateCommandAsync(string raw, string[] parts, string userId)
    {
        if (string.IsNullOrWhiteSpace(_adminQq))
        {
            await SendPrivateMessageAsync(userId, "❌ 未配置管理员QQ，无法设置更新播报。");
            return;
        }

        if (!string.Equals(userId, _adminQq, StringComparison.Ordinal))
        {
            await SendPrivateMessageAsync(userId, "❌ 无权限：仅管理员可使用 /update。");
            return;
        }

        var updateText = ExtractCommandTail(raw, parts);
        if (string.IsNullOrWhiteSpace(updateText))
        {
            await SendPrivateMessageAsync(userId, "❌ 用法: /update 文本");
            return;
        }

        SetPendingUpdateText(updateText);
        await SendPrivateMessageAsync(userId, $"✅ 已设置下一次查询播报。\n更新内容：{updateText}");
    }

    private static async Task HandleAiModerationToggleCommandAsync(string raw, string[] parts, string? groupId, string? msgId, string? userId)
    {
        if (string.IsNullOrWhiteSpace(groupId) || NeedMsgIdButMissing(msgId))
        {
            return;
        }

        var safeMsgId = msgId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(_adminQq))
        {
            await SendGroupMessageAsync(groupId, safeMsgId, "❌ 未配置管理员QQ，无法使用 /开关ai。");
            return;
        }

        if (!IsAdminUser(userId))
        {
            await SendGroupMessageAsync(groupId, safeMsgId, "❌ 无权限：仅管理员可使用 /开关ai。");
            return;
        }

        var option = ExtractCommandTail(raw, parts);
        if (TryParseAiToggleOption(option, out var enabled))
        {
            _aiModerationEnabled = enabled;
        }
        else if (!string.IsNullOrWhiteSpace(option))
        {
            await SendGroupMessageAsync(groupId, safeMsgId, "❌ 用法: /开关ai [开|关|状态]");
            return;
        }
        else
        {
            _aiModerationEnabled = !_aiModerationEnabled;
        }

        _callModerationCache.Clear();
        var state = _aiModerationEnabled ? "开启" : "关闭";
        await SendGroupMessageAsync(groupId, safeMsgId, $"✅ AI审核已{state}。");
    }

    private static async Task HandleAiModerationTogglePrivateCommandAsync(string raw, string[] parts, string userId)
    {
        if (string.IsNullOrWhiteSpace(_adminQq))
        {
            await SendPrivateMessageAsync(userId, "❌ 未配置管理员QQ，无法使用 /开关ai。");
            return;
        }

        if (!IsAdminUser(userId))
        {
            await SendPrivateMessageAsync(userId, "❌ 无权限：仅管理员可使用 /开关ai。");
            return;
        }

        var option = ExtractCommandTail(raw, parts);
        if (TryParseAiToggleOption(option, out var enabled))
        {
            _aiModerationEnabled = enabled;
        }
        else if (!string.IsNullOrWhiteSpace(option))
        {
            await SendPrivateMessageAsync(userId, "❌ 用法: /开关ai [开|关|状态]");
            return;
        }
        else
        {
            _aiModerationEnabled = !_aiModerationEnabled;
        }

        _callModerationCache.Clear();
        var state = _aiModerationEnabled ? "开启" : "关闭";
        await SendPrivateMessageAsync(userId, $"✅ AI审核已{state}。");
    }

    private static bool TryParseAiToggleOption(string? option, out bool enabled)
    {
        enabled = _aiModerationEnabled;
        var text = NormalizeCallText(option ?? string.Empty).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (text is "开" or "开启" or "on" or "open" or "1" or "true")
        {
            enabled = true;
            return true;
        }

        if (text is "关" or "关闭" or "off" or "close" or "0" or "false")
        {
            enabled = false;
            return true;
        }

        if (text is "状态" or "status")
        {
            enabled = _aiModerationEnabled;
            return true;
        }

        return false;
    }

    private static async Task HandleCallWhitelistCommandAsync(string raw, string[] parts, string? groupId, string? msgId, string? userId)
    {
        if (string.IsNullOrWhiteSpace(groupId) || NeedMsgIdButMissing(msgId))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_adminQq))
        {
            await SendGroupMessageAsync(groupId, msgId ?? string.Empty, "❌ 未配置管理员QQ，无法使用 /叫。");
            return;
        }

        if (string.IsNullOrWhiteSpace(userId) || !string.Equals(userId, _adminQq, StringComparison.Ordinal))
        {
            await SendGroupMessageAsync(groupId, msgId ?? string.Empty, "❌ 无权限：仅管理员可使用 /叫。");
            return;
        }

        var tail = ExtractCommandTail(raw, parts);
        var text = NormalizeCallText(tail);
        if (string.IsNullOrWhiteSpace(text))
        {
            await SendGroupMessageAsync(groupId, msgId ?? string.Empty, "❌ 用法: /叫 文本");
            return;
        }

        await SendGroupMessageAsync(groupId, msgId ?? string.Empty, "ℹ️ 为防止绕过审核，已禁用运行时白名单。关键词仅以代码内置规则为准。");
    }

    private static async Task HandleCallWhitelistPrivateCommandAsync(string raw, string[] parts, string userId)
    {
        if (string.IsNullOrWhiteSpace(_adminQq))
        {
            await SendPrivateMessageAsync(userId, "❌ 未配置管理员QQ，无法使用 /叫。");
            return;
        }

        if (!string.Equals(userId, _adminQq, StringComparison.Ordinal))
        {
            await SendPrivateMessageAsync(userId, "❌ 无权限：仅管理员可使用 /叫。");
            return;
        }

        var tail = ExtractCommandTail(raw, parts);
        var text = NormalizeCallText(tail);
        if (string.IsNullOrWhiteSpace(text))
        {
            await SendPrivateMessageAsync(userId, "❌ 用法: /叫 文本");
            return;
        }

        await SendPrivateMessageAsync(userId, "ℹ️ 为防止绕过审核，已禁用运行时白名单。关键词仅以代码内置规则为准。");
    }

    private static async Task<bool> TryHandleCallEchoInGroupAsync(string normalizedMsg, string groupId, string msgId, string? userId)
    {
        if (!TryExtractCallText(normalizedMsg, out var callText))
        {
            return false;
        }

        var limitReason = CheckGroupCallLimit(groupId, callText);
        if (limitReason != GroupCallLimitReason.None)
        {
            var status = limitReason == GroupCallLimitReason.TooFrequent ? "rate_limited" : "repeat_limited";
            LogCallEchoText("group", groupId, userId, callText, status);
            var tip = limitReason == GroupCallLimitReason.TooFrequent
                ? "发送过于频繁，请稍后再试。"
                : "该内容无法显示";
            await SendGroupMessageAsync(groupId, msgId, tip);
            return true;
        }

        var blocked = await IsCallTextBlockedAsync(callText);
        LogCallEchoText("group", groupId, userId, callText, blocked ? "moderation_blocked" : "allowed");
        if (blocked)
        {
            await SendGroupMessageAsync(groupId, msgId, "该内容无法显示");
            return true;
        }

        await SendGroupMessageAsync(groupId, msgId, callText);
        return true;
    }

    private static async Task<bool> TryHandleCallEchoInPrivateAsync(string normalizedMsg, string userId)
    {
        if (!TryExtractCallText(normalizedMsg, out var callText))
        {
            return false;
        }

        var blocked = await IsCallTextBlockedAsync(callText);
        LogCallEchoText("private", null, userId, callText, blocked ? "moderation_blocked" : "allowed");
        if (blocked)
        {
            await SendPrivateMessageAsync(userId, "该内容无法显示");
            return true;
        }

        await SendPrivateMessageAsync(userId, callText);
        return true;
    }

    private static GroupCallLimitReason CheckGroupCallLimit(string groupId, string callText)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return GroupCallLimitReason.None;
        }

        var now = DateTimeOffset.UtcNow;
        var contentKey = BuildCompactModerationText(callText);
        if (string.IsNullOrWhiteSpace(contentKey))
        {
            contentKey = NormalizeCallText(callText).ToLowerInvariant();
        }

        lock (_groupCallRateLimitLock)
        {
            if (!_groupCallRateLimitStates.TryGetValue(groupId, out var state))
            {
                state = new GroupCallRateLimitState();
                _groupCallRateLimitStates[groupId] = state;
            }

            state.LastSeenUtc = now;
            var windowStart = now - GroupCallRateLimitWindow;
            while (state.MessageTimesUtc.Count > 0 && state.MessageTimesUtc.Peek() < windowStart)
            {
                state.MessageTimesUtc.Dequeue();
            }

            state.MessageTimesUtc.Enqueue(now);

            if (string.Equals(state.LastContentKey, contentKey, StringComparison.Ordinal))
            {
                state.LastContentStreak++;
            }
            else
            {
                state.LastContentKey = contentKey;
                state.LastContentStreak = 1;
            }

            TrimGroupCallRateLimitStatesUnsafe(now);

            if (state.LastContentStreak >= GroupCallRepeatedContentBlockThreshold)
            {
                return GroupCallLimitReason.RepeatedContent;
            }

            if (state.MessageTimesUtc.Count > GroupCallRateLimitPerMinute)
            {
                return GroupCallLimitReason.TooFrequent;
            }

            return GroupCallLimitReason.None;
        }
    }

    private static void TrimGroupCallRateLimitStatesUnsafe(DateTimeOffset now)
    {
        if (_groupCallRateLimitStates.Count == 0)
        {
            return;
        }

        var staleBefore = now - GroupCallRateLimitStateTtl;
        List<string>? staleKeys = null;
        foreach (var pair in _groupCallRateLimitStates)
        {
            if (pair.Value.LastSeenUtc >= staleBefore)
            {
                continue;
            }

            staleKeys ??= new List<string>();
            staleKeys.Add(pair.Key);
        }

        if (staleKeys == null || staleKeys.Count == 0)
        {
            return;
        }

        foreach (var key in staleKeys)
        {
            _groupCallRateLimitStates.Remove(key);
        }
    }

    private sealed record IdiomRoundResult(bool Valid, string Normalized, string Next, string Reason);
    private sealed record IdiomSuggestResult(bool Success, string Idiom, string Reason);

    private static async Task<bool> TryHandleNapcatIdiomChainAsync(string groupId, string msgId, string? userId, string normalizedMsg)
    {
        if (string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(normalizedMsg))
        {
            return false;
        }

        if (TryGetPrefixedCommandToken(normalizedMsg, out var prefixedCmd)
            && !IsIdiomChainCommandToken(prefixedCmd))
        {
            return false;
        }

        var controlText = NormalizeIdiomControlText(normalizedMsg);
        var hasSession = TryGetActiveIdiomChainSession(groupId, out var session);

        if (IsIdiomChainStopText(controlText))
        {
            if (!hasSession || session == null)
            {
                await SendGroupMessageAsync(groupId, msgId, "ℹ️ 当前没有进行中的成语接龙。发送“成语接龙”即可开始。");
                return true;
            }

            await session.Gate.WaitAsync();
            try
            {
                var usedCount = session.UsedIdioms.Count;
                _idiomChainSessions.TryRemove(groupId, out _);
                PersistRuntimeStateToDisk();
                _dataStore.IncrementNapcatUsage();
                await SendGroupMessageAsync(groupId, msgId, $"✅ 已结束本群成语接龙，共记录 {usedCount} 个成语。");
            }
            finally
            {
                session.Gate.Release();
            }

            return true;
        }

        if (TryParseIdiomChainStartRequest(controlText, out var openingIdiomRaw))
        {
            session ??= _idiomChainSessions.GetOrAdd(groupId, _ => new IdiomChainSession());
            await session.Gate.WaitAsync();
            try
            {
                session.UsedIdioms.Clear();
                session.LastIdiom = string.Empty;
                session.ExpectedStartChar = string.Empty;
                session.LastUpdatedUtc = DateTimeOffset.UtcNow;
                PersistRuntimeStateToDisk();

                if (string.IsNullOrWhiteSpace(openingIdiomRaw))
                {
                    var start = await RequestDeepSeekIdiomSuggestionAsync(string.Empty, session.UsedIdioms);
                    if (!start.Success || !TryNormalizeIdiomText(start.Idiom, out var firstIdiom))
                    {
                        var reason = string.IsNullOrWhiteSpace(start.Reason) ? "DeepSeek 暂时不可用，请稍后重试。" : start.Reason;
                        await SendGroupMessageAsync(groupId, msgId, $"❌ 成语接龙启动失败：{reason}");
                        return true;
                    }

                    session.UsedIdioms.Add(firstIdiom);
                    session.LastIdiom = firstIdiom;
                    session.ExpectedStartChar = GetLastChineseChar(firstIdiom);
                    session.LastUpdatedUtc = DateTimeOffset.UtcNow;
                    PersistRuntimeStateToDisk();
                    _dataStore.IncrementNapcatUsage();

                    await SendGroupMessageAsync(
                        groupId,
                        msgId,
                        $"🎯 成语接龙开始！我先来：{firstIdiom}\n请接以“{session.ExpectedStartChar}”开头的四字成语。\n发送“结束接龙”可结束，发送“接龙提示”可求助。");
                    return true;
                }

                if (!TryNormalizeIdiomText(openingIdiomRaw, out var openingIdiom))
                {
                    await SendGroupMessageAsync(groupId, msgId, "❌ 开局成语格式不正确，请发送标准四字成语。");
                    return true;
                }

                var round = await RequestDeepSeekIdiomRoundAsync(openingIdiom, string.Empty, session.UsedIdioms);
                if (!round.Valid)
                {
                    await SendGroupMessageAsync(groupId, msgId, $"❌ 开局失败：{round.Reason}");
                    return true;
                }

                session.UsedIdioms.Add(round.Normalized);
                if (!string.IsNullOrWhiteSpace(round.Next))
                {
                    session.UsedIdioms.Add(round.Next);
                }

                if (session.UsedIdioms.Count > IdiomChainMaxUsedCount)
                {
                    session.UsedIdioms.Clear();
                    session.UsedIdioms.Add(round.Normalized);
                    if (!string.IsNullOrWhiteSpace(round.Next))
                    {
                        session.UsedIdioms.Add(round.Next);
                    }
                }

                if (string.IsNullOrWhiteSpace(round.Next))
                {
                    _idiomChainSessions.TryRemove(groupId, out _);
                    PersistRuntimeStateToDisk();
                    _dataStore.IncrementNapcatUsage();
                    await SendGroupMessageAsync(
                        groupId,
                        msgId,
                        $"✅ 开局成语：{round.Normalized}\n我一时接不上，你赢了！发送“成语接龙”可再开一局。");
                    return true;
                }

                session.LastIdiom = round.Next;
                session.ExpectedStartChar = GetLastChineseChar(round.Next);
                session.LastUpdatedUtc = DateTimeOffset.UtcNow;
                PersistRuntimeStateToDisk();
                _dataStore.IncrementNapcatUsage();

                await SendGroupMessageAsync(
                    groupId,
                    msgId,
                    $"✅ 开局成语：{round.Normalized}\n我接：{round.Next}\n请接以“{session.ExpectedStartChar}”开头的四字成语。");
                return true;
            }
            finally
            {
                session.Gate.Release();
            }
        }

        if (!hasSession || session == null)
        {
            return false;
        }

        if (IsIdiomChainHintText(controlText))
        {
            await session.Gate.WaitAsync();
            try
            {
                var expected = session.ExpectedStartChar;
                if (string.IsNullOrWhiteSpace(expected))
                {
                    await SendGroupMessageAsync(groupId, msgId, "ℹ️ 当前没有等待接龙的首字，请重新发送“成语接龙”开始。");
                    return true;
                }

                var hint = await RequestDeepSeekIdiomSuggestionAsync(expected, session.UsedIdioms);
                if (!hint.Success || !TryNormalizeIdiomText(hint.Idiom, out var hintIdiom))
                {
                    var reason = string.IsNullOrWhiteSpace(hint.Reason) ? "暂时想不到提示，你可以换个成语试试。" : hint.Reason;
                    await SendGroupMessageAsync(groupId, msgId, $"ℹ️ {reason}");
                    return true;
                }

                await SendGroupMessageAsync(groupId, msgId, $"💡 提示：可以试试“{hintIdiom}”（首字“{expected}”）。");
                _dataStore.IncrementNapcatUsage();
                return true;
            }
            finally
            {
                session.Gate.Release();
            }
        }

        if (!TryNormalizeIdiomText(controlText, out var userIdiom))
        {
            return false;
        }

        await session.Gate.WaitAsync();
        try
        {
            if (DateTimeOffset.UtcNow - session.LastUpdatedUtc > IdiomChainSessionTimeout)
            {
                _idiomChainSessions.TryRemove(groupId, out _);
                PersistRuntimeStateToDisk();
                await SendGroupMessageAsync(groupId, msgId, "⌛ 这局成语接龙已超时结束。发送“成语接龙”可重新开始。");
                return true;
            }

            if (!string.IsNullOrWhiteSpace(session.ExpectedStartChar))
            {
                var expected = session.ExpectedStartChar[0];
                if (userIdiom[0] != expected)
                {
                    await SendGroupMessageAsync(groupId, msgId, $"❌ 这个成语应以“{session.ExpectedStartChar}”开头，请重试。");
                    return true;
                }
            }

            if (session.UsedIdioms.Contains(userIdiom))
            {
                await SendGroupMessageAsync(groupId, msgId, $"❌ “{userIdiom}”已经用过了，换一个。");
                return true;
            }

            var roundResult = await RequestDeepSeekIdiomRoundAsync(userIdiom, session.ExpectedStartChar, session.UsedIdioms);
            if (!roundResult.Valid)
            {
                await SendGroupMessageAsync(groupId, msgId, $"❌ {roundResult.Reason}");
                return true;
            }

            session.UsedIdioms.Add(roundResult.Normalized);
            if (!string.IsNullOrWhiteSpace(roundResult.Next))
            {
                session.UsedIdioms.Add(roundResult.Next);
            }

            if (session.UsedIdioms.Count > IdiomChainMaxUsedCount)
            {
                session.UsedIdioms.Clear();
                session.UsedIdioms.Add(roundResult.Normalized);
                if (!string.IsNullOrWhiteSpace(roundResult.Next))
                {
                    session.UsedIdioms.Add(roundResult.Next);
                }
            }

            if (string.IsNullOrWhiteSpace(roundResult.Next))
            {
                _idiomChainSessions.TryRemove(groupId, out _);
                PersistRuntimeStateToDisk();
                _dataStore.IncrementNapcatUsage();
                await SendGroupMessageAsync(
                    groupId,
                    msgId,
                    $"✅ 你出：{roundResult.Normalized}\n我接不上了，这局你赢！发送“成语接龙”可再来一局。");
                return true;
            }

            session.LastIdiom = roundResult.Next;
            session.ExpectedStartChar = GetLastChineseChar(roundResult.Next);
            session.LastUpdatedUtc = DateTimeOffset.UtcNow;
            PersistRuntimeStateToDisk();
            _dataStore.IncrementNapcatUsage();
            await SendGroupMessageAsync(
                groupId,
                msgId,
                $"✅ 你出：{roundResult.Normalized}\n🤖 我接：{roundResult.Next}\n请接以“{session.ExpectedStartChar}”开头的四字成语。");
            return true;
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private static bool TryGetActiveIdiomChainSession(string groupId, out IdiomChainSession? session)
    {
        session = null;
        if (!_idiomChainSessions.TryGetValue(groupId, out var existing))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - existing.LastUpdatedUtc > IdiomChainSessionTimeout)
        {
            _idiomChainSessions.TryRemove(groupId, out _);
            PersistRuntimeStateToDisk();
            return false;
        }

        session = existing;
        return true;
    }

    private static bool TryGetPrefixedCommandToken(string text, out string commandToken)
    {
        commandToken = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.TrimStart();
        if (trimmed.Length == 0)
        {
            return false;
        }

        var first = trimmed[0];
        if (first != '/' && first != '!' && first != '=' && first != '／' && first != '！' && first != '＝')
        {
            return false;
        }

        trimmed = trimmed[1..].TrimStart();
        if (trimmed.Length == 0)
        {
            return false;
        }

        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        commandToken = NormalizeCommand(parts[0]);
        return !string.IsNullOrWhiteSpace(commandToken);
    }

    private static bool IsIdiomChainCommandToken(string commandToken)
    {
        return commandToken is "成语接龙" or "接龙"
            or "结束接龙" or "停止接龙" or "退出接龙" or "结束成语接龙" or "停止成语接龙"
            or "接龙提示" or "提示接龙";
    }

    private static string NormalizeIdiomControlText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.Replace('\u3000', ' ').Trim();
        while (normalized.Length > 0)
        {
            var first = normalized[0];
            if (first != '/' && first != '!' && first != '=' && first != '／' && first != '！' && first != '＝')
            {
                break;
            }

            normalized = normalized[1..].TrimStart();
        }

        return normalized.Trim();
    }

    private static bool TryParseIdiomChainStartRequest(string controlText, out string openingIdiom)
    {
        openingIdiom = string.Empty;
        if (string.IsNullOrWhiteSpace(controlText))
        {
            return false;
        }

        var text = controlText.Trim();
        if (text.Equals("成语接龙", StringComparison.OrdinalIgnoreCase)
            || text.Equals("接龙", StringComparison.OrdinalIgnoreCase)
            || text.Equals("来个成语接龙", StringComparison.OrdinalIgnoreCase)
            || text.Equals("来一局成语接龙", StringComparison.OrdinalIgnoreCase)
            || text.Equals("玩成语接龙", StringComparison.OrdinalIgnoreCase)
            || text.Equals("开始成语接龙", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (text.StartsWith("成语接龙", StringComparison.OrdinalIgnoreCase))
        {
            var tail = text["成语接龙".Length..].Trim();
            if (string.IsNullOrWhiteSpace(tail))
            {
                return true;
            }

            if (IsIdiomChainStopText(tail) || IsIdiomChainHintText(tail))
            {
                return false;
            }

            openingIdiom = tail;
            return true;
        }

        if (text.StartsWith("接龙", StringComparison.OrdinalIgnoreCase))
        {
            var tail = text["接龙".Length..].Trim();
            if (string.IsNullOrWhiteSpace(tail))
            {
                return true;
            }

            if (IsIdiomChainStopText(tail) || IsIdiomChainHintText(tail))
            {
                return false;
            }

            openingIdiom = tail;
            return true;
        }

        return false;
    }

    private static bool IsIdiomChainStopText(string controlText)
    {
        if (string.IsNullOrWhiteSpace(controlText))
        {
            return false;
        }

        var text = controlText.Trim();
        return text is "结束接龙" or "停止接龙" or "退出接龙"
            or "结束成语接龙" or "停止成语接龙"
            or "接龙结束" or "接龙停止" or "接龙退出"
            or "不玩了";
    }

    private static bool IsIdiomChainHintText(string controlText)
    {
        if (string.IsNullOrWhiteSpace(controlText))
        {
            return false;
        }

        var text = controlText.Trim();
        return text is "提示" or "接龙提示" or "提示接龙" or "来个提示" or "不会" or "接不上了";
    }

    private static bool TryNormalizeIdiomText(string text, out string idiom)
    {
        idiom = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = text.Replace('\u3000', ' ').Trim();
        var builder = new StringBuilder(4);
        foreach (var ch in normalized)
        {
            if (IsChineseChar(ch))
            {
                builder.Append(ch);
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                continue;
            }

            if ("，,。！？!?；;：:\"“”‘’'()（）【】[]《》<>-—·.".Contains(ch))
            {
                continue;
            }

            return false;
        }

        if (builder.Length != 4)
        {
            return false;
        }

        idiom = builder.ToString();
        return true;
    }

    private static bool IsChineseChar(char ch)
    {
        return ch >= '\u4E00' && ch <= '\u9FFF';
    }

    private static string GetLastChineseChar(string idiom)
    {
        if (string.IsNullOrWhiteSpace(idiom))
        {
            return string.Empty;
        }

        for (var i = idiom.Length - 1; i >= 0; i--)
        {
            if (IsChineseChar(idiom[i]))
            {
                return idiom[i].ToString();
            }
        }

        return string.Empty;
    }

    private static string BuildUsedIdiomsPrompt(IReadOnlyCollection<string> usedIdioms)
    {
        if (usedIdioms == null || usedIdioms.Count == 0)
        {
            return "(空)";
        }

        var text = string.Join("、", usedIdioms.Take(120));
        if (text.Length > 1500)
        {
            text = text[..1500];
        }

        return text;
    }

    private static async Task<IdiomRoundResult> RequestDeepSeekIdiomRoundAsync(string userIdiom, string expectedStartChar, IReadOnlyCollection<string> usedIdioms)
    {
        try
        {
            var body = new
            {
                model = DeepSeekModelName,
                temperature = 0.2,
                messages = new object[]
                {
                    new
                    {
                        role = "system",
                        content = "你是中文成语接龙裁判。仅输出单个JSON对象，不要markdown。字段：{\"valid\":true/false,\"normalized\":\"四字成语\",\"next\":\"四字成语或空字符串\",\"reason\":\"简短中文\"}。规则：1) normalized必须是常见四字成语；2) 若expected_start非空，normalized首字必须等于expected_start；3) normalized与next都不能出现在used；4) next首字必须等于normalized末字；5) 若玩家输入不合法，valid=false且next为空；6) 若玩家合法但你接不上，valid=true且next为空。"
                    },
                    new
                    {
                        role = "user",
                        content = $"expected_start={expectedStartChar}\nplayer_idiom={userIdiom}\nused={BuildUsedIdiomsPrompt(usedIdioms)}\n请返回JSON。"
                    }
                }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, DeepSeekApiUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DeepSeekApiKey);
            request.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[成语接龙] DeepSeek请求失败: status={(int)response.StatusCode}, body={content}");
                return new IdiomRoundResult(false, userIdiom, string.Empty, "DeepSeek 请求失败，请稍后重试。");
            }

            var root = JObject.Parse(content);
            var raw = root.SelectToken("choices[0].message.content")?.ToString() ?? string.Empty;
            var jsonText = ExtractJsonObject(raw);
            if (string.IsNullOrWhiteSpace(jsonText))
            {
                return new IdiomRoundResult(false, userIdiom, string.Empty, "DeepSeek 返回格式异常。");
            }

            var obj = JObject.Parse(jsonText);
            var validToken = obj["valid"] ?? obj["ok"] ?? obj["accepted"] ?? obj["正确"];
            var reason = obj["reason"]?.ToString() ?? obj["message"]?.ToString() ?? "不符合成语接龙规则。";
            var normalizedRaw = obj["normalized"]?.ToString() ?? obj["idiom"]?.ToString() ?? userIdiom;
            var nextRaw = obj["next"]?.ToString() ?? obj["bot"]?.ToString() ?? string.Empty;

            var valid = false;
            if (validToken != null)
            {
                _ = TryParseBoolToken(validToken, out valid);
            }

            if (!TryNormalizeIdiomText(normalizedRaw, out var normalized))
            {
                normalized = userIdiom;
            }

            if (!valid)
            {
                if (string.IsNullOrWhiteSpace(reason))
                {
                    reason = "这个词不是有效的四字成语。";
                }

                return new IdiomRoundResult(false, normalized, string.Empty, reason);
            }

            if (!string.IsNullOrWhiteSpace(expectedStartChar) && normalized[0].ToString() != expectedStartChar)
            {
                return new IdiomRoundResult(false, normalized, string.Empty, $"应以“{expectedStartChar}”开头。");
            }

            if (usedIdioms.Contains(normalized))
            {
                return new IdiomRoundResult(false, normalized, string.Empty, $"“{normalized}”已经用过了。");
            }

            if (!TryNormalizeIdiomText(nextRaw, out var next))
            {
                return new IdiomRoundResult(true, normalized, string.Empty, reason);
            }

            var expectedNextStart = GetLastChineseChar(normalized);
            if (string.IsNullOrWhiteSpace(expectedNextStart) || next[0].ToString() != expectedNextStart)
            {
                return new IdiomRoundResult(true, normalized, string.Empty, reason);
            }

            if (usedIdioms.Contains(next) || string.Equals(next, normalized, StringComparison.Ordinal))
            {
                return new IdiomRoundResult(true, normalized, string.Empty, reason);
            }

            return new IdiomRoundResult(true, normalized, next, reason);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[成语接龙] DeepSeek回合异常: {ex.Message}");
            return new IdiomRoundResult(false, userIdiom, string.Empty, "DeepSeek 异常，请稍后重试。");
        }
    }

    private static async Task<IdiomSuggestResult> RequestDeepSeekIdiomSuggestionAsync(string expectedStartChar, IReadOnlyCollection<string> usedIdioms)
    {
        try
        {
            var body = new
            {
                model = DeepSeekModelName,
                temperature = 0.8,
                messages = new object[]
                {
                    new
                    {
                        role = "system",
                        content = "你是中文成语助手。请只输出单个JSON对象，不要markdown。字段：{\"idiom\":\"四字成语\",\"reason\":\"简短中文\"}。要求：idiom必须是常见四字成语，且不在used列表；若expected_start非空，idiom首字必须等于expected_start。"
                    },
                    new
                    {
                        role = "user",
                        content = $"expected_start={expectedStartChar}\nused={BuildUsedIdiomsPrompt(usedIdioms)}\n请返回JSON。"
                    }
                }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, DeepSeekApiUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DeepSeekApiKey);
            request.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[成语接龙] DeepSeek提示失败: status={(int)response.StatusCode}, body={content}");
                return new IdiomSuggestResult(false, string.Empty, "DeepSeek 请求失败。");
            }

            var root = JObject.Parse(content);
            var raw = root.SelectToken("choices[0].message.content")?.ToString() ?? string.Empty;
            var jsonText = ExtractJsonObject(raw);
            if (string.IsNullOrWhiteSpace(jsonText))
            {
                return new IdiomSuggestResult(false, string.Empty, "DeepSeek 返回格式异常。");
            }

            var obj = JObject.Parse(jsonText);
            var idiomRaw = obj["idiom"]?.ToString() ?? obj["next"]?.ToString() ?? obj["word"]?.ToString() ?? string.Empty;
            var reason = obj["reason"]?.ToString() ?? string.Empty;
            if (!TryNormalizeIdiomText(idiomRaw, out var idiom))
            {
                return new IdiomSuggestResult(false, string.Empty, "DeepSeek 未返回有效四字成语。");
            }

            if (!string.IsNullOrWhiteSpace(expectedStartChar) && idiom[0].ToString() != expectedStartChar)
            {
                return new IdiomSuggestResult(false, string.Empty, "DeepSeek 提示首字不匹配。");
            }

            if (usedIdioms.Contains(idiom))
            {
                return new IdiomSuggestResult(false, string.Empty, "DeepSeek 提示成语已用过。");
            }

            return new IdiomSuggestResult(true, idiom, reason);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[成语接龙] DeepSeek提示异常: {ex.Message}");
            return new IdiomSuggestResult(false, string.Empty, "DeepSeek 异常。");
        }
    }

    private static bool TryExtractCallText(string input, out string callText)
    {
        callText = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var text = input.Trim();
        if (!text.StartsWith("叫", StringComparison.Ordinal))
        {
            return false;
        }

        if (text.Length == 1)
        {
            return false;
        }

        var payload = NormalizeCallText(text[1..]);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        callText = payload;
        return true;
    }

    private static string NormalizeCallText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.Normalize(NormalizationForm.FormKC).Replace('\u3000', ' ');
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (ch is '\u200B' or '\u200C' or '\u200D' or '\u2060' or '\uFEFF')
            {
                continue;
            }

            sb.Append(ch);
        }

        return sb.ToString().Trim();
    }

    private static string BuildCompactModerationText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = NormalizeCallText(text);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (char.IsWhiteSpace(ch))
            {
                continue;
            }

            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            var isSeparator =
                category is UnicodeCategory.Control
                    or UnicodeCategory.Format
                    or UnicodeCategory.SpaceSeparator
                    or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator
                    or UnicodeCategory.ConnectorPunctuation
                    or UnicodeCategory.DashPunctuation
                    or UnicodeCategory.OpenPunctuation
                    or UnicodeCategory.ClosePunctuation
                    or UnicodeCategory.InitialQuotePunctuation
                    or UnicodeCategory.FinalQuotePunctuation
                    or UnicodeCategory.OtherPunctuation
                    or UnicodeCategory.MathSymbol
                    or UnicodeCategory.CurrencySymbol
                    or UnicodeCategory.ModifierSymbol
                    or UnicodeCategory.OtherSymbol;
            if (isSeparator)
            {
                continue;
            }

            sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString();
    }

    private static async Task<bool> IsCallTextBlockedAsync(string text)
    {
        var normalized = NormalizeCallText(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (IsFamilyCallTextAllowed(normalized))
        {
            return false;
        }

        var compact = BuildCompactModerationText(normalized);
        var cacheKey = string.IsNullOrWhiteSpace(compact) ? normalized : $"{normalized}||{compact}";
        if (_callModerationCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        bool blocked;
        if (_aiModerationEnabled)
        {
            blocked = await RequestDeepSeekCallModerationAsync(normalized, compact);
        }
        else
        {
            blocked = HasLocalBlockedKeyword(normalized);
        }

        _callModerationCache[cacheKey] = blocked;
        TrimCallModerationCacheIfNeeded();
        return blocked;
    }

    private static async Task<bool> RequestDeepSeekCallModerationAsync(string text, string compactText)
    {
        try
        {
            var body = new
            {
                model = DeepSeekModelName,
                temperature = 0.0,
                messages = new object[]
                {
                    new
                    {
                        role = "system",
                        content = "你是中文内容审核器。拦截三类：1) 政治相关敏感/涉政词汇；2) 任何性暗示、性器官描述、色情擦边或露骨性内容；3) 发泄类/发癫文（大量情绪宣泄、崩溃式表达），即使不含辱骂也算违规。明确放行：仅家庭称谓如“爸爸”“妈妈”“爸爸妈妈”不违规。要识别绕过写法：谐音、拆字、空格或符号穿插、拼音/英文替换。只输出JSON：{\"blocked\":true/false,\"category\":\"politics|sexual|venting|other|none\",\"severity\":\"high|medium|low\",\"reason\":\"简短中文\"}。"
                    },
                    new
                    {
                        role = "user",
                        content = $"请按上述规则审核。\n原文：{text}\n规整文本：{compactText}\n请识别可能的谐音/变体绕过。"
                    }
                }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, DeepSeekApiUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DeepSeekApiKey);
            request.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[叫词审核] DeepSeek请求失败: status={(int)response.StatusCode}, body={content}");
                return HasLocalBlockedKeyword(text);
            }

            var root = JObject.Parse(content);
            var raw = root.SelectToken("choices[0].message.content")?.ToString() ?? string.Empty;
            if (ContainsDeepSeekViolationHint(raw))
            {
                return ShouldBlockByDeepSeekResult(true, "other", "medium", raw, text);
            }

            var jsonText = ExtractJsonObject(raw);
            if (string.IsNullOrWhiteSpace(jsonText))
            {
                return HasLocalBlockedKeyword(text);
            }

            var obj = JObject.Parse(jsonText);
            var blockedToken = obj["blocked"] ?? obj["violation"] ?? obj["违规"];
            var category = obj["category"]?.ToString()
                           ?? obj["type"]?.ToString()
                           ?? obj["类别"]?.ToString();
            var severity = obj["severity"]?.ToString()
                           ?? obj["level"]?.ToString()
                           ?? obj["尺度"]?.ToString()
                           ?? obj["risk"]?.ToString();
            var reason = obj["reason"]?.ToString()
                         ?? obj["原因"]?.ToString();
            if (blockedToken != null && TryParseBoolToken(blockedToken, out var blocked))
            {
                return ShouldBlockByDeepSeekResult(blocked, category, severity, reason, text);
            }

            if (ShouldBlockByDeepSeekResult(true, category, severity, reason, text))
            {
                return true;
            }

            return HasLocalBlockedKeyword(text);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[叫词审核] DeepSeek异常: {ex.Message}");
            return HasLocalBlockedKeyword(text);
        }
    }

    private static bool ShouldBlockByDeepSeekResult(bool blocked, string? category, string? severity, string? reason, string originalText)
    {
        if (IsFamilyCallTextAllowed(originalText))
        {
            return false;
        }

        if (IsPoliticsModeration(category, reason))
        {
            return !IsBenignCountryMention(originalText, category, reason);
        }

        if (IsSexualModeration(category, severity, reason, originalText))
        {
            return true;
        }

        if (IsVentingModeration(category, reason, originalText))
        {
            return true;
        }

        return blocked;
    }

    private static bool IsBenignCountryMention(string originalText, string? category, string? reason)
    {
        var text = $"{originalText} {category} {reason}";
        var normalized = NormalizeCallText(text);
        var compact = BuildCompactModerationText(normalized);
        if (string.IsNullOrWhiteSpace(compact))
        {
            return false;
        }

        var hasGeoTerm = false;
        foreach (var term in BenignGeoTerms)
        {
            var termCompact = BuildCompactModerationText(term);
            if (string.IsNullOrWhiteSpace(termCompact))
            {
                continue;
            }

            if (compact.Contains(termCompact, StringComparison.Ordinal))
            {
                hasGeoTerm = true;
                break;
            }
        }

        if (!hasGeoTerm)
        {
            return false;
        }

        foreach (var signal in PoliticalEscalationSignals)
        {
            var signalCompact = BuildCompactModerationText(signal);
            if (string.IsNullOrWhiteSpace(signalCompact))
            {
                continue;
            }

            if (compact.Contains(signalCompact, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPoliticsModeration(string? category, string? reason)
    {
        var text = $"{category} {reason}".ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("politic", StringComparison.Ordinal)
               || text.Contains("政治", StringComparison.Ordinal)
               || text.Contains("涉政", StringComparison.Ordinal)
               || text.Contains("政权", StringComparison.Ordinal)
               || text.Contains("领导人", StringComparison.Ordinal)
               || text.Contains("煽动颠覆", StringComparison.Ordinal);
    }

    private static bool IsSexualModeration(string? category, string? severity, string? reason, string originalText)
    {
        var text = $"{category} {severity} {reason}".ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text))
        {
            return HasLocalBlockedKeyword(originalText);
        }

        var hasSexSignal =
            text.Contains("sexual", StringComparison.Ordinal)
            || text.Contains("sex", StringComparison.Ordinal)
            || text.Contains("性暗示", StringComparison.Ordinal)
            || text.Contains("性器官", StringComparison.Ordinal)
            || text.Contains("擦边", StringComparison.Ordinal)
            || text.Contains("露骨", StringComparison.Ordinal)
            || text.Contains("淫秽", StringComparison.Ordinal)
            || text.Contains("性交易", StringComparison.Ordinal)
            || text.Contains("未成年", StringComparison.Ordinal)
            || text.Contains("强奸", StringComparison.Ordinal)
            || text.Contains("性交", StringComparison.Ordinal)
            || text.Contains("口交", StringComparison.Ordinal)
            || text.Contains("肛交", StringComparison.Ordinal)
            || text.Contains("porn", StringComparison.Ordinal)
            || text.Contains("成人视频", StringComparison.Ordinal)
            || text.Contains("嫖娼", StringComparison.Ordinal)
            || text.Contains("援交", StringComparison.Ordinal)
            || text.Contains("裸聊", StringComparison.Ordinal);

        if (hasSexSignal)
        {
            return true;
        }

        return HasLocalBlockedKeyword(originalText);
    }

    private static bool IsVentingModeration(string? category, string? reason, string originalText)
    {
        var text = $"{category} {reason}".ToLowerInvariant();
        if (text.Contains("vent", StringComparison.Ordinal)
            || text.Contains("发泄", StringComparison.Ordinal)
            || text.Contains("发癫", StringComparison.Ordinal)
            || text.Contains("崩溃", StringComparison.Ordinal)
            || text.Contains("情绪宣泄", StringComparison.Ordinal))
        {
            return true;
        }

        return HasLocalVentingPattern(originalText);
    }

    private static bool ContainsDeepSeekViolationHint(string rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            return false;
        }

        var rawLower = rawResponse.ToLowerInvariant();
        if (rawLower.Contains("\"blocked\":false", StringComparison.Ordinal)
            || rawLower.Contains("\"blocked\" : false", StringComparison.Ordinal)
            || rawLower.Contains("\"violation\":false", StringComparison.Ordinal)
            || rawLower.Contains("\"violation\" : false", StringComparison.Ordinal))
        {
            return false;
        }

        if (rawLower.Contains("\"blocked\":true", StringComparison.Ordinal)
            || rawLower.Contains("\"blocked\" : true", StringComparison.Ordinal)
            || rawLower.Contains("\"violation\":true", StringComparison.Ordinal)
            || rawLower.Contains("\"violation\" : true", StringComparison.Ordinal))
        {
            return true;
        }

        var normalized = NormalizeCallText(rawResponse).ToLowerInvariant();
        if (normalized.Contains("不违规", StringComparison.Ordinal)
            || normalized.Contains("未违规", StringComparison.Ordinal)
            || normalized.Contains("未检测到违规", StringComparison.Ordinal)
            || normalized.Contains("未发现违规", StringComparison.Ordinal)
            || normalized.Contains("无违规", StringComparison.Ordinal))
        {
            return false;
        }

        return normalized.Contains("检测到违规", StringComparison.Ordinal)
               || normalized.Contains("违规", StringComparison.Ordinal)
               || normalized.Contains("violation", StringComparison.Ordinal)
               || normalized.Contains("blocked", StringComparison.Ordinal);
    }

    private static bool TryParseBoolToken(JToken token, out bool value)
    {
        value = false;
        if (token.Type == JTokenType.Boolean)
        {
            value = token.Value<bool>();
            return true;
        }

        var raw = token.ToString().Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (bool.TryParse(raw, out value))
        {
            return true;
        }

        if (raw is "1" or "是" or "违规" or "true" or "True")
        {
            value = true;
            return true;
        }

        if (raw is "0" or "否" or "不违规" or "false" or "False")
        {
            value = false;
            return true;
        }

        return false;
    }

    private static string ExtractJsonObject(string text)
    {
        var raw = (text ?? string.Empty).Trim();
        if (raw.StartsWith("```", StringComparison.Ordinal))
        {
            var firstBrace = raw.IndexOf('{');
            var lastBrace = raw.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
            {
                return raw[firstBrace..(lastBrace + 1)];
            }
        }

        if (raw.StartsWith("{", StringComparison.Ordinal) && raw.EndsWith("}", StringComparison.Ordinal))
        {
            return raw;
        }

        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return raw[start..(end + 1)];
        }

        return string.Empty;
    }

    private static bool HasLocalBlockedKeyword(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (IsFamilyCallTextAllowed(text))
        {
            return false;
        }

        var normalized = NormalizeCallText(text).ToLowerInvariant();
        var compact = BuildCompactModerationText(normalized);
        foreach (var keyword in LocalBlockedCallKeywords)
        {
            var normalizedKeyword = NormalizeCallText(keyword).ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(normalizedKeyword)
                && normalized.Contains(normalizedKeyword, StringComparison.Ordinal))
            {
                return true;
            }

            var compactKeyword = BuildCompactModerationText(keyword);
            if (!string.IsNullOrWhiteSpace(compactKeyword)
                && compact.Contains(compactKeyword, StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (var keyword in LocalBlockedCallVentingKeywords)
        {
            var normalizedKeyword = NormalizeCallText(keyword).ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(normalizedKeyword)
                && normalized.Contains(normalizedKeyword, StringComparison.Ordinal))
            {
                return true;
            }

            var compactKeyword = BuildCompactModerationText(keyword);
            if (!string.IsNullOrWhiteSpace(compactKeyword)
                && compact.Contains(compactKeyword, StringComparison.Ordinal))
            {
                return true;
            }
        }

        if (HasLocalVentingPattern(normalized))
        {
            return true;
        }

        return false;
    }

    private static bool IsFamilyCallTextAllowed(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var compact = BuildCompactModerationText(text);
        if (string.IsNullOrWhiteSpace(compact))
        {
            return false;
        }

        foreach (var keyword in CallTextFamilyAllowKeywords)
        {
            var compactKeyword = BuildCompactModerationText(keyword);
            if (!string.IsNullOrWhiteSpace(compactKeyword)
                && string.Equals(compact, compactKeyword, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasLocalVentingPattern(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = NormalizeCallText(text).ToLowerInvariant();
        if (RepeatedEmotionalCharRegex.IsMatch(normalized))
        {
            return true;
        }

        if (RepeatedEmotionPunctuationRegex.IsMatch(normalized))
        {
            return true;
        }

        return false;
    }

    private static async Task HandleCustomTitleCommandAsync(string[] parts, string? groupId, string? msgId, string? userId)
    {
        if (string.IsNullOrWhiteSpace(groupId) || NeedMsgIdButMissing(msgId) || string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        if (parts.Length < 3 || !parts[1].Equals("add", StringComparison.OrdinalIgnoreCase))
        {
            await SendGroupMessageAsync(groupId, msgId, "❌ 用法: /ch add <称号> [颜色代码]（不填颜色默认 478978，颜色支持带#或不带#）");
            return;
        }

        if (!_bindService.TryGetBindingByQq(userId, out var binding) || string.IsNullOrWhiteSpace(binding.BjdUuid))
        {
            await SendGroupMessageAsync(groupId, msgId, "❌ 未检测到你的绑定信息，请先执行 !bind <布吉岛用户名>");
            return;
        }

        const string defaultColorHex = "478978";
        var colorHex = defaultColorHex;
        string title;
        if (parts.Length >= 4)
        {
            var parsedColor = NormalizeHexColor(parts[^1]);
            if (parsedColor != null)
            {
                colorHex = parsedColor;
                title = string.Join(" ", parts.Skip(2).Take(parts.Length - 3)).Trim();
            }
            else
            {
                title = string.Join(" ", parts.Skip(2)).Trim();
            }
        }
        else
        {
            title = parts[2].Trim();
        }
        if (string.IsNullOrWhiteSpace(title))
        {
            await SendGroupMessageAsync(groupId, msgId, "❌ 称号不能为空。");
            return;
        }

        if (title.Length > 24)
        {
            await SendGroupMessageAsync(groupId, msgId, "❌ 称号过长，请控制在 24 个字符以内。");
            return;
        }

        if (!string.IsNullOrWhiteSpace(_adminQq) && string.Equals(userId, _adminQq, StringComparison.Ordinal))
        {
            _dataStore.UpsertCustomTitle(binding.BjdUuid, title, colorHex);
            await SendGroupMessageAsync(groupId, msgId, $"✅ 管理员直通成功：称号已设置为 {title} (#{colorHex})");
            return;
        }

        if (!string.Equals(groupId, CustomTitleReviewGroupId, StringComparison.Ordinal))
        {
            await SendGroupMessageAsync(groupId, msgId, """
📝 自定义称号审核已迁移到官方群：1081992954。
请先加入该群，再在群内发送：/ch add <称号> [颜色代码]
颜色可不填，默认 478978。管理员在审核群发送“同意”后即生效。
""");
            return;
        }

        var reviewContent = $"""
【称号申请】
申请QQ: {userId}
布吉岛ID: {binding.BjdName}
UUID: {binding.BjdUuid}
称号: {title}
颜色: #{colorHex}
管理员在本群发送“同意”即可通过（回复本条可精准通过该申请）。
""";

        var reviewReferenceMsgId = msgId;
        var reviewMessageId = await SendGroupMessageWithIdAsync(CustomTitleReviewGroupId, reviewReferenceMsgId, reviewContent);
        if (string.IsNullOrWhiteSpace(reviewMessageId))
        {
            await SendGroupMessageAsync(groupId, msgId, "❌ 申请提交失败：无法发送到审核群。");
            return;
        }

        lock (_customTitleApprovalLock)
        {
            _pendingCustomTitleRequests[reviewMessageId] = new PendingCustomTitleRequest
            {
                ApplicantQq = userId,
                ApplicantBjdName = binding.BjdName,
                ApplicantBjdUuid = binding.BjdUuid,
                Title = title,
                ColorHex = colorHex,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
        }

        PersistRuntimeStateToDisk();
        await SendGroupMessageAsync(groupId, msgId, "✅ 已提交称号申请，等待管理员在本群发送“同意”通过。");
    }

    private static async Task HandleBgCommandAsync(string[] parts, string? groupId, string? msgId, string? userId)
    {
        if (string.IsNullOrWhiteSpace(groupId) || NeedMsgIdButMissing(msgId) || string.IsNullOrWhiteSpace(userId)) return;
        const int minIdFontSize = 12;
        const int maxIdFontSize = 36;

        if (parts.Length >= 2 && parts[1].Equals("cl", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(_adminQq))
            {
                await SendGroupMessageAsync(groupId, msgId, "❌ 未配置管理员QQ，无法设置纯色背景。");
                return;
            }

            if (!string.Equals(userId, _adminQq, StringComparison.Ordinal))
            {
                await SendGroupMessageAsync(groupId, msgId, "❌ 无权限：仅管理员可设置纯色背景。");
                return;
            }

            if (parts.Length < 3)
            {
                await SendGroupMessageAsync(groupId, msgId, "❌ 用法: !bg cl <颜色ID>（不带#，如 F5F7FA）");
                return;
            }

            var colorId = NormalizeHexColor(parts[2]);
            if (colorId == null)
            {
                await SendGroupMessageAsync(groupId, msgId, "❌ 颜色ID格式错误，仅支持 3位或6位十六进制（如 FFF 或 F5F7FA）");
                return;
            }

            _dataStore.SetBackgroundSolidColorHex(colorId);
            await SendGroupMessageAsync(groupId, msgId, $"✅ 默认纯色背景已设置为 #{colorId}");
            return;
        }

        if (parts.Length >= 2 && parts[1].Equals("icon", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(_adminQq))
            {
                await SendGroupMessageAsync(groupId, msgId, "❌ 未配置管理员QQ，无法设置图标大小。");
                return;
            }

            if (!string.Equals(userId, _adminQq, StringComparison.Ordinal))
            {
                await SendGroupMessageAsync(groupId, msgId, "❌ 无权限：仅管理员可设置图标大小。");
                return;
            }

            if (parts.Length < 3 || !int.TryParse(parts[2], out var iconSize))
            {
                await SendGroupMessageAsync(groupId, msgId, "❌ 用法: !bg icon <大小像素> (16-40)");
                return;
            }

            iconSize = Math.Clamp(iconSize, 16, 40);
            _dataStore.SetChipIconSize(iconSize);
            await SendGroupMessageAsync(groupId, msgId, $"✅ 物品图标大小已设置为 {iconSize}px（全局生效）");
            return;
        }

        if (parts.Length >= 2 && parts[1].Equals("id", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length < 3 || !int.TryParse(parts[2], out var idFontSize))
            {
                await SendGroupMessageAsync(groupId, msgId, $"❌ 用法: !bg id <大小像素> ({minIdFontSize}-{maxIdFontSize})");
                return;
            }

            idFontSize = Math.Clamp(idFontSize, minIdFontSize, maxIdFontSize);

            if (IsOfficialGroupMessageSource())
            {
                if (!string.Equals(groupId, CustomTitleReviewGroupId, StringComparison.Ordinal))
                {
                    await SendGroupMessageAsync(groupId, msgId, $"""
📝 ID字号审核已迁移到官方群：{CustomTitleReviewGroupId}。
请先加入该群，再在群内发送：!bg id <大小像素>
管理员在审核群发送“同意”后即生效。
""");
                    return;
                }

                var applicantName = "未绑定";
                if (_bindService.TryGetBindingByQq(userId, out var binding) && !string.IsNullOrWhiteSpace(binding.BjdName))
                {
                    applicantName = binding.BjdName;
                }

                var reviewContent = $"""
【ID字号申请】
申请QQ: {userId}
申请人: {applicantName}
目标字号: {idFontSize}px（范围 {minIdFontSize}-{maxIdFontSize}）
管理员在本群发送“同意”即可通过（回复本条可精准通过该申请）。
""";

                var reviewMessageId = await SendGroupMessageWithIdAsync(CustomTitleReviewGroupId, msgId, reviewContent);
                if (string.IsNullOrWhiteSpace(reviewMessageId))
                {
                    await SendGroupMessageAsync(groupId, msgId, "❌ 申请提交失败：无法发送到审核群。");
                    return;
                }

                lock (_customTitleApprovalLock)
                {
                    _pendingPlayerIdFontSizeRequests[reviewMessageId] = new PendingPlayerIdFontSizeRequest
                    {
                        ApplicantQq = userId,
                        ApplicantBjdName = applicantName,
                        IdFontSize = idFontSize,
                        CreatedAtUtc = DateTimeOffset.UtcNow
                    };
                }

                PersistRuntimeStateToDisk();
                await SendGroupMessageAsync(groupId, msgId, "✅ 已提交ID字号申请，等待管理员在本群发送“同意”通过。");
                return;
            }

            if (string.IsNullOrWhiteSpace(_adminQq))
            {
                await SendGroupMessageAsync(groupId, msgId, "❌ 未配置管理员QQ，无法设置ID字号。");
                return;
            }

            if (!string.Equals(userId, _adminQq, StringComparison.Ordinal))
            {
                await SendGroupMessageAsync(groupId, msgId, "❌ 无权限：仅管理员可设置ID字号。");
                return;
            }

            _dataStore.SetPlayerIdFontSize(idFontSize);
            await SendGroupMessageAsync(groupId, msgId, $"✅ ID字号已设置为 {idFontSize}px（全局生效，范围 {minIdFontSize}-{maxIdFontSize}，默认 14）");
            return;
        }

        if (parts.Length >= 2 && parts[1].Equals("set", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(_adminQq))
            {
                await SendGroupMessageAsync(groupId, msgId, "❌ 未配置管理员QQ，无法设置背景透明度。");
                return;
            }

            if (!string.Equals(userId, _adminQq, StringComparison.Ordinal))
            {
                await SendGroupMessageAsync(groupId, msgId, "❌ 无权限：仅管理员可设置背景透明度。");
                return;
            }

            if (parts.Length < 3
                || (!double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var opacityRaw)
                    && !double.TryParse(parts[2], out opacityRaw)))
            {
                await SendGroupMessageAsync(groupId, msgId, "❌ 用法: !bg set <透明度> (0-1 或 0-100)");
                return;
            }

            var opacity = opacityRaw > 1 ? opacityRaw / 100.0 : opacityRaw;
            opacity = Math.Clamp(opacity, 0, 1);
            _dataStore.SetBackgroundOpacity(opacity);
            await SendGroupMessageAsync(groupId, msgId, $"✅ 背景透明度已设置为 {opacity:0.##}");
            return;
        }

        var result = _backgroundCommand.BeginUpload(userId, groupId);
        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            await SendGroupMessageAsync(groupId, msgId, result.Message);
        }
    }

    private static async Task HandleBwCommandAsync(string[] parts, string? groupId, string? msgId, string? userId)
    {
        if (string.IsNullOrWhiteSpace(groupId) || NeedMsgIdButMissing(msgId)) return;
        if (_bwService == null)
        {
            await SendGroupMessageAsync(groupId, msgId, "❌ 图片渲染器未启动，无法生成战绩卡片。");
            return;
        }

        string? modeToken = null;
        DateOnly historyDate = default;
        var isHistoryQuery = false;
        string playerName;
        if (parts.Length == 1)
        {
            if (string.IsNullOrWhiteSpace(userId) || !_bindService.TryGetBindingByQq(userId, out var binding))
            {
                await SendGroupMessageAsync(groupId, msgId, "❌ 未检测到你的绑定信息，请先执行 !bind <布吉岛用户名>，或使用 !bw <玩家名> [模式]");
                return;
            }

            if (string.IsNullOrWhiteSpace(binding.BjdName))
            {
                await SendGroupMessageAsync(groupId, msgId, "❌ 绑定信息缺少布吉岛用户名，请重新执行 !bind <布吉岛用户名>");
                return;
            }

            playerName = binding.BjdName;
        }
        else if (parts.Length == 2)
        {
            playerName = parts[1];
        }
        else if (parts.Length == 3)
        {
            playerName = parts[1];
            if (TryParseBwHistoryDateToken(parts[2], out historyDate))
            {
                isHistoryQuery = true;
            }
            else if (!IsBwModeToken(parts[2]))
            {
                await SendGroupMessageAsync(groupId, msgId, "❌ 用法: !bw <玩家名> [模式] 或 !bw <玩家名> <x年x月x日>。模式示例: solo / 2s / 4s / xp32 / bw16");
                return;
            }
            else
            {
                modeToken = parts[2];
            }
        }
        else
        {
            await SendGroupMessageAsync(groupId, msgId, "❌ 用法: !bw（查绑定总览） 或 !bw <玩家名> [模式] 或 !bw <玩家名> <x年x月x日>");
            return;
        }

        if (isHistoryQuery)
        {
            await SendBwHistorySnapshotToGroupAsync(groupId, msgId, userId, playerName, historyDate);
            return;
        }

        var displayPlayerName = GetDisplayPlayerIdForCurrentSource(playerName);
        await SendPendingUpdateToGroupIfExistsAsync(groupId, msgId, userId);

        var perfTotal = Stopwatch.StartNew();
        var playerApiTask = RequestPlayerInfoAsync(playerName);
        var apiSw = Stopwatch.StartNew();
        var apiResult = await RequestGameStatsAsync(playerName);
        apiSw.Stop();
        if (!apiResult.Success)
        {
            await SendGroupMessageAsync(groupId, msgId, $"❌ API 连接失败: {apiResult.ErrorMessage}");
            return;
        }

        if (IsNoBedwarsData(apiResult.JsonResponse))
        {
            await SendGroupMessageAsync(groupId, msgId, "这个人未游玩游戏或者未进入服务器！");
            return;
        }

        _sessionService?.SilentCacheAsync(apiResult.JsonResponse!, playerName);

        string? bwxpShow = null;
        var playerInfo = _bindService.ExtractPlayerInfo(apiResult.JsonResponse!, playerName);
        var useFastPlayerApiWait = IsAdminUser(userId);
        var playerApiWaitSw = Stopwatch.StartNew();
        ApiCallResult? playerApiResult = useFastPlayerApiWait
            ? await TryAwaitApiCallWithTimeoutAsync(playerApiTask, BwPlayerInfoFastWaitTimeout)
            : await playerApiTask;
        playerApiWaitSw.Stop();
        if (playerApiResult.HasValue
            && playerApiResult.Value.Success
            && !string.IsNullOrWhiteSpace(playerApiResult.Value.JsonResponse))
        {
            var playerApiJson = playerApiResult.Value.JsonResponse!;
            bwxpShow = ParseBwxpShow(playerApiJson);
            if (string.IsNullOrWhiteSpace(playerInfo.Uuid))
            {
                playerInfo = _bindService.ExtractPlayerFromPlayerApi(playerApiJson, playerName);
            }
        }

        if (string.IsNullOrWhiteSpace(playerInfo.Uuid))
        {
            playerInfo = _bindService.ExtractPlayerInfo(apiResult.JsonResponse!, playerName);
        }

        SaveBwDailySnapshot(playerName, apiResult.JsonResponse!, playerInfo.Uuid, "manual");
        var avatarSrc = _infoPhotoService.TryBuildAvatarDataUri(playerInfo.Uuid);
        var customTitleBadgeHtml = BuildCustomTitleBadgeHtml(playerName, playerInfo.Uuid);

        string? backgroundSrc = null;
        var solidBackgroundColor = _dataStore.GetBackgroundSolidColorHex();
        var backgroundOpacity = _dataStore.GetBackgroundOpacity();
        var chipIconSize = _dataStore.GetChipIconSize();
        var playerIdFontSize = _dataStore.GetPlayerIdFontSize();
        if (!string.IsNullOrWhiteSpace(userId) && _bindService.TryGetBindingByQq(userId, out var requesterBinding))
        {
            var targetUuid = playerInfo.Uuid;
            if (string.IsNullOrWhiteSpace(targetUuid)
                && _dataStore.TryGetQqBindingByPlayerName(playerName, out var targetBindingByName))
            {
                targetUuid = targetBindingByName.BjdUuid;
            }

            var sameUuid = !string.IsNullOrWhiteSpace(targetUuid)
                           && string.Equals(targetUuid, requesterBinding.BjdUuid, StringComparison.OrdinalIgnoreCase);
            var sameName = string.Equals(playerName, requesterBinding.BjdName, StringComparison.OrdinalIgnoreCase);
            if (sameUuid || sameName)
            {
                backgroundSrc = _backgroundService.TryBuildBackgroundDataUriWithReason(requesterBinding.BjdUuid, out var bgReason);
                _ = bgReason;
            }
        }

        try
        {
            var renderSw = Stopwatch.StartNew();
            using var imgStream = await _bwService.GenerateStatsImageAsync(
                apiResult.JsonResponse!,
                avatarSrc,
                playerInfo.Uuid,
                displayPlayerName,
                bwxpShow,
                modeToken,
                backgroundSrc,
                solidBackgroundColor,
                backgroundOpacity,
                chipIconSize,
                playerIdFontSize,
                customTitleBadgeHtml);
            renderSw.Stop();

            var bwCaption = _dataStore.GetBwImageCaption();
            var sendSw = Stopwatch.StartNew();
            var sentMessageId = await SendGroupImageAndGetMessageIdAsync(groupId, msgId, imgStream, bwCaption);
            sendSw.Stop();
            RegisterBwQuickReplyContextForGroup(groupId, sentMessageId, playerName);
            _dataStore.RecordQueryablePlayerId(playerName);
            perfTotal.Stop();
            if (useFastPlayerApiWait)
            {
                Console.WriteLine($"[性能][BW群][管理员] {playerName} api={apiSw.ElapsedMilliseconds}ms playerWait={playerApiWaitSw.ElapsedMilliseconds}ms render={renderSw.ElapsedMilliseconds}ms send={sendSw.ElapsedMilliseconds}ms total={perfTotal.ElapsedMilliseconds}ms");
            }
        }
        catch (InvalidOperationException ex)
        {
            await SendGroupMessageAsync(groupId, msgId, $"❌ {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BW] 渲染或发送失败: {ex}");
            await SendGroupMessageAsync(groupId, msgId, "❌ 生成战绩图片失败（渲染器可能卡住或重启中），请稍后重试。");
        }
    }

    private static async Task HandleBwPrivateCommandAsync(string[] parts, string userId)
    {
        if (_bwService == null)
        {
            await SendPrivateMessageAsync(userId, "❌ 图片渲染器未启动，无法生成战绩卡片。");
            return;
        }

        string? modeToken = null;
        DateOnly historyDate = default;
        var isHistoryQuery = false;
        string playerName;
        if (parts.Length == 1)
        {
            if (!_bindService.TryGetBindingByQq(userId, out var binding) || string.IsNullOrWhiteSpace(binding.BjdName))
            {
                await SendPrivateMessageAsync(userId, "❌ 未检测到你的绑定信息，请先执行 !bind <布吉岛用户名>，或使用 !bw <玩家名> [模式]");
                return;
            }
            playerName = binding.BjdName;
        }
        else if (parts.Length == 2)
        {
            playerName = parts[1];
        }
        else if (parts.Length == 3)
        {
            playerName = parts[1];
            if (TryParseBwHistoryDateToken(parts[2], out historyDate))
            {
                isHistoryQuery = true;
            }
            else if (!IsBwModeToken(parts[2]))
            {
                await SendPrivateMessageAsync(userId, "❌ 用法: !bw <玩家名> [模式] 或 !bw <玩家名> <x年x月x日>。模式示例: solo / 2s / 4s / xp32 / bw16");
                return;
            }
            else
            {
                modeToken = parts[2];
            }
        }
        else
        {
            await SendPrivateMessageAsync(userId, "❌ 用法: !bw（查绑定总览） 或 !bw <玩家名> [模式] 或 !bw <玩家名> <x年x月x日>");
            return;
        }

        if (isHistoryQuery)
        {
            await SendBwHistorySnapshotToPrivateAsync(userId, playerName, historyDate);
            return;
        }

        await SendPendingUpdateToPrivateIfExistsAsync(userId);
        var perfTotal = Stopwatch.StartNew();
        var playerApiTask = RequestPlayerInfoAsync(playerName);
        var apiSw = Stopwatch.StartNew();
        var apiResult = await RequestGameStatsAsync(playerName);
        apiSw.Stop();
        if (!apiResult.Success)
        {
            await SendPrivateMessageAsync(userId, $"❌ API 连接失败: {apiResult.ErrorMessage}");
            return;
        }

        if (IsNoBedwarsData(apiResult.JsonResponse))
        {
            await SendPrivateMessageAsync(userId, "这个人未游玩游戏或者未进入服务器！");
            return;
        }

        string? bwxpShow = null;
        var playerInfo = _bindService.ExtractPlayerInfo(apiResult.JsonResponse!, playerName);
        var useFastPlayerApiWait = IsAdminUser(userId);
        var playerApiWaitSw = Stopwatch.StartNew();
        ApiCallResult? playerApiResult = useFastPlayerApiWait
            ? await TryAwaitApiCallWithTimeoutAsync(playerApiTask, BwPlayerInfoFastWaitTimeout)
            : await playerApiTask;
        playerApiWaitSw.Stop();
        if (playerApiResult.HasValue
            && playerApiResult.Value.Success
            && !string.IsNullOrWhiteSpace(playerApiResult.Value.JsonResponse))
        {
            var playerApiJson = playerApiResult.Value.JsonResponse!;
            bwxpShow = ParseBwxpShow(playerApiJson);
            if (string.IsNullOrWhiteSpace(playerInfo.Uuid))
            {
                playerInfo = _bindService.ExtractPlayerFromPlayerApi(playerApiJson, playerName);
            }
        }

        SaveBwDailySnapshot(playerName, apiResult.JsonResponse!, playerInfo.Uuid, "manual");
        var avatarSrc = _infoPhotoService.TryBuildAvatarDataUri(playerInfo.Uuid);
        var customTitleBadgeHtml = BuildCustomTitleBadgeHtml(playerName, playerInfo.Uuid);
        var solidBackgroundColor = _dataStore.GetBackgroundSolidColorHex();
        var backgroundOpacity = _dataStore.GetBackgroundOpacity();
        var chipIconSize = _dataStore.GetChipIconSize();
        var playerIdFontSize = _dataStore.GetPlayerIdFontSize();
        string? backgroundSrc = null;
        if (_bindService.TryGetBindingByQq(userId, out var requesterBinding))
        {
            var sameName = string.Equals(playerName, requesterBinding.BjdName, StringComparison.OrdinalIgnoreCase);
            var sameUuid = !string.IsNullOrWhiteSpace(playerInfo.Uuid)
                           && string.Equals(playerInfo.Uuid, requesterBinding.BjdUuid, StringComparison.OrdinalIgnoreCase);
            if (sameName || sameUuid)
            {
                backgroundSrc = _backgroundService.TryBuildBackgroundDataUriWithReason(requesterBinding.BjdUuid, out _);
            }
        }

        try
        {
            var renderSw = Stopwatch.StartNew();
            using var imgStream = await _bwService.GenerateStatsImageAsync(
                apiResult.JsonResponse!,
                avatarSrc,
                playerInfo.Uuid,
                playerName,
                bwxpShow,
                modeToken,
                backgroundSrc,
                solidBackgroundColor,
                backgroundOpacity,
                chipIconSize,
                playerIdFontSize,
                customTitleBadgeHtml);
            renderSw.Stop();

            var sendSw = Stopwatch.StartNew();
            await SendPrivateImageAsync(userId, imgStream, _dataStore.GetBwImageCaption());
            sendSw.Stop();
            _dataStore.RecordQueryablePlayerId(playerName);
            perfTotal.Stop();
            if (useFastPlayerApiWait)
            {
                Console.WriteLine($"[性能][BW私聊][管理员] {playerName} api={apiSw.ElapsedMilliseconds}ms playerWait={playerApiWaitSw.ElapsedMilliseconds}ms render={renderSw.ElapsedMilliseconds}ms send={sendSw.ElapsedMilliseconds}ms total={perfTotal.ElapsedMilliseconds}ms");
            }
        }
        catch (InvalidOperationException ex)
        {
            await SendPrivateMessageAsync(userId, $"❌ {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BW-私聊] 渲染或发送失败: {ex}");
            await SendPrivateMessageAsync(userId, "❌ 生成战绩图片失败（渲染器可能卡住或重启中），请稍后重试。");
        }
    }

    private static async Task HandleSwCommandAsync(string[] parts, string? groupId, string? msgId, string? userId)
    {
        if (string.IsNullOrWhiteSpace(groupId) || NeedMsgIdButMissing(msgId)) return;
        if (!await EnsureSwServiceReadyAsync(groupId, msgId))
        {
            return;
        }

        string playerName;
        if (parts.Length == 1)
        {
            if (string.IsNullOrWhiteSpace(userId) || !_bindService.TryGetBindingByQq(userId, out var binding))
            {
                await SendGroupMessageAsync(groupId, msgId, "❌ 未检测到你的绑定信息，请先执行 !bind <布吉岛用户名>，或使用 !sw <玩家名>");
                return;
            }

            if (string.IsNullOrWhiteSpace(binding.BjdName))
            {
                await SendGroupMessageAsync(groupId, msgId, "❌ 绑定信息缺少布吉岛用户名，请重新执行 !bind <布吉岛用户名>");
                return;
            }

            playerName = binding.BjdName;
        }
        else if (parts.Length == 2)
        {
            playerName = parts[1];
        }
        else
        {
            await SendGroupMessageAsync(groupId, msgId, "❌ 用法: !sw（查绑定） 或 !sw <玩家名>");
            return;
        }

        var displayPlayerName = GetDisplayPlayerIdForCurrentSource(playerName);
        await SendPendingUpdateToGroupIfExistsAsync(groupId, msgId, userId);

        var playerApiTask = RequestPlayerInfoAsync(playerName);
        var apiResult = await RequestSkywarsStatsAsync(playerName);
        if (!apiResult.Success)
        {
            await SendGroupMessageAsync(groupId, msgId, $"❌ SW API 连接失败: {apiResult.ErrorMessage}");
            return;
        }

        if (IsNoSkywarsData(apiResult.JsonResponse))
        {
            await SendGroupMessageAsync(groupId, msgId, "这个人未游玩 Skywars 或未进入服务器！");
            return;
        }

        string? swxpShow = null;
        var playerInfo = _bindService.ExtractPlayerInfo(apiResult.JsonResponse!, playerName);
        var playerApiResult = await playerApiTask;
        if (playerApiResult.Success && !string.IsNullOrWhiteSpace(playerApiResult.JsonResponse))
        {
            swxpShow = ParseSwxpShow(playerApiResult.JsonResponse);
            if (string.IsNullOrWhiteSpace(playerInfo.Uuid))
            {
                playerInfo = _bindService.ExtractPlayerFromPlayerApi(playerApiResult.JsonResponse, playerName);
            }
        }

        if (string.IsNullOrWhiteSpace(playerInfo.Uuid))
        {
            playerInfo = playerInfo with { Uuid = TryParseSkywarsUuid(apiResult.JsonResponse!) ?? string.Empty };
        }

        var chipIconSize = _dataStore.GetChipIconSize();
        var customTitleBadgeHtml = BuildCustomTitleBadgeHtml(playerName, playerInfo.Uuid);
        try
        {
            var avatarSrc = _infoPhotoService.TryBuildAvatarDataUri(playerInfo.Uuid);
            using var imgStream = await _swService.GenerateStatsImageAsync(
                apiResult.JsonResponse!,
                avatarSrc,
                playerInfo.Uuid,
                displayPlayerName,
                chipIconSize,
                swxpShow,
                customTitleBadgeHtml);

            await SendGroupImageAsync(groupId, msgId, imgStream);
            _dataStore.RecordQueryablePlayerId(playerName);
        }
        catch (InvalidOperationException ex)
        {
            await SendGroupMessageAsync(groupId, msgId, $"❌ {ex.Message}");
        }
        catch (FileNotFoundException ex)
        {
            Console.WriteLine($"[SW] 模板缺失: {ex}");
            await SendGroupMessageAsync(groupId, msgId, $"❌ SW 模板文件缺失：{ex.FileName ?? ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SW] 渲染或发送失败: {ex}");
            await SendGroupMessageAsync(groupId, msgId, "❌ 生成 SW 图片失败，请稍后重试。");
        }
    }

    private static async Task HandleSwPrivateCommandAsync(string[] parts, string userId)
    {
        if (!await EnsureSwServiceReadyForPrivateAsync(userId))
        {
            return;
        }

        string playerName;
        if (parts.Length == 1)
        {
            if (!_bindService.TryGetBindingByQq(userId, out var binding) || string.IsNullOrWhiteSpace(binding.BjdName))
            {
                await SendPrivateMessageAsync(userId, "❌ 未检测到你的绑定信息，请先执行 !bind <布吉岛用户名>，或使用 !sw <玩家名>");
                return;
            }

            playerName = binding.BjdName;
        }
        else if (parts.Length == 2)
        {
            playerName = parts[1];
        }
        else
        {
            await SendPrivateMessageAsync(userId, "❌ 用法: !sw（查绑定） 或 !sw <玩家名>");
            return;
        }

        await SendPendingUpdateToPrivateIfExistsAsync(userId);

        var playerApiTask = RequestPlayerInfoAsync(playerName);
        var apiResult = await RequestSkywarsStatsAsync(playerName);
        if (!apiResult.Success)
        {
            await SendPrivateMessageAsync(userId, $"❌ SW API 连接失败: {apiResult.ErrorMessage}");
            return;
        }

        if (IsNoSkywarsData(apiResult.JsonResponse))
        {
            await SendPrivateMessageAsync(userId, "这个人未游玩 Skywars 或未进入服务器！");
            return;
        }

        string? swxpShow = null;
        var playerInfo = _bindService.ExtractPlayerInfo(apiResult.JsonResponse!, playerName);
        var playerApiResult = await playerApiTask;
        if (playerApiResult.Success && !string.IsNullOrWhiteSpace(playerApiResult.JsonResponse))
        {
            swxpShow = ParseSwxpShow(playerApiResult.JsonResponse);
            if (string.IsNullOrWhiteSpace(playerInfo.Uuid))
            {
                playerInfo = _bindService.ExtractPlayerFromPlayerApi(playerApiResult.JsonResponse, playerName);
            }
        }

        if (string.IsNullOrWhiteSpace(playerInfo.Uuid))
        {
            playerInfo = playerInfo with { Uuid = TryParseSkywarsUuid(apiResult.JsonResponse!) ?? string.Empty };
        }

        var chipIconSize = _dataStore.GetChipIconSize();
        var customTitleBadgeHtml = BuildCustomTitleBadgeHtml(playerName, playerInfo.Uuid);
        try
        {
            var avatarSrc = _infoPhotoService.TryBuildAvatarDataUri(playerInfo.Uuid);
            using var imgStream = await _swService.GenerateStatsImageAsync(
                apiResult.JsonResponse!,
                avatarSrc,
                playerInfo.Uuid,
                playerName,
                chipIconSize,
                swxpShow,
                customTitleBadgeHtml);

            await SendPrivateImageAsync(userId, imgStream);
            _dataStore.RecordQueryablePlayerId(playerName);
        }
        catch (InvalidOperationException ex)
        {
            await SendPrivateMessageAsync(userId, $"❌ {ex.Message}");
        }
        catch (FileNotFoundException ex)
        {
            Console.WriteLine($"[SW-私聊] 模板缺失: {ex}");
            await SendPrivateMessageAsync(userId, $"❌ SW 模板文件缺失：{ex.FileName ?? ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SW-私聊] 渲染或发送失败: {ex}");
            await SendPrivateMessageAsync(userId, "❌ 生成 SW 图片失败，请稍后重试。");
        }
    }

    private static async Task HandleLbPrivateCommandAsync(string[] parts, string userId)
    {
        if (!await EnsureLbServiceReadyForPrivateAsync(userId))
        {
            return;
        }

        string playerName;
        if (parts.Length >= 2)
        {
            playerName = parts[1];
        }
        else
        {
            if (!_bindService.TryGetBindingByQq(userId, out var binding) || string.IsNullOrWhiteSpace(binding.BjdName))
            {
                await SendPrivateMessageAsync(userId, "❌ 未检测到你的绑定信息，请先执行 !bind <布吉岛用户名>，或使用 !lb <玩家名>");
                return;
            }

            playerName = binding.BjdName;
        }

        await SendPendingUpdateToPrivateIfExistsAsync(userId);
        var apiResult = await RequestLeaderboardAsync(playerName);
        if (!apiResult.Success)
        {
            await SendPrivateMessageAsync(userId, $"❌ 排行榜 API 连接失败: {apiResult.ErrorMessage}");
            return;
        }

        SaveLeaderboardDailySnapshot(playerName, apiResult.JsonResponse!, null, "manual");
        _dataStore.RecordQueryablePlayerId(playerName);

        var lbApiCode = ParseApiCode(apiResult.JsonResponse);
        if (lbApiCode == 404)
        {
            var lbMsg = ParseApiMessage(apiResult.JsonResponse) ?? "未上榜.";
            await SendPrivateMessageAsync(userId, $"ℹ️ {playerName} {lbMsg}");
            return;
        }

        var playerInfo = _bindService.ExtractPlayerInfo(apiResult.JsonResponse!, playerName);
        if (string.IsNullOrWhiteSpace(playerInfo.Uuid))
        {
            var playerApiResult = await RequestPlayerInfoAsync(playerName);
            if (playerApiResult.Success && !string.IsNullOrWhiteSpace(playerApiResult.JsonResponse))
            {
                playerInfo = _bindService.ExtractPlayerFromPlayerApi(playerApiResult.JsonResponse, playerName);
            }
        }

        try
        {
            var avatarSrc = _infoPhotoService.TryBuildAvatarDataUri(playerInfo.Uuid);
            using var imgStream = await _lbService.GenerateLeaderboardImageAsync(
                apiResult.JsonResponse!,
                playerName,
                avatarSrc,
                playerInfo.Uuid);
            await SendPrivateImageAsync(userId, imgStream);
            SaveLeaderboardDailySnapshot(playerName, apiResult.JsonResponse!, playerInfo.Uuid, "manual");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LB-私聊] 渲染或发送失败: {ex}");
            await SendPrivateMessageAsync(userId, "❌ 生成排行榜图片失败，请稍后重试。");
        }
    }

    private static async Task HandleSessionPrivateCommandAsync(string[] parts, string userId)
    {
        if (!await EnsureSessionServiceReadyForPrivateAsync(userId))
        {
            return;
        }

        var days = 1;
        string playerName;
        if (parts.Length >= 2 && parts[1].Equals("bw", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length == 2)
            {
                if (!_bindService.TryGetBindingByQq(userId, out var binding))
                {
                    await SendPrivateMessageAsync(userId, "❌ 未检测到你的绑定信息，请先执行 !bind <布吉岛用户名>，或使用 !sess bw <玩家名> [t天数]");
                    return;
                }
                playerName = binding.BjdName;
            }
            else
            {
                if (TryParseSessionDaysToken(parts[^1], out var parsedDays))
                {
                    days = parsedDays;
                    playerName = parts.Length == 3
                        ? (_bindService.TryGetBindingByQq(userId, out var binding) ? binding.BjdName : string.Empty)
                        : parts[2];
                }
                else
                {
                    playerName = parts[2];
                }
            }
        }
        else
        {
            await SendPrivateMessageAsync(userId, "❌ 用法: !sess bw [玩家名] [t天数]");
            return;
        }

        if (string.IsNullOrWhiteSpace(playerName))
        {
            await SendPrivateMessageAsync(userId, "❌ 玩家名不能为空。");
            return;
        }

        await SendPendingUpdateToPrivateIfExistsAsync(userId);
        var apiResult = await RequestGameStatsAsync(playerName);
        if (!apiResult.Success)
        {
            await SendPrivateMessageAsync(userId, $"❌ API 连接失败: {apiResult.ErrorMessage}");
            return;
        }

        _sessionService?.SilentCacheAsync(apiResult.JsonResponse!, playerName);
        var (img, reminder) = await _sessionService!.GenerateSessionImageAsync(apiResult.JsonResponse!, playerName, days);
        using (img)
        {
            await SendPrivateImageAsync(userId, img);
        }

        if (!string.IsNullOrWhiteSpace(reminder))
        {
            await SendPrivateMessageAsync(userId, reminder);
        }
    }

    private static async Task HandleHelpPrivateCommandAsync(string userId)
    {
        if (!await EnsureHelpServiceReadyForPrivateAsync(userId))
        {
            return;
        }

        using var stream = await _helpService.GenerateHelpImageAsync();
        await SendPrivateImageAsync(userId, stream);
    }

    private static async Task HandleSessionCommandAsync(string[] parts, string? groupId, string? msgId, string? userId)
    {
        if (string.IsNullOrWhiteSpace(groupId) || NeedMsgIdButMissing(msgId)) return;
        if (!await EnsureSessionServiceReadyAsync(groupId, msgId))
        {
            return;
        }

        var days = 1;
        string playerName;

        if (parts.Length < 2 || !string.Equals(parts[1], "bw", StringComparison.OrdinalIgnoreCase))
        {
            await SendGroupMessageAsync(groupId, msgId, "❌ 用法: !sess bw [玩家名] [t天数]。后续会支持多模式，必须带 bw。");
            return;
        }

        if (parts.Length == 2)
        {
            if (string.IsNullOrWhiteSpace(userId) || !_bindService.TryGetBindingByQq(userId, out var binding))
            {
                await SendGroupMessageAsync(groupId, msgId, "❌ 未检测到你的绑定信息，请先执行 !bind <布吉岛用户名>，或使用 !sess bw <玩家名> [t天数]");
                return;
            }

            playerName = binding.BjdName;
        }
        else if (parts.Length == 3)
        {
            var token = parts[2];
            if (TryParseSessionDaysToken(token, out var parsedDays))
            {
                days = parsedDays;
                if (string.IsNullOrWhiteSpace(userId) || !_bindService.TryGetBindingByQq(userId, out var binding))
                {
                    await SendGroupMessageAsync(groupId, msgId, "❌ 未检测到你的绑定信息，请先执行 !bind <布吉岛用户名>，或使用 !sess bw <玩家名> [t天数]");
                    return;
                }

                playerName = binding.BjdName;
            }
            else
            {
                playerName = token;
            }
        }
        else if (parts.Length == 4)
        {
            playerName = parts[2];
            if (!TryParseSessionDaysToken(parts[3], out days))
            {
                await SendGroupMessageAsync(groupId, msgId, "❌ 用法: !sess bw [玩家名] [t天数]，例如 t1 / t3。");
                return;
            }
        }
        else
        {
            await SendGroupMessageAsync(groupId, msgId, "❌ 用法: !sess bw [玩家名] [t天数]");
            return;
        }

        if (string.IsNullOrWhiteSpace(playerName))
        {
            await SendGroupMessageAsync(groupId, msgId, "❌ 玩家名不能为空。");
            return;
        }

        days = Math.Clamp(days, 1, 400);
        await SendPendingUpdateToGroupIfExistsAsync(groupId, msgId, userId);

        var displayPlayerName = GetDisplayPlayerIdForCurrentSource(playerName);
        var apiResult = await RequestGameStatsAsync(playerName);
        if (!apiResult.Success)
        {
            await SendGroupMessageAsync(groupId, msgId, $"❌ API 连接失败: {apiResult.ErrorMessage}");
            return;
        }

        var (img, reminder) = await _sessionService.GenerateSessionImageAsync(
            apiResult.JsonResponse!,
            playerName,
            days,
            displayPlayerName);
        var shouldSendIntro = !string.IsNullOrWhiteSpace(userId)
                              && _userTracker != null
                              && _userTracker.CheckAndMarkFirstTime(userId);

        if (shouldSendIntro)
        {
            using var intro = await _sessionService.GenerateIntroImageAsync();
            await SendGroupImageAsync(groupId, msgId, intro);
        }

        using (img)
        {
            await SendGroupImageAsync(groupId, msgId, img);
        }

        if (!string.IsNullOrWhiteSpace(reminder))
        {
            await SendGroupMessageAsync(groupId, msgId, reminder);
        }
    }

    private static bool TryParseSessionDaysToken(string token, out int days)
    {
        days = 0;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var text = token.Trim();
        if (!(text.StartsWith("t", StringComparison.OrdinalIgnoreCase) || text.StartsWith("T", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return int.TryParse(text[1..], out days) && days > 0;
    }

    private static async Task<bool> TryHandleCustomTitleApprovalReplyAsync(
        string groupId,
        string msgId,
        string userId,
        string rawContent,
        string? replyMessageId)
    {
        if (string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        var plain = NormalizePlainText(rawContent);
        if (!string.Equals(plain, "同意", StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(groupId, CustomTitleReviewGroupId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(_adminQq)
            || !string.Equals(userId, _adminQq, StringComparison.Ordinal))
        {
            return false;
        }

        PendingCustomTitleRequest? customTitleRequest;
        PendingSkinAddRequest? skinAddRequest;
        PendingPlayerIdFontSizeRequest? playerIdFontSizeRequest;
        lock (_customTitleApprovalLock)
        {
            customTitleRequest = null;
            skinAddRequest = null;
            playerIdFontSizeRequest = null;

            if (!string.IsNullOrWhiteSpace(replyMessageId)
                && _pendingCustomTitleRequests.TryGetValue(replyMessageId, out var repliedCustomTitleRequest))
            {
                customTitleRequest = repliedCustomTitleRequest;
                _pendingCustomTitleRequests.Remove(replyMessageId);
            }
            else if (!string.IsNullOrWhiteSpace(replyMessageId)
                     && _pendingSkinAddRequests.TryGetValue(replyMessageId, out var repliedSkinAddRequest))
            {
                skinAddRequest = repliedSkinAddRequest;
                _pendingSkinAddRequests.Remove(replyMessageId);
            }
            else if (!string.IsNullOrWhiteSpace(replyMessageId)
                     && _pendingPlayerIdFontSizeRequests.TryGetValue(replyMessageId, out var repliedPlayerIdFontSizeRequest))
            {
                playerIdFontSizeRequest = repliedPlayerIdFontSizeRequest;
                _pendingPlayerIdFontSizeRequests.Remove(replyMessageId);
            }

            if (customTitleRequest == null && skinAddRequest == null && playerIdFontSizeRequest == null)
            {
                var oldestType = 0;
                var oldestKey = string.Empty;
                var oldestCreatedAtUtc = DateTimeOffset.MaxValue;

                if (_pendingCustomTitleRequests.Count > 0)
                {
                    var oldest = _pendingCustomTitleRequests
                        .OrderBy(x => x.Value.CreatedAtUtc)
                        .First();
                    oldestType = 1;
                    oldestKey = oldest.Key;
                    oldestCreatedAtUtc = oldest.Value.CreatedAtUtc;
                }

                if (_pendingSkinAddRequests.Count > 0)
                {
                    var oldest = _pendingSkinAddRequests
                        .OrderBy(x => x.Value.CreatedAtUtc)
                        .First();
                    if (oldest.Value.CreatedAtUtc < oldestCreatedAtUtc)
                    {
                        oldestType = 2;
                        oldestKey = oldest.Key;
                        oldestCreatedAtUtc = oldest.Value.CreatedAtUtc;
                    }
                }

                if (_pendingPlayerIdFontSizeRequests.Count > 0)
                {
                    var oldest = _pendingPlayerIdFontSizeRequests
                        .OrderBy(x => x.Value.CreatedAtUtc)
                        .First();
                    if (oldest.Value.CreatedAtUtc < oldestCreatedAtUtc)
                    {
                        oldestType = 3;
                        oldestKey = oldest.Key;
                    }
                }

                switch (oldestType)
                {
                    case 1:
                        customTitleRequest = _pendingCustomTitleRequests[oldestKey];
                        _pendingCustomTitleRequests.Remove(oldestKey);
                        break;
                    case 2:
                        skinAddRequest = _pendingSkinAddRequests[oldestKey];
                        _pendingSkinAddRequests.Remove(oldestKey);
                        break;
                    case 3:
                        playerIdFontSizeRequest = _pendingPlayerIdFontSizeRequests[oldestKey];
                        _pendingPlayerIdFontSizeRequests.Remove(oldestKey);
                        break;
                }
            }
        }

        if (customTitleRequest == null && skinAddRequest == null && playerIdFontSizeRequest == null)
        {
            await SendGroupMessageAsync(groupId, msgId, "❌ 当前没有待审核申请。");
            return true;
        }

        if (customTitleRequest != null)
        {
            _dataStore.UpsertCustomTitle(customTitleRequest.ApplicantBjdUuid, customTitleRequest.Title, customTitleRequest.ColorHex);
            await SendGroupMessageAsync(groupId, msgId,
                $"✅ 已通过：{customTitleRequest.ApplicantBjdName} 的称号申请 -> {customTitleRequest.Title} (#{customTitleRequest.ColorHex})");

            if (_napcatBot != null)
            {
                await SendPrivateMessageAsync(customTitleRequest.ApplicantQq,
                    $"✅ 你的称号申请已通过：{customTitleRequest.Title} (#{customTitleRequest.ColorHex})");
            }

            PersistRuntimeStateToDisk();
            return true;
        }

        if (skinAddRequest != null)
        {
            var skinResult = await _infoPhotoService.AddSkinAsync(skinAddRequest.ApplicantQq, skinAddRequest.OfficialId, _bindService);
            var maskedOfficialId = MaskPlayerId(skinAddRequest.OfficialId);
            if (skinResult.Success)
            {
                await SendGroupMessageAsync(groupId, msgId,
                    $"✅ 已通过：{skinAddRequest.ApplicantBjdName} 的皮肤申请 -> {maskedOfficialId}");

                if (_napcatBot != null)
                {
                    await SendPrivateMessageAsync(skinAddRequest.ApplicantQq,
                        $"✅ 你的皮肤申请已通过：{skinAddRequest.OfficialId}");
                }
            }
            else
            {
                var groupDetail = skinResult.Message.Replace(
                    skinAddRequest.OfficialId,
                    maskedOfficialId,
                    StringComparison.OrdinalIgnoreCase);
                await SendGroupMessageAsync(groupId, msgId, $"❌ 皮肤申请处理失败：{groupDetail}");

                if (_napcatBot != null)
                {
                    await SendPrivateMessageAsync(skinAddRequest.ApplicantQq,
                        $"❌ 你的皮肤申请未通过：{skinResult.Message}");
                }
            }

            PersistRuntimeStateToDisk();
            return true;
        }

        if (playerIdFontSizeRequest != null)
        {
            _dataStore.SetPlayerIdFontSize(playerIdFontSizeRequest.IdFontSize);
            await SendGroupMessageAsync(groupId, msgId,
                $"✅ 已通过：{playerIdFontSizeRequest.ApplicantBjdName} 的ID字号申请 -> {playerIdFontSizeRequest.IdFontSize}px");

            if (_napcatBot != null)
            {
                await SendPrivateMessageAsync(playerIdFontSizeRequest.ApplicantQq,
                    $"✅ 你的ID字号申请已通过：{playerIdFontSizeRequest.IdFontSize}px");
            }

            PersistRuntimeStateToDisk();
            return true;
        }

        return true;
    }

    private static async Task<bool> TryHandleBwQuickReplyAsync(
        string groupId,
        string msgId,
        string? userId,
        string rawContent,
        string? replyMessageId,
        bool allowGroupLatestFallback = false)
    {
        if (string.IsNullOrWhiteSpace(groupId)
            || string.IsNullOrWhiteSpace(msgId))
        {
            return false;
        }

        if (!TryParseBwQuickReplyAction(rawContent, out var action))
        {
            return false;
        }

        string playerName;
        if (!string.IsNullOrWhiteSpace(replyMessageId))
        {
            if (!TryGetBwQuickReplyPlayer(groupId, replyMessageId, out playerName))
            {
                return false;
            }
        }
        else
        {
            if (!allowGroupLatestFallback)
            {
                return false;
            }

            if (!TryGetLatestBwQuickReplyPlayerForGroup(groupId, out playerName))
            {
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(playerName))
        {
            return false;
        }

        switch (action)
        {
            case 1:
                await HandleBwCommandAsync(new[] { "bw", playerName, "solo" }, groupId, msgId, userId);
                break;
            case 2:
                await HandleBwCommandAsync(new[] { "bw", playerName, "2s" }, groupId, msgId, userId);
                break;
            case 3:
                await HandleBwCommandAsync(new[] { "bw", playerName, "4s" }, groupId, msgId, userId);
                break;
            case 4:
                await HandleBwCommandAsync(new[] { "bw", playerName, "xp32" }, groupId, msgId, userId);
                break;
            case 5:
                await HandleBwCommandAsync(new[] { "bw", playerName, "xp64" }, groupId, msgId, userId);
                break;
            case 6:
                await HandleLbCommandAsync(new[] { "lb", playerName }, groupId, msgId, userId);
                break;
            default:
                return false;
        }

        if (_currentMessageSource.Value is MessageSource.NapcatGroup or MessageSource.NapcatPrivate)
        {
            _dataStore.IncrementNapcatUsage();
        }

        return true;
    }

    private static bool TryParseBwQuickReplyAction(string rawContent, out int action)
    {
        action = 0;
        var plain = NormalizePlainText(rawContent)
            .Replace("１", "1", StringComparison.Ordinal)
            .Replace("２", "2", StringComparison.Ordinal)
            .Replace("３", "3", StringComparison.Ordinal)
            .Replace("４", "4", StringComparison.Ordinal)
            .Replace("５", "5", StringComparison.Ordinal)
            .Replace("６", "6", StringComparison.Ordinal)
            .Trim();

        if (plain.Length != 1 || !char.IsDigit(plain[0]))
        {
            return false;
        }

        action = plain[0] - '0';
        return action is >= 1 and <= 6;
    }

    private static bool TryGetBwQuickReplyPlayer(string groupId, string replyMessageId, out string playerName)
    {
        playerName = string.Empty;
        var hasChanges = TrimBwQuickReplyContextsIfNeeded();

        var key = BuildBwQuickReplyKey(groupId, replyMessageId);
        if (!_bwQuickReplyContexts.TryGetValue(key, out var ctx))
        {
            if (hasChanges)
            {
                PersistBwQuickReplyContextsToDisk();
            }
            return false;
        }

        if (DateTimeOffset.UtcNow - ctx.CreatedAtUtc > BwQuickReplyContextTtl)
        {
            if (_bwQuickReplyContexts.TryRemove(key, out _))
            {
                hasChanges = true;
            }
            if (hasChanges)
            {
                PersistBwQuickReplyContextsToDisk();
            }
            return false;
        }

        if (hasChanges)
        {
            PersistBwQuickReplyContextsToDisk();
        }
        playerName = ctx.PlayerName;
        return !string.IsNullOrWhiteSpace(playerName);
    }

    private static bool TryGetLatestBwQuickReplyPlayerForGroup(string groupId, out string playerName)
    {
        playerName = string.Empty;
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return false;
        }

        var hasChanges = TrimBwQuickReplyContextsIfNeeded();
        var prefix = $"{groupId}:";
        var now = DateTimeOffset.UtcNow;
        BwQuickReplyContext? best = null;

        foreach (var kv in _bwQuickReplyContexts)
        {
            if (!kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (now - kv.Value.CreatedAtUtc > BwQuickReplyContextTtl)
            {
                if (_bwQuickReplyContexts.TryRemove(kv.Key, out _))
                {
                    hasChanges = true;
                }
                continue;
            }

            if (best == null || kv.Value.CreatedAtUtc > best.CreatedAtUtc)
            {
                best = kv.Value;
            }
        }

        if (best == null || string.IsNullOrWhiteSpace(best.PlayerName))
        {
            if (hasChanges)
            {
                PersistBwQuickReplyContextsToDisk();
            }
            return false;
        }

        if (hasChanges)
        {
            PersistBwQuickReplyContextsToDisk();
        }
        playerName = best.PlayerName;
        return true;
    }

    private static void RegisterBwQuickReplyContextForGroup(string groupId, string? messageId, string playerName)
    {
        if (string.IsNullOrWhiteSpace(groupId)
            || string.IsNullOrWhiteSpace(messageId)
            || string.IsNullOrWhiteSpace(playerName))
        {
            return;
        }

        var key = BuildBwQuickReplyKey(groupId, messageId);
        _bwQuickReplyContexts[key] = new BwQuickReplyContext(playerName.Trim(), DateTimeOffset.UtcNow);
        TrimBwQuickReplyContextsIfNeeded();
        PersistBwQuickReplyContextsToDisk();
    }

    private static string BuildBwQuickReplyKey(string groupId, string messageId)
    {
        return $"{groupId}:{messageId}";
    }

    private static bool TrimBwQuickReplyContextsIfNeeded()
    {
        if (_bwQuickReplyContexts.IsEmpty)
        {
            return false;
        }

        var hasChanges = false;
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in _bwQuickReplyContexts)
        {
            if (now - kv.Value.CreatedAtUtc > BwQuickReplyContextTtl)
            {
                if (_bwQuickReplyContexts.TryRemove(kv.Key, out _))
                {
                    hasChanges = true;
                }
            }
        }

        var count = _bwQuickReplyContexts.Count;
        if (count <= BwQuickReplyContextMaxEntries)
        {
            return hasChanges;
        }

        var removeCount = count - BwQuickReplyContextMaxEntries;
        foreach (var key in _bwQuickReplyContexts
                     .OrderBy(x => x.Value.CreatedAtUtc)
                     .Take(removeCount)
                     .Select(x => x.Key)
                     .ToList())
        {
            if (_bwQuickReplyContexts.TryRemove(key, out _))
            {
                hasChanges = true;
            }
        }

        return hasChanges;
    }

    private static void LoadBwQuickReplyContextsFromDisk()
    {
        var path = _bwQuickReplyContextPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            List<PersistedBwQuickReplyContext>? persisted;
            lock (_bwQuickReplyContextFileLock)
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                persisted = JsonConvert.DeserializeObject<List<PersistedBwQuickReplyContext>>(json);
            }

            if (persisted == null || persisted.Count == 0)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var restored = 0;
            foreach (var item in persisted)
            {
                if (item == null
                    || string.IsNullOrWhiteSpace(item.Key)
                    || string.IsNullOrWhiteSpace(item.PlayerName))
                {
                    continue;
                }

                if (now - item.CreatedAtUtc > BwQuickReplyContextTtl)
                {
                    continue;
                }

                _bwQuickReplyContexts[item.Key.Trim()] =
                    new BwQuickReplyContext(item.PlayerName.Trim(), item.CreatedAtUtc);
                restored++;
            }

            var trimmed = TrimBwQuickReplyContextsIfNeeded();
            if (trimmed || restored != persisted.Count)
            {
                PersistBwQuickReplyContextsToDisk();
            }

            if (restored > 0)
            {
                Console.WriteLine($"[快捷回复] 已恢复 {restored} 条战绩卡回复上下文。");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[快捷回复] 回复上下文恢复失败: {ex.Message}");
        }
    }

    private static void PersistBwQuickReplyContextsToDisk()
    {
        var path = _bwQuickReplyContextPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var snapshot = _bwQuickReplyContexts
                .Select(x => new PersistedBwQuickReplyContext
                {
                    Key = x.Key,
                    PlayerName = x.Value.PlayerName,
                    CreatedAtUtc = x.Value.CreatedAtUtc
                })
                .OrderByDescending(x => x.CreatedAtUtc)
                .Take(BwQuickReplyContextMaxEntries)
                .ToList();

            var json = JsonConvert.SerializeObject(snapshot, Formatting.None);
            lock (_bwQuickReplyContextFileLock)
            {
                File.WriteAllText(path, json, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[快捷回复] 回复上下文保存失败: {ex.Message}");
        }
    }

    private static void LoadRuntimeStateFromDisk()
    {
        var path = _runtimeStatePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            RuntimeStateSnapshot? state;
            lock (_runtimeStateFileLock)
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                state = JsonConvert.DeserializeObject<RuntimeStateSnapshot>(json);
            }

            if (state == null)
            {
                return;
            }

            var needsRewrite = false;
            var now = DateTimeOffset.UtcNow;
            var restoredApprovalCount = 0;
            var restoredIdiomSessionCount = 0;

            lock (_customTitleApprovalLock)
            {
                _pendingCustomTitleRequests.Clear();
                _pendingSkinAddRequests.Clear();
                _pendingPlayerIdFontSizeRequests.Clear();

                foreach (var item in state.PendingCustomTitleRequests ?? new List<PersistedPendingCustomTitleRequest>())
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.ReviewMessageId) || string.IsNullOrWhiteSpace(item.ApplicantQq))
                    {
                        needsRewrite = true;
                        continue;
                    }

                    _pendingCustomTitleRequests[item.ReviewMessageId.Trim()] = new PendingCustomTitleRequest
                    {
                        ApplicantQq = item.ApplicantQq.Trim(),
                        ApplicantBjdName = item.ApplicantBjdName?.Trim() ?? string.Empty,
                        ApplicantBjdUuid = item.ApplicantBjdUuid?.Trim() ?? string.Empty,
                        Title = item.Title?.Trim() ?? string.Empty,
                        ColorHex = string.IsNullOrWhiteSpace(item.ColorHex) ? "FFFFFF" : item.ColorHex.Trim(),
                        CreatedAtUtc = item.CreatedAtUtc
                    };
                    restoredApprovalCount++;
                }

                foreach (var item in state.PendingSkinAddRequests ?? new List<PersistedPendingSkinAddRequest>())
                {
                    if (item == null
                        || string.IsNullOrWhiteSpace(item.ReviewMessageId)
                        || string.IsNullOrWhiteSpace(item.ApplicantQq)
                        || string.IsNullOrWhiteSpace(item.OfficialId))
                    {
                        needsRewrite = true;
                        continue;
                    }

                    _pendingSkinAddRequests[item.ReviewMessageId.Trim()] = new PendingSkinAddRequest
                    {
                        ApplicantQq = item.ApplicantQq.Trim(),
                        ApplicantBjdName = item.ApplicantBjdName?.Trim() ?? string.Empty,
                        ApplicantBjdUuid = item.ApplicantBjdUuid?.Trim() ?? string.Empty,
                        OfficialId = item.OfficialId.Trim(),
                        CreatedAtUtc = item.CreatedAtUtc
                    };
                    restoredApprovalCount++;
                }

                foreach (var item in state.PendingPlayerIdFontSizeRequests ?? new List<PersistedPendingPlayerIdFontSizeRequest>())
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.ReviewMessageId) || string.IsNullOrWhiteSpace(item.ApplicantQq))
                    {
                        needsRewrite = true;
                        continue;
                    }

                    _pendingPlayerIdFontSizeRequests[item.ReviewMessageId.Trim()] = new PendingPlayerIdFontSizeRequest
                    {
                        ApplicantQq = item.ApplicantQq.Trim(),
                        ApplicantBjdName = item.ApplicantBjdName?.Trim() ?? string.Empty,
                        IdFontSize = item.IdFontSize,
                        CreatedAtUtc = item.CreatedAtUtc
                    };
                    restoredApprovalCount++;
                }
            }

            lock (_pendingUpdateLock)
            {
                _pendingUpdateText = (state.PendingUpdateText ?? string.Empty).Trim();
                _pendingUpdateDeliveredUsers.Clear();
                foreach (var userId in state.PendingUpdateDeliveredUsers ?? new List<string>())
                {
                    if (string.IsNullOrWhiteSpace(userId))
                    {
                        needsRewrite = true;
                        continue;
                    }

                    _pendingUpdateDeliveredUsers.Add(userId.Trim());
                }
            }

            _idiomChainSessions.Clear();
            foreach (var item in state.IdiomChainSessions ?? new List<PersistedIdiomChainSession>())
            {
                if (item == null || string.IsNullOrWhiteSpace(item.GroupId))
                {
                    needsRewrite = true;
                    continue;
                }

                if (now - item.LastUpdatedUtc > IdiomChainSessionTimeout)
                {
                    needsRewrite = true;
                    continue;
                }

                var session = new IdiomChainSession
                {
                    LastIdiom = item.LastIdiom?.Trim() ?? string.Empty,
                    ExpectedStartChar = item.ExpectedStartChar?.Trim() ?? string.Empty,
                    LastUpdatedUtc = item.LastUpdatedUtc
                };

                foreach (var idiom in item.UsedIdioms ?? new List<string>())
                {
                    if (string.IsNullOrWhiteSpace(idiom))
                    {
                        needsRewrite = true;
                        continue;
                    }

                    if (session.UsedIdioms.Count >= IdiomChainMaxUsedCount)
                    {
                        needsRewrite = true;
                        break;
                    }

                    session.UsedIdioms.Add(idiom.Trim());
                }

                _idiomChainSessions[item.GroupId.Trim()] = session;
                restoredIdiomSessionCount++;
            }

            if (restoredApprovalCount > 0 || restoredIdiomSessionCount > 0 || !string.IsNullOrWhiteSpace(_pendingUpdateText))
            {
                Console.WriteLine($"[状态恢复] 审核队列={restoredApprovalCount}，接龙会话={restoredIdiomSessionCount}");
            }

            if (needsRewrite)
            {
                PersistRuntimeStateToDisk();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[状态恢复] 运行状态恢复失败: {ex.Message}");
        }
    }

    private static void PersistRuntimeStateToDisk()
    {
        var path = _runtimeStatePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var snapshot = new RuntimeStateSnapshot();

            lock (_customTitleApprovalLock)
            {
                snapshot.PendingCustomTitleRequests = _pendingCustomTitleRequests
                    .Select(x => new PersistedPendingCustomTitleRequest
                    {
                        ReviewMessageId = x.Key,
                        ApplicantQq = x.Value.ApplicantQq,
                        ApplicantBjdName = x.Value.ApplicantBjdName,
                        ApplicantBjdUuid = x.Value.ApplicantBjdUuid,
                        Title = x.Value.Title,
                        ColorHex = x.Value.ColorHex,
                        CreatedAtUtc = x.Value.CreatedAtUtc
                    })
                    .OrderBy(x => x.CreatedAtUtc)
                    .ToList();

                snapshot.PendingSkinAddRequests = _pendingSkinAddRequests
                    .Select(x => new PersistedPendingSkinAddRequest
                    {
                        ReviewMessageId = x.Key,
                        ApplicantQq = x.Value.ApplicantQq,
                        ApplicantBjdName = x.Value.ApplicantBjdName,
                        ApplicantBjdUuid = x.Value.ApplicantBjdUuid,
                        OfficialId = x.Value.OfficialId,
                        CreatedAtUtc = x.Value.CreatedAtUtc
                    })
                    .OrderBy(x => x.CreatedAtUtc)
                    .ToList();

                snapshot.PendingPlayerIdFontSizeRequests = _pendingPlayerIdFontSizeRequests
                    .Select(x => new PersistedPendingPlayerIdFontSizeRequest
                    {
                        ReviewMessageId = x.Key,
                        ApplicantQq = x.Value.ApplicantQq,
                        ApplicantBjdName = x.Value.ApplicantBjdName,
                        IdFontSize = x.Value.IdFontSize,
                        CreatedAtUtc = x.Value.CreatedAtUtc
                    })
                    .OrderBy(x => x.CreatedAtUtc)
                    .ToList();
            }

            lock (_pendingUpdateLock)
            {
                snapshot.PendingUpdateText = (_pendingUpdateText ?? string.Empty).Trim();
                snapshot.PendingUpdateDeliveredUsers = _pendingUpdateDeliveredUsers
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToList();
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var kv in _idiomChainSessions)
            {
                if (string.IsNullOrWhiteSpace(kv.Key))
                {
                    continue;
                }

                var session = kv.Value;
                if (session == null || now - session.LastUpdatedUtc > IdiomChainSessionTimeout)
                {
                    continue;
                }

                List<string> usedIdioms;
                try
                {
                    usedIdioms = session.UsedIdioms
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => x.Trim())
                        .Distinct(StringComparer.Ordinal)
                        .Take(IdiomChainMaxUsedCount)
                        .ToList();
                }
                catch
                {
                    continue;
                }

                snapshot.IdiomChainSessions.Add(new PersistedIdiomChainSession
                {
                    GroupId = kv.Key,
                    UsedIdioms = usedIdioms,
                    LastIdiom = session.LastIdiom,
                    ExpectedStartChar = session.ExpectedStartChar,
                    LastUpdatedUtc = session.LastUpdatedUtc
                });
            }

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonConvert.SerializeObject(snapshot, Formatting.None);
            lock (_runtimeStateFileLock)
            {
                File.WriteAllText(path, json, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[状态保存] 运行状态保存失败: {ex.Message}");
        }
    }

    private static string? TryExtractNapcatReplyMessageId(JObject raw)
    {
        if (raw["message"] is JArray segments)
        {
            foreach (var segment in segments)
            {
                var type = segment?["type"]?.ToString();
                if (!string.Equals(type, "reply", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var replyId = segment?["data"]?["id"]?.ToString();
                if (!string.IsNullOrWhiteSpace(replyId))
                {
                    return replyId;
                }
            }
        }

        var rawMessage = raw["raw_message"]?.ToString();
        if (string.IsNullOrWhiteSpace(rawMessage))
        {
            return null;
        }

        var match = CqReplyRegex.Match(rawMessage);
        return match.Success ? match.Groups["id"].Value : null;
    }

    private static bool IsNapcatAtQuickReplyBot(JObject raw)
    {
        if (raw["message"] is JArray segments)
        {
            foreach (var segment in segments)
            {
                var type = segment?["type"]?.ToString();
                if (!string.Equals(type, "at", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var atQq = segment?["data"]?["qq"]?.ToString()
                           ?? segment?["data"]?["user_id"]?.ToString();
                if (string.Equals(atQq, NapcatQuickReplyAtBotQq, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        var rawMessage = raw["raw_message"]?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawMessage))
        {
            return false;
        }

        return rawMessage.Contains($"[CQ:at,qq={NapcatQuickReplyAtBotQq}]", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePlainText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var noCq = CqCodeRegex.Replace(raw, string.Empty);
        return noCq.Trim();
    }

    private static string ExtractCommandTail(string raw, string[] parts)
    {
        if (string.IsNullOrWhiteSpace(raw) || parts.Length == 0)
        {
            return string.Empty;
        }

        var commandLength = parts[0].Length;
        if (raw.Length <= commandLength)
        {
            return string.Empty;
        }

        return raw[commandLength..].Trim();
    }

    private static void SetPendingUpdateText(string text)
    {
        var value = (text ?? string.Empty).Trim();
        lock (_pendingUpdateLock)
        {
            _pendingUpdateText = value;
            _pendingUpdateDeliveredUsers.Clear();
        }

        PersistRuntimeStateToDisk();
    }

    private static string? TryConsumePendingUpdateTextForUser(string? userId)
    {
        var changed = false;
        string? updateText = null;
        lock (_pendingUpdateLock)
        {
            if (string.IsNullOrWhiteSpace(_pendingUpdateText))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(userId))
            {
                return null;
            }

            if (_pendingUpdateDeliveredUsers.Contains(userId))
            {
                return null;
            }

            _pendingUpdateDeliveredUsers.Add(userId);
            changed = true;
            updateText = _pendingUpdateText.Trim();
        }

        if (changed)
        {
            PersistRuntimeStateToDisk();
        }

        return updateText;
    }

    private static async Task SendPendingUpdateToGroupIfExistsAsync(string groupId, string? msgId, string? userId)
    {
        var updateText = TryConsumePendingUpdateTextForUser(userId);
        if (string.IsNullOrWhiteSpace(updateText))
        {
            return;
        }

        await SendGroupMessageAsync(groupId, msgId ?? string.Empty, $"更新内容：{updateText}");
    }

    private static async Task SendPendingUpdateToPrivateIfExistsAsync(string userId)
    {
        var updateText = TryConsumePendingUpdateTextForUser(userId);
        if (string.IsNullOrWhiteSpace(updateText))
        {
            return;
        }

        await SendPrivateMessageAsync(userId, $"更新内容：{updateText}");
    }

    private static string? BuildCustomTitleBadgeHtml(string playerName, string? playerUuid)
    {
        var targetUuid = playerUuid;
        if (string.IsNullOrWhiteSpace(targetUuid) && _dataStore.TryGetQqBindingByPlayerName(playerName, out var targetBinding))
        {
            targetUuid = targetBinding.BjdUuid;
        }

        if (!_dataStore.TryGetCustomTitleByBjdUuid(targetUuid, out var titleBinding))
        {
            return null;
        }

        var colorHex = NormalizeHexColor(titleBinding.ColorHex) ?? "FFFFFF";
        var safeTitle = WebUtility.HtmlEncode(titleBinding.Title);
        return $"<span class=\"custom-title-badge\" style=\"color: #{colorHex};background: transparent !important;border: none !important;box-shadow: none !important;padding: 0 !important;border-radius: 0 !important;font-size: 26px !important;line-height: 1 !important;font-weight: 900 !important;\">[{safeTitle}]</span>";
    }

    private static string? NormalizeHexColor(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var text = input.Trim();
        if (text.StartsWith("#", StringComparison.Ordinal))
        {
            text = text[1..];
        }

        if (text.Length is not (3 or 6))
        {
            return null;
        }

        foreach (var ch in text)
        {
            var isHex = (ch >= '0' && ch <= '9')
                        || (ch >= 'a' && ch <= 'f')
                        || (ch >= 'A' && ch <= 'F');
            if (!isHex)
            {
                return null;
            }
        }

        return text.ToUpperInvariant();
    }

    private static bool IsNoBedwarsData(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        try
        {
            var root = JObject.Parse(json);
            var data = root["data"] as JObject;
            if (data == null)
            {
                return true;
            }

            var totalGame = data.Value<int?>("total_game") ?? 0;
            var totalWin = data.Value<int?>("total_win") ?? 0;
            var totalFk = data.Value<int?>("total_fk") ?? 0;
            var totalBedDestroy = data.Value<int?>("total_bed_destroy") ?? 0;
            if (totalGame > 0 || totalWin > 0 || totalFk > 0 || totalBedDestroy > 0)
            {
                return false;
            }

            var bedwars = data["bedwars"] as JObject;
            if (bedwars == null || !bedwars.Properties().Any())
            {
                return true;
            }

            foreach (var mode in bedwars.Properties())
            {
                if (mode.Value is not JObject modeObj)
                {
                    continue;
                }

                if ((modeObj.Value<int?>("game") ?? 0) > 0) return false;
                if ((modeObj.Value<int?>("win") ?? 0) > 0) return false;
                if ((modeObj.Value<int?>("lose") ?? 0) > 0) return false;
                if ((modeObj.Value<int?>("final_kills") ?? 0) > 0) return false;
                if ((modeObj.Value<int?>("final_deaths") ?? 0) > 0) return false;
                if ((modeObj.Value<int?>("bed_destory") ?? 0) > 0) return false;
                if ((modeObj.Value<int?>("bed_lose") ?? 0) > 0) return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsNoSkywarsData(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        try
        {
            var root = JObject.Parse(json);
            var data = root["data"] as JObject;
            if (data == null)
            {
                return true;
            }

            var totalGame = data.Value<int?>("total_game") ?? 0;
            var totalKills = data.Value<int?>("total_kills") ?? 0;
            var totalWin = data.Value<int?>("total_win") ?? 0;
            if (totalGame > 0 || totalKills > 0 || totalWin > 0)
            {
                return false;
            }

            var skywars = data["skywars"] as JObject;
            if (skywars == null || !skywars.Properties().Any())
            {
                return true;
            }

            foreach (var mode in skywars.Properties())
            {
                if (string.Equals(mode.Name, "effect", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (mode.Value is not JObject modeObj)
                {
                    continue;
                }

                if ((modeObj.Value<int?>("game") ?? 0) > 0) return false;
                if ((modeObj.Value<int?>("win") ?? 0) > 0) return false;
                if ((modeObj.Value<int?>("lose") ?? 0) > 0) return false;
                if ((modeObj.Value<int?>("kills") ?? 0) > 0) return false;
                if ((modeObj.Value<int?>("deaths") ?? 0) > 0) return false;
                if ((modeObj.Value<int?>("projectileKills") ?? 0) > 0) return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryParseSkywarsUuid(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var obj = JObject.Parse(json);
            return obj.SelectToken("data.uuid")?.ToString()
                   ?? obj.SelectToken("data._id")?.ToString()
                   ?? obj.SelectToken("uuid")?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsBwModeToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var text = token.Trim();
        if (BwModeAliases.Contains(text))
        {
            return true;
        }

        if (text.StartsWith("bw", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (text.StartsWith("xp", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (int.TryParse(text, out _))
        {
            return true;
        }

        return false;
    }

    private static bool IsBwHistoryQueryParts(string[] parts)
    {
        return parts.Length == 3 && TryParseBwHistoryDateToken(parts[2], out _);
    }

    private static bool TryParseBwHistoryDateToken(string token, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var text = token.Trim();
        var formats = new[]
        {
            "yyyy年M月d日",
            "yyyy-M-d",
            "yyyy/M/d",
            "yyyy.M.d",
            "yyyyMMdd"
        };

        foreach (var format in formats)
        {
            if (DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                date = DateOnly.FromDateTime(parsed);
                return true;
            }
        }

        return false;
    }

    private static string ResolveBwSnapshotDisplayName(string jsonResponse, string fallbackName)
    {
        if (string.IsNullOrWhiteSpace(jsonResponse))
        {
            return fallbackName;
        }

        try
        {
            var obj = JObject.Parse(jsonResponse);
            var name = obj.SelectToken("data.name")?.ToString();
            return string.IsNullOrWhiteSpace(name) ? fallbackName : name.Trim();
        }
        catch
        {
            return fallbackName;
        }
    }

    private static void SaveBwDailySnapshot(string queriedPlayerName, string jsonResponse, string? playerUuid, string source)
    {
        if (_bwHistoryStore == null || string.IsNullOrWhiteSpace(queriedPlayerName) || string.IsNullOrWhiteSpace(jsonResponse))
        {
            return;
        }

        try
        {
            var now = DateTimeOffset.Now;
            var displayName = ResolveBwSnapshotDisplayName(jsonResponse, queriedPlayerName);
            _bwHistoryStore.UpsertSnapshot(
                queriedPlayerName,
                displayName,
                jsonResponse,
                now,
                source,
                playerUuid);

            if (!string.Equals(displayName, queriedPlayerName, StringComparison.OrdinalIgnoreCase))
            {
                _bwHistoryStore.UpsertSnapshot(
                    displayName,
                    displayName,
                    jsonResponse,
                    now,
                    source,
                    playerUuid);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BW历史] 保存快照失败: player={queriedPlayerName}, error={ex.Message}");
        }
    }

    private static string ResolveLeaderboardSnapshotDisplayName(string jsonResponse, string fallbackName)
    {
        if (string.IsNullOrWhiteSpace(jsonResponse))
        {
            return fallbackName;
        }

        try
        {
            var obj = JObject.Parse(jsonResponse);
            var name = obj.SelectToken("data.name")?.ToString()
                       ?? obj.SelectToken("name")?.ToString();
            return string.IsNullOrWhiteSpace(name) ? fallbackName : name.Trim();
        }
        catch
        {
            return fallbackName;
        }
    }

    private static void SaveLeaderboardDailySnapshot(string queriedPlayerName, string jsonResponse, string? playerUuid, string source)
    {
        if (_leaderboardSnapshotStore == null || string.IsNullOrWhiteSpace(queriedPlayerName) || string.IsNullOrWhiteSpace(jsonResponse))
        {
            return;
        }

        try
        {
            var now = DateTimeOffset.Now;
            var displayName = ResolveLeaderboardSnapshotDisplayName(jsonResponse, queriedPlayerName);
            _leaderboardSnapshotStore.UpsertSnapshot(
                queriedPlayerName,
                displayName,
                jsonResponse,
                now,
                source,
                playerUuid);

            if (!string.Equals(displayName, queriedPlayerName, StringComparison.OrdinalIgnoreCase))
            {
                _leaderboardSnapshotStore.UpsertSnapshot(
                    displayName,
                    displayName,
                    jsonResponse,
                    now,
                    source,
                    playerUuid);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[排行榜历史] 保存快照失败: player={queriedPlayerName}, error={ex.Message}");
        }
    }

    private static async Task SendBwHistorySnapshotToGroupAsync(
        string groupId,
        string msgId,
        string? userId,
        string playerName,
        DateOnly date)
    {
        if (_bwService == null || _bwHistoryStore == null)
        {
            await SendGroupMessageAsync(groupId, msgId, "❌ 历史查询服务未初始化。");
            return;
        }

        if (!_bwHistoryStore.TryGetSnapshot(playerName, date, out var snapshot))
        {
            await SendGroupMessageAsync(groupId, msgId, "未有此记录，可能未扫描。");
            return;
        }

        try
        {
            var targetName = string.IsNullOrWhiteSpace(snapshot.DisplayName) ? playerName : snapshot.DisplayName;
            var displayTargetName = GetDisplayPlayerIdForCurrentSource(targetName);
            var playerUuid = snapshot.PlayerUuid;
            if (string.IsNullOrWhiteSpace(playerUuid))
            {
                var info = _bindService.ExtractPlayerInfo(snapshot.JsonResponse, targetName);
                playerUuid = info.Uuid;
            }

            var avatarSrc = _infoPhotoService.TryBuildAvatarDataUri(playerUuid);
            var customTitleBadgeHtml = BuildCustomTitleBadgeHtml(targetName, playerUuid);
            var solidBackgroundColor = _dataStore.GetBackgroundSolidColorHex();
            var backgroundOpacity = _dataStore.GetBackgroundOpacity();
            var chipIconSize = _dataStore.GetChipIconSize();
            var playerIdFontSize = _dataStore.GetPlayerIdFontSize();

            string? backgroundSrc = null;
            if (!string.IsNullOrWhiteSpace(userId) && _bindService.TryGetBindingByQq(userId, out var requesterBinding))
            {
                var sameUuid = !string.IsNullOrWhiteSpace(playerUuid)
                               && string.Equals(playerUuid, requesterBinding.BjdUuid, StringComparison.OrdinalIgnoreCase);
                var sameName = string.Equals(targetName, requesterBinding.BjdName, StringComparison.OrdinalIgnoreCase);
                if (sameUuid || sameName)
                {
                    backgroundSrc = _backgroundService.TryBuildBackgroundDataUriWithReason(requesterBinding.BjdUuid, out _);
                }
            }

            using var imgStream = await _bwService.GenerateStatsImageAsync(
                snapshot.JsonResponse,
                avatarSrc,
                playerUuid,
                displayTargetName,
                null,
                null,
                backgroundSrc,
                solidBackgroundColor,
                backgroundOpacity,
                chipIconSize,
                playerIdFontSize,
                customTitleBadgeHtml);

            await SendGroupMessageAsync(groupId, msgId, $"此数据查于{snapshot.CapturedAtLocal:yyyy年M月d日 HH:mm:ss}");
            await SendGroupImageAsync(groupId, msgId, imgStream, _dataStore.GetBwImageCaption());
            _dataStore.RecordQueryablePlayerId(targetName);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BW历史] 发送失败: {ex}");
            await SendGroupMessageAsync(groupId, msgId, "❌ 历史记录渲染失败，请稍后重试。");
        }
    }

    private static async Task SendBwHistorySnapshotToPrivateAsync(string userId, string playerName, DateOnly date)
    {
        if (_bwService == null || _bwHistoryStore == null)
        {
            await SendPrivateMessageAsync(userId, "❌ 历史查询服务未初始化。");
            return;
        }

        if (!_bwHistoryStore.TryGetSnapshot(playerName, date, out var snapshot))
        {
            await SendPrivateMessageAsync(userId, "未有此记录，可能未扫描。");
            return;
        }

        try
        {
            var targetName = string.IsNullOrWhiteSpace(snapshot.DisplayName) ? playerName : snapshot.DisplayName;
            var playerUuid = snapshot.PlayerUuid;
            if (string.IsNullOrWhiteSpace(playerUuid))
            {
                var info = _bindService.ExtractPlayerInfo(snapshot.JsonResponse, targetName);
                playerUuid = info.Uuid;
            }

            var avatarSrc = _infoPhotoService.TryBuildAvatarDataUri(playerUuid);
            var customTitleBadgeHtml = BuildCustomTitleBadgeHtml(targetName, playerUuid);
            var solidBackgroundColor = _dataStore.GetBackgroundSolidColorHex();
            var backgroundOpacity = _dataStore.GetBackgroundOpacity();
            var chipIconSize = _dataStore.GetChipIconSize();
            var playerIdFontSize = _dataStore.GetPlayerIdFontSize();

            string? backgroundSrc = null;
            if (_bindService.TryGetBindingByQq(userId, out var requesterBinding))
            {
                var sameName = string.Equals(targetName, requesterBinding.BjdName, StringComparison.OrdinalIgnoreCase);
                var sameUuid = !string.IsNullOrWhiteSpace(playerUuid)
                               && string.Equals(playerUuid, requesterBinding.BjdUuid, StringComparison.OrdinalIgnoreCase);
                if (sameName || sameUuid)
                {
                    backgroundSrc = _backgroundService.TryBuildBackgroundDataUriWithReason(requesterBinding.BjdUuid, out _);
                }
            }

            using var imgStream = await _bwService.GenerateStatsImageAsync(
                snapshot.JsonResponse,
                avatarSrc,
                playerUuid,
                targetName,
                null,
                null,
                backgroundSrc,
                solidBackgroundColor,
                backgroundOpacity,
                chipIconSize,
                playerIdFontSize,
                customTitleBadgeHtml);

            await SendPrivateMessageAsync(userId, $"此数据查于{snapshot.CapturedAtLocal:yyyy年M月d日 HH:mm:ss}");
            await SendPrivateImageAsync(userId, imgStream, _dataStore.GetBwImageCaption());
            _dataStore.RecordQueryablePlayerId(targetName);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BW历史-私聊] 发送失败: {ex}");
            await SendPrivateMessageAsync(userId, "❌ 历史记录渲染失败，请稍后重试。");
        }
    }

    private static async Task RunBwDailySnapshotSchedulerAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_bwHistoryStore != null)
                {
                    var now = DateTime.Now;
                    if (now.Hour is >= 2 and < 5)
                    {
                        var today = DateOnly.FromDateTime(now);
                        var dateText = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        if (!string.Equals(_lastBwSnapshotScanDate, dateText, StringComparison.Ordinal))
                        {
                            await RunBwDailySnapshotScanOnceAsync(today, token);
                            _lastBwSnapshotScanDate = dateText;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BW历史] 定时扫描异常: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private static async Task RunBwDailySnapshotScanOnceAsync(DateOnly scanDate, CancellationToken token)
    {
        if (_bwHistoryStore == null)
        {
            return;
        }

        var boundPlayers = _dataStore.GetBoundPlayerIds();
        var queryablePlayers = _dataStore.GetQueryablePlayerIds();
        var players = boundPlayers
            .Concat(queryablePlayers)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Console.WriteLine($"[BW历史] 扫描名单：绑定 {boundPlayers.Count}，可查 {queryablePlayers.Count}，合并 {players.Count}。");
        if (players.Count == 0)
        {
            Console.WriteLine("[BW历史] 定时扫描跳过：暂无可扫描玩家。");
            return;
        }

        for (var i = players.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (players[i], players[j]) = (players[j], players[i]);
        }

        var success = 0;
        var skipped = 0;
        var failed = 0;
        Console.WriteLine($"[BW历史] 开始凌晨扫描：{scanDate:yyyy-MM-dd}，目标 {players.Count} 人。");

        foreach (var player in players)
        {
            if (token.IsCancellationRequested)
            {
                break;
            }

            var now = DateTime.Now;
            if (now.Hour >= 5)
            {
                Console.WriteLine("[BW历史] 到达 05:00，停止本轮扫描。");
                break;
            }

            if (_bwHistoryStore.HasSnapshotForDate(player, scanDate))
            {
                skipped++;
                continue;
            }

            ApiCallResult result = default;
            var ok = false;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                result = await RequestGameStatsByGameTypeAsync(player, _gameType, useCache: false);
                if (result.Success && !IsNoBedwarsData(result.JsonResponse))
                {
                    ok = true;
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), token);
            }

            if (!ok || string.IsNullOrWhiteSpace(result.JsonResponse))
            {
                failed++;
                Console.WriteLine($"[BW历史] 扫描失败: {player}");
                continue;
            }

            var playerInfo = _bindService.ExtractPlayerInfo(result.JsonResponse, player);
            SaveBwDailySnapshot(player, result.JsonResponse, playerInfo.Uuid, "scan");
            success++;

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(350, 1200)), token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }

        Console.WriteLine($"[BW历史] 扫描结束：成功 {success}，已存在 {skipped}，失败 {failed}。");
    }

    private static async Task RunLeaderboardNightSnapshotSchedulerAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_leaderboardSnapshotStore != null)
                {
                    var now = DateTime.Now;
                    var windowStart = now.Date + LeaderboardNightScanStartTime;
                    var windowStop = now.Date + LeaderboardNightScanStopTime;
                    if (now >= windowStart && now < windowStop)
                    {
                        var today = DateOnly.FromDateTime(now);
                        var dateText = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        if (!string.Equals(_lastLeaderboardSnapshotScanDate, dateText, StringComparison.Ordinal))
                        {
                            await RunLeaderboardNightSnapshotScanOnceAsync(today, token);
                            _lastLeaderboardSnapshotScanDate = dateText;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[排行榜历史] 定时扫描异常: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private static async Task RunLeaderboardNightSnapshotScanOnceAsync(DateOnly scanDate, CancellationToken token)
    {
        if (_leaderboardSnapshotStore == null)
        {
            return;
        }

        var scanWindowStart = scanDate.ToDateTime(TimeOnly.MinValue).Add(LeaderboardNightScanStartTime);
        var scanWindowStop = scanDate.ToDateTime(TimeOnly.MinValue).Add(LeaderboardNightScanStopTime);
        var nowAtStart = DateTime.Now;
        if (nowAtStart < scanWindowStart || nowAtStart >= scanWindowStop)
        {
            Console.WriteLine($"[排行榜历史] 当前不在扫描时间段（{LeaderboardNightScanStartTime:hh\\:mm}-{LeaderboardNightScanStopTime:hh\\:mm}），跳过本轮。");
            return;
        }

        var players = _dataStore.GetQueryablePlayerIds();
        if (players.Count == 0)
        {
            Console.WriteLine("[排行榜历史] 定时扫描跳过：暂无可扫描玩家。");
            return;
        }

        for (var i = players.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (players[i], players[j]) = (players[j], players[i]);
        }

        var success = 0;
        var skipped = 0;
        var failed = 0;
        var notRanked = 0;
        Console.WriteLine($"[排行榜历史] 开始夜间扫描：{scanDate:yyyy-MM-dd}，时间 {LeaderboardNightScanStartTime:hh\\:mm}-{LeaderboardNightScanStopTime:hh\\:mm}，目标 {players.Count} 人。");

        foreach (var player in players)
        {
            if (token.IsCancellationRequested)
            {
                break;
            }

            var now = DateTime.Now;
            if (now >= scanWindowStop)
            {
                Console.WriteLine($"[排行榜历史] 到达 {LeaderboardNightScanStopTime:hh\\:mm}，停止本轮扫描。");
                break;
            }

            if (_leaderboardSnapshotStore.HasSnapshotForDate(player, scanDate))
            {
                skipped++;
                continue;
            }

            ApiCallResult result = default;
            var hasRecord = false;
            var notRankedCurrent = false;
            var stopRequestedByDeadline = false;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var nowForRequest = DateTime.Now;
                var remaining = scanWindowStop - nowForRequest;
                if (remaining <= TimeSpan.Zero)
                {
                    stopRequestedByDeadline = true;
                    break;
                }

                using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                requestCts.CancelAfter(remaining);
                result = await RequestLeaderboardAsync(player, useCache: false, cancellationToken: requestCts.Token);

                if (requestCts.IsCancellationRequested && !token.IsCancellationRequested)
                {
                    stopRequestedByDeadline = true;
                    break;
                }

                if (result.Success)
                {
                    var code = ParseApiCode(result.JsonResponse);
                    if (code == 404)
                    {
                        notRankedCurrent = true;
                        break;
                    }

                    hasRecord = !string.IsNullOrWhiteSpace(result.JsonResponse);
                    if (hasRecord)
                    {
                        break;
                    }
                }

                var nowForRetry = DateTime.Now;
                var retryRemaining = scanWindowStop - nowForRetry;
                if (retryRemaining <= TimeSpan.Zero)
                {
                    stopRequestedByDeadline = true;
                    break;
                }

                var retryDelay = TimeSpan.FromMilliseconds(300 * attempt);
                if (retryDelay > retryRemaining)
                {
                    retryDelay = retryRemaining;
                }

                await Task.Delay(retryDelay, token);
            }

            if (stopRequestedByDeadline)
            {
                Console.WriteLine($"[排行榜历史] 到达 {LeaderboardNightScanStopTime:hh\\:mm}，停止本轮扫描。");
                break;
            }

            if (notRankedCurrent)
            {
                notRanked++;
                continue;
            }

            if (!hasRecord || string.IsNullOrWhiteSpace(result.JsonResponse))
            {
                failed++;
                Console.WriteLine($"[排行榜历史] 扫描失败: {player}");
                continue;
            }

            var playerInfo = _bindService.ExtractPlayerInfo(result.JsonResponse, player);
            SaveLeaderboardDailySnapshot(player, result.JsonResponse, playerInfo.Uuid, "scan");
            success++;
            Console.WriteLine($"[排行榜历史] 已记录: {player}");

            try
            {
                var nowForGap = DateTime.Now;
                var gapRemaining = scanWindowStop - nowForGap;
                if (gapRemaining <= TimeSpan.Zero)
                {
                    Console.WriteLine($"[排行榜历史] 到达 {LeaderboardNightScanStopTime:hh\\:mm}，停止本轮扫描。");
                    break;
                }

                var randomDelay = TimeSpan.FromMilliseconds(Random.Shared.Next(350, 1200));
                if (randomDelay > gapRemaining)
                {
                    randomDelay = gapRemaining;
                }

                await Task.Delay(randomDelay, token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }

        Console.WriteLine($"[排行榜历史] 扫描结束：成功 {success}，已存在 {skipped}，未上榜 {notRanked}，失败 {failed}。");
    }

    private static bool TryMapBjdBwShortcut(
        string normalizedMsg,
        string? userId,
        out string raw,
        out string[] parts,
        out bool shouldSilentlyIgnore)
    {
        raw = string.Empty;
        parts = Array.Empty<string>();
        shouldSilentlyIgnore = false;

        if (string.IsNullOrWhiteSpace(normalizedMsg))
        {
            return false;
        }

        var tokens = normalizedMsg.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length != 3 || !string.Equals(tokens[0], "bjd", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(tokens[1], "name", StringComparison.OrdinalIgnoreCase)
            && string.Equals(tokens[2], "bw", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(userId)
                && _bindService.TryGetBindingByQq(userId, out var binding)
                && !string.IsNullOrWhiteSpace(binding.BjdName))
            {
                raw = "bw";
                parts = new[] { "bw" };
                return true;
            }

            shouldSilentlyIgnore = true;
            return false;
        }

        if (string.Equals(tokens[1], "bw", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(tokens[2]))
        {
            raw = $"bw {tokens[2]}";
            parts = new[] { "bw", tokens[2] };
            return true;
        }

        return false;
    }

    private static async Task<bool> EnsureSessionServiceReadyAsync(string groupId, string msgId)
    {
        if (_sessionService == null)
        {
            await SendGroupMessageAsync(groupId, msgId, "❌ Session 服务未初始化。");
            return false;
        }

        if (_sessionInitialized)
        {
            TouchRendererUsage(ref _lastSessionRendererUseTicksUtc);
            return true;
        }

        await _sessionInitLock.WaitAsync();
        try
        {
            if (_sessionInitialized)
            {
                TouchRendererUsage(ref _lastSessionRendererUseTicksUtc);
                return true;
            }

            await SendGroupMessageAsync(groupId, msgId, "⏳ 首次使用 Session，正在初始化渲染器，请稍候...");
            await _sessionService.InitializeAsync();
            _sessionInitialized = true;
            TouchRendererUsage(ref _lastSessionRendererUseTicksUtc);
            return true;
        }
        catch (Exception ex)
        {
            await SendGroupMessageAsync(groupId, msgId, $"❌ Session 初始化失败: {ex.Message}");
            return false;
        }
        finally
        {
            _sessionInitLock.Release();
        }
    }

    private static async Task<bool> EnsureSessionServiceReadyForPrivateAsync(string userId)
    {
        if (_sessionService == null)
        {
            await SendPrivateMessageAsync(userId, "❌ Session 服务未初始化。");
            return false;
        }

        if (_sessionInitialized)
        {
            TouchRendererUsage(ref _lastSessionRendererUseTicksUtc);
            return true;
        }

        await _sessionInitLock.WaitAsync();
        try
        {
            if (_sessionInitialized)
            {
                TouchRendererUsage(ref _lastSessionRendererUseTicksUtc);
                return true;
            }

            await SendPrivateMessageAsync(userId, "⏳ 首次使用 Session，正在初始化渲染器，请稍候...");
            await _sessionService.InitializeAsync();
            _sessionInitialized = true;
            TouchRendererUsage(ref _lastSessionRendererUseTicksUtc);
            return true;
        }
        catch (Exception ex)
        {
            await SendPrivateMessageAsync(userId, $"❌ Session 初始化失败: {ex.Message}");
            return false;
        }
        finally
        {
            _sessionInitLock.Release();
        }
    }

    private static async Task<bool> EnsureSwServiceReadyAsync(string groupId, string msgId)
    {
        if (_swService == null)
        {
            _swService = new SkaywarsService();
            _swInitialized = false;
        }

        if (_swInitialized)
        {
            TouchRendererUsage(ref _lastSwRendererUseTicksUtc);
            return true;
        }

        await _swInitLock.WaitAsync();
        try
        {
            if (_swInitialized)
            {
                TouchRendererUsage(ref _lastSwRendererUseTicksUtc);
                return true;
            }

            await SendGroupMessageAsync(groupId, msgId, "⏳ 首次使用 SW，正在初始化渲染器，请稍候...");
            await _swService.InitializeAsync();
            _swInitialized = true;
            TouchRendererUsage(ref _lastSwRendererUseTicksUtc);
            return true;
        }
        catch (Exception ex)
        {
            await SendGroupMessageAsync(groupId, msgId, $"❌ SW 初始化失败: {ex.Message}");
            return false;
        }
        finally
        {
            _swInitLock.Release();
        }
    }

    private static async Task<bool> EnsureSwServiceReadyForPrivateAsync(string userId)
    {
        if (_swService == null)
        {
            _swService = new SkaywarsService();
            _swInitialized = false;
        }

        if (_swInitialized)
        {
            TouchRendererUsage(ref _lastSwRendererUseTicksUtc);
            return true;
        }

        await _swInitLock.WaitAsync();
        try
        {
            if (_swInitialized)
            {
                TouchRendererUsage(ref _lastSwRendererUseTicksUtc);
                return true;
            }

            await SendPrivateMessageAsync(userId, "⏳ 首次使用 SW，正在初始化渲染器，请稍候...");
            await _swService.InitializeAsync();
            _swInitialized = true;
            TouchRendererUsage(ref _lastSwRendererUseTicksUtc);
            return true;
        }
        catch (Exception ex)
        {
            await SendPrivateMessageAsync(userId, $"❌ SW 初始化失败: {ex.Message}");
            return false;
        }
        finally
        {
            _swInitLock.Release();
        }
    }

    private static async Task<bool> EnsureLbServiceReadyAsync(string groupId, string msgId)
    {
        if (_lbService == null)
        {
            _lbService = new LeaderboardRankings();
            _lbInitialized = false;
        }

        if (_lbInitialized)
        {
            TouchRendererUsage(ref _lastLbRendererUseTicksUtc);
            return true;
        }

        await _lbInitLock.WaitAsync();
        try
        {
            if (_lbInitialized)
            {
                TouchRendererUsage(ref _lastLbRendererUseTicksUtc);
                return true;
            }

            await SendGroupMessageAsync(groupId, msgId, "⏳ 首次使用排行榜，正在初始化渲染器，请稍候...");
            await _lbService.InitializeAsync();
            _lbInitialized = true;
            TouchRendererUsage(ref _lastLbRendererUseTicksUtc);
            return true;
        }
        catch (Exception ex)
        {
            await SendGroupMessageAsync(groupId, msgId, $"❌ 排行榜渲染器初始化失败: {ex.Message}");
            return false;
        }
        finally
        {
            _lbInitLock.Release();
        }
    }

    private static async Task<bool> EnsureLbServiceReadyForPrivateAsync(string userId)
    {
        if (_lbService == null)
        {
            _lbService = new LeaderboardRankings();
            _lbInitialized = false;
        }

        if (_lbInitialized)
        {
            TouchRendererUsage(ref _lastLbRendererUseTicksUtc);
            return true;
        }

        await _lbInitLock.WaitAsync();
        try
        {
            if (_lbInitialized)
            {
                TouchRendererUsage(ref _lastLbRendererUseTicksUtc);
                return true;
            }

            await SendPrivateMessageAsync(userId, "⏳ 首次使用排行榜，正在初始化渲染器，请稍候...");
            await _lbService.InitializeAsync();
            _lbInitialized = true;
            TouchRendererUsage(ref _lastLbRendererUseTicksUtc);
            return true;
        }
        catch (Exception ex)
        {
            await SendPrivateMessageAsync(userId, $"❌ 排行榜渲染器初始化失败: {ex.Message}");
            return false;
        }
        finally
        {
            _lbInitLock.Release();
        }
    }

    private static async Task<bool> EnsureShoutServiceReadyAsync(string groupId, string msgId)
    {
        if (_shoutLogService == null)
        {
            await SendGroupMessageAsync(groupId, msgId, "❌ 喊话渲染器未就绪。");
            return false;
        }

        if (_shoutInitialized)
        {
            TouchRendererUsage(ref _lastShoutRendererUseTicksUtc);
            return true;
        }

        await _shoutInitLock.WaitAsync();
        try
        {
            if (_shoutInitialized)
            {
                TouchRendererUsage(ref _lastShoutRendererUseTicksUtc);
                return true;
            }

            await SendGroupMessageAsync(groupId, msgId, "⏳ 首次使用喊话，正在初始化渲染器，请稍候...");
            await _shoutLogService.InitializeAsync();
            _shoutInitialized = true;
            TouchRendererUsage(ref _lastShoutRendererUseTicksUtc);
            return true;
        }
        catch (Exception ex)
        {
            await SendGroupMessageAsync(groupId, msgId, $"❌ 喊话渲染器初始化失败: {ex.Message}");
            return false;
        }
        finally
        {
            _shoutInitLock.Release();
        }
    }

    private static async Task<bool> EnsureHelpServiceReadyAsync(string groupId, string msgId)
    {
        if (_helpService == null)
        {
            _helpService = new HelpService();
            _helpInitialized = false;
        }

        if (_helpInitialized)
        {
            TouchRendererUsage(ref _lastHelpRendererUseTicksUtc);
            return true;
        }

        await _helpInitLock.WaitAsync();
        try
        {
            if (_helpInitialized)
            {
                TouchRendererUsage(ref _lastHelpRendererUseTicksUtc);
                return true;
            }

            await SendGroupMessageAsync(groupId, msgId, "⏳ 首次使用帮助菜单，正在初始化渲染器，请稍候...");
            await _helpService.InitializeAsync();
            _helpInitialized = true;
            TouchRendererUsage(ref _lastHelpRendererUseTicksUtc);
            return true;
        }
        catch (Exception ex)
        {
            await SendGroupMessageAsync(groupId, msgId, $"❌ 帮助渲染器初始化失败: {ex.Message}");
            return false;
        }
        finally
        {
            _helpInitLock.Release();
        }
    }

    private static async Task<bool> EnsureHelpServiceReadyForPrivateAsync(string userId)
    {
        if (_helpService == null)
        {
            _helpService = new HelpService();
            _helpInitialized = false;
        }

        if (_helpInitialized)
        {
            TouchRendererUsage(ref _lastHelpRendererUseTicksUtc);
            return true;
        }

        await _helpInitLock.WaitAsync();
        try
        {
            if (_helpInitialized)
            {
                TouchRendererUsage(ref _lastHelpRendererUseTicksUtc);
                return true;
            }

            await SendPrivateMessageAsync(userId, "⏳ 首次使用帮助菜单，正在初始化渲染器，请稍候...");
            await _helpService.InitializeAsync();
            _helpInitialized = true;
            TouchRendererUsage(ref _lastHelpRendererUseTicksUtc);
            return true;
        }
        catch (Exception ex)
        {
            await SendPrivateMessageAsync(userId, $"❌ 帮助渲染器初始化失败: {ex.Message}");
            return false;
        }
        finally
        {
            _helpInitLock.Release();
        }
    }

    private static async Task HandleLbCommandAsync(string[] parts, string? groupId, string? msgId, string? userId)
    {
        if (string.IsNullOrWhiteSpace(groupId) || NeedMsgIdButMissing(msgId)) return;
        if (!await EnsureLbServiceReadyAsync(groupId, msgId))
        {
            return;
        }

        string playerName;
        if (parts.Length >= 2)
        {
            playerName = parts[1];
        }
        else
        {
            if (string.IsNullOrWhiteSpace(userId) || !_bindService.TryGetBindingByQq(userId, out var binding))
            {
                await SendGroupMessageAsync(groupId, msgId, "❌ 未检测到你的绑定信息，请先执行 !bind <布吉岛用户名>，或使用 !lb <玩家名>");
                return;
            }

            if (string.IsNullOrWhiteSpace(binding.BjdName))
            {
                await SendGroupMessageAsync(groupId, msgId, "❌ 绑定信息缺少布吉岛用户名，请重新执行 !bind <布吉岛用户名>");
                return;
            }

            playerName = binding.BjdName;
        }

        var displayPlayerName = GetDisplayPlayerIdForCurrentSource(playerName);
        await SendPendingUpdateToGroupIfExistsAsync(groupId, msgId, userId);

        var apiResult = await RequestLeaderboardAsync(playerName);
        if (!apiResult.Success)
        {
            await SendGroupMessageAsync(groupId, msgId, $"❌ 排行榜 API 连接失败: {apiResult.ErrorMessage}");
            return;
        }

        SaveLeaderboardDailySnapshot(playerName, apiResult.JsonResponse!, null, "manual");
        _dataStore.RecordQueryablePlayerId(playerName);

        var lbApiCode = ParseApiCode(apiResult.JsonResponse);
        if (lbApiCode == 404)
        {
            var lbMsg = ParseApiMessage(apiResult.JsonResponse) ?? "未上榜.";
            await SendGroupMessageAsync(groupId, msgId, $"ℹ️ {displayPlayerName} {lbMsg}");
            return;
        }

        var playerInfo = _bindService.ExtractPlayerInfo(apiResult.JsonResponse!, playerName);
        if (string.IsNullOrWhiteSpace(playerInfo.Uuid))
        {
            var playerApiResult = await RequestPlayerInfoAsync(playerName);
            if (playerApiResult.Success && !string.IsNullOrWhiteSpace(playerApiResult.JsonResponse))
            {
                playerInfo = _bindService.ExtractPlayerFromPlayerApi(playerApiResult.JsonResponse, playerName);
            }
        }

        try
        {
            var avatarSrc = _infoPhotoService.TryBuildAvatarDataUri(playerInfo.Uuid);
            using var imgStream = await _lbService.GenerateLeaderboardImageAsync(
                apiResult.JsonResponse!,
                displayPlayerName,
                avatarSrc,
                playerInfo.Uuid);

            await SendGroupImageAsync(groupId, msgId, imgStream);
            SaveLeaderboardDailySnapshot(playerName, apiResult.JsonResponse!, playerInfo.Uuid, "manual");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LB] 渲染或发送失败: {ex}");
            await SendGroupMessageAsync(groupId, msgId, "❌ 生成排行榜图片失败，请稍后重试。");
        }
    }

    private static async Task HandleShoutLogCommandAsync(string[] parts, string? groupId, string? msgId, string? userId)
    {
        if (string.IsNullOrWhiteSpace(groupId) || NeedMsgIdButMissing(msgId)) return;
        if (!await EnsureShoutServiceReadyAsync(groupId, msgId))
        {
            return;
        }

        DateTime startTime;
        DateTime endTime;
        int durationMinutes;

        if (parts.Length < 2)
        {
            endTime = DateTime.Now;
            startTime = endTime.AddMinutes(-30);
            durationMinutes = 30;
        }
        else
        {
            var rawTime = string.Concat(parts.Skip(1));
            if (!TryParseShoutStartTime(rawTime, out startTime, out var parseError))
            {
                await SendGroupMessageAsync(groupId, msgId, $"❌ 时间格式错误: {parseError}");
                return;
            }

            endTime = new DateTime(startTime.Year, startTime.Month, startTime.Day, startTime.Hour, 0, 0)
                .AddHours(1);
            durationMinutes = (int)(endTime - startTime).TotalMinutes;
            if (durationMinutes <= 0) durationMinutes = 60;
        }

        await SendPendingUpdateToGroupIfExistsAsync(groupId, msgId, userId);

        try
        {
            using var stream = await _shoutLogService.GenerateShoutImageAsync(startTime, durationMinutes);
            await SendGroupImageAsync(groupId, msgId, stream);
        }
        catch (InvalidOperationException ex)
        {
            await SendGroupMessageAsync(groupId, msgId, $"❌ {ex.Message}");
            Console.WriteLine($"[喊话] 无数据: {ex.Message}");
        }
        catch (Exception ex)
        {
            await SendGroupMessageAsync(groupId, msgId, $"❌ 查询喊话记录失败: {ex.Message}");
            Console.WriteLine($"[喊话] 查询失败: {ex}");
        }
    }

    private static async Task HandleHelpCommandAsync(string? groupId, string? msgId)
    {
        if (string.IsNullOrWhiteSpace(groupId) || NeedMsgIdButMissing(msgId)) return;
        if (!await EnsureHelpServiceReadyAsync(groupId, msgId))
        {
            return;
        }

        try
        {
            using var stream = await _helpService.GenerateHelpImageAsync();
            await SendGroupImageAsync(groupId, msgId, stream);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[帮助] 发送失败: {ex}");
            await SendGroupMessageAsync(groupId, msgId, "❌ 帮助菜单生成失败，请稍后再试。");
        }
    }

    private static bool TryParseShoutStartTime(string rawInput, out DateTime startTime, out string error)
    {
        startTime = default;
        error = string.Empty;

        var text = (rawInput ?? string.Empty)
            .Trim()
            .Replace(" ", string.Empty)
            .Replace("：", ":", StringComparison.Ordinal)
            .Replace("。", ".", StringComparison.Ordinal);

        var match = ShoutTimeRegex.Match(text);
        if (!match.Success)
        {
            error = "请使用“2月13日13点30”或“2.13日13.30”";
            return false;
        }

        var month = int.Parse(match.Groups["m"].Value);
        var day = int.Parse(match.Groups["d"].Value);
        var hour = int.Parse(match.Groups["h"].Value);
        var minute = int.Parse(match.Groups["min"].Value);

        if (month < 1 || month > 12)
        {
            error = "月份应在 1-12";
            return false;
        }

        if (hour < 0 || hour > 23 || minute < 0 || minute > 59)
        {
            error = "小时应在 0-23，分钟应在 0-59";
            return false;
        }

        var year = DateTime.Now.Year;
        var maxDay = DateTime.DaysInMonth(year, month);
        if (day < 1 || day > maxDay)
        {
            error = $"该月份日期应在 1-{maxDay}";
            return false;
        }

        startTime = new DateTime(year, month, day, hour, minute, 0);
        return true;
    }

    private static string BuildApiCacheKey(string apiType, string playerName)
    {
        var normalizedPlayer = (playerName ?? string.Empty).Trim().ToLowerInvariant();
        return $"{apiType}:{normalizedPlayer}";
    }

    private static bool TryGetCachedApiResult(string cacheKey, out ApiCallResult result)
    {
        MaintainApiCacheIfNeeded();
        if (_apiResultCache.TryGetValue(cacheKey, out var cached)
            && cached.ExpiresAtUtc > DateTimeOffset.UtcNow)
        {
            result = cached.Result;
            return true;
        }

        _apiResultCache.TryRemove(cacheKey, out _);
        result = default;
        return false;
    }

    private static ApiCallResult CacheApiResult(string cacheKey, ApiCallResult result, TimeSpan ttl)
    {
        if (!result.Success || string.IsNullOrWhiteSpace(result.JsonResponse))
        {
            return result;
        }

        _apiResultCache[cacheKey] = new ApiResultCacheEntry(result, DateTimeOffset.UtcNow.Add(ttl));
        MaintainApiCacheIfNeeded();
        return result;
    }

    private static async Task<ApiCallResult?> TryAwaitApiCallWithTimeoutAsync(Task<ApiCallResult> task, TimeSpan timeout)
    {
        if (task.IsCompleted)
        {
            return await task;
        }

        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (completed != task)
        {
            return null;
        }

        return await task;
    }

    private static async Task<ApiCallResult> RequestGameStatsAsync(string playerName)
    {
        return await RequestGameStatsByGameTypeAsync(playerName, _gameType);
    }

    private static async Task<ApiCallResult> RequestSkywarsStatsAsync(string playerName)
    {
        return await RequestGameStatsByGameTypeAsync(playerName, "skywars");
    }

    private static async Task<ApiCallResult> RequestGameStatsByGameTypeAsync(string playerName, string gameType, bool useCache = true)
    {
        var resolvedGameType = string.IsNullOrWhiteSpace(gameType) ? "bedwars" : gameType.Trim().ToLowerInvariant();
        var cacheKey = BuildApiCacheKey($"gamestats:{resolvedGameType}", playerName);
        if (useCache && TryGetCachedApiResult(cacheKey, out var cached))
        {
            return cached;
        }

        ApiCallResult? lastResult = null;
        foreach (var token in GetApiTokensInTryOrder())
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _apiUrl);
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 BedwarsBot/1.0");
                if (!string.IsNullOrWhiteSpace(token))
                {
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                }

                var body = JsonConvert.SerializeObject(new { username = playerName, gametype = resolvedGameType });
                request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

                using var response = await _httpClient.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    var error = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                        ? "API 鉴权失败(401)，将尝试下一个Token"
                        : $"API返回 {(int)response.StatusCode} ({response.ReasonPhrase})";
                    Console.WriteLine($"[API] 请求失败: {_apiUrl}, Status={(int)response.StatusCode}, Body={content}");
                    lastResult = ApiCallResult.Fail(error);
                    continue;
                }

                var okResult = ApiCallResult.Ok(content);
                return useCache ? CacheApiResult(cacheKey, okResult, GameStatsCacheTtl) : okResult;
            }
            catch (Exception ex)
            {
                lastResult = ApiCallResult.Fail(ex.Message);
            }
        }

        return lastResult ?? ApiCallResult.Fail("API请求失败");
    }

    private static async Task<ApiCallResult> RequestPlayerInfoAsync(string playerName)
    {
        var cacheKey = BuildApiCacheKey("player", playerName);
        if (TryGetCachedApiResult(cacheKey, out var cached))
        {
            return cached;
        }

        // 兼容服务端可能的不同参数命名/方法
        var bodies = new[]
        {
            JsonConvert.SerializeObject(new { playername = playerName }),
            JsonConvert.SerializeObject(new { username = playerName }),
            JsonConvert.SerializeObject(new { name = playerName })
        };

        foreach (var body in bodies)
        {
            var postResult = await SendJsonRequestAsync(HttpMethod.Post, _playerApiUrl, body);
            if (postResult.Success && HasUuid(postResult.JsonResponse))
            {
                return CacheApiResult(cacheKey, postResult, PlayerInfoCacheTtl);
            }
        }

        var q1 = $"{_playerApiUrl}?playername={Uri.EscapeDataString(playerName)}";
        var get1 = await SendJsonRequestAsync(HttpMethod.Get, q1, null);
        if (get1.Success && HasUuid(get1.JsonResponse))
        {
            return CacheApiResult(cacheKey, get1, PlayerInfoCacheTtl);
        }

        var q2 = $"{_playerApiUrl}?username={Uri.EscapeDataString(playerName)}";
        var get2 = await SendJsonRequestAsync(HttpMethod.Get, q2, null);
        if (get2.Success && HasUuid(get2.JsonResponse))
        {
            return CacheApiResult(cacheKey, get2, PlayerInfoCacheTtl);
        }

        return ApiCallResult.Fail("player接口未返回uuid，请检查接口参数或权限");
    }

    private static async Task<ApiCallResult> RequestLeaderboardAsync(
        string playerName,
        bool useCache = true,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = BuildApiCacheKey("leaderboard", playerName);
        if (useCache && TryGetCachedApiResult(cacheKey, out var cached))
        {
            return cached;
        }

        var urls = BuildLeaderboardUrls();
        ApiCallResult? lastResult = null;

        foreach (var url in urls)
        {
            var result = await SendLeaderboardRequestAsync(url, playerName, cancellationToken);
            if (result.Success)
            {
                lock (_leaderboardUrlLock)
                {
                    _lastWorkingLeaderboardUrl = url;
                }

                return useCache ? CacheApiResult(cacheKey, result, LeaderboardCacheTtl) : result;
            }
            lastResult = result;
        }

        return lastResult ?? ApiCallResult.Fail("排行榜接口请求失败");
    }

    private static IEnumerable<string> BuildLeaderboardUrls()
    {
        var urls = new List<string>();
        lock (_leaderboardUrlLock)
        {
            if (!string.IsNullOrWhiteSpace(_lastWorkingLeaderboardUrl))
            {
                urls.Add(_lastWorkingLeaderboardUrl!);
            }
        }

        if (!string.IsNullOrWhiteSpace(_leaderboardApiUrl))
        {
            urls.Add(_leaderboardApiUrl);
            urls.Add(_leaderboardApiUrl.Replace("/leaderboard", "/leaderboards", StringComparison.OrdinalIgnoreCase));
        }

        urls.Add("https://api.mcbjd.net/v2/leaderboard");
        urls.Add("https://api.mcbjd.net/v2/leaderboards");
        return urls.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<ApiCallResult> SendLeaderboardRequestAsync(
        string url,
        string playerName,
        CancellationToken cancellationToken = default)
    {
        ApiCallResult? lastResult = null;
        foreach (var token in GetApiTokensInTryOrder())
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 BedwarsBot/1.0");
                if (!string.IsNullOrWhiteSpace(token))
                {
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                }

                var body = JsonConvert.SerializeObject(new { username = playerName, gametype = _gameType });
                request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[LB API] 请求失败: {url}, Status={(int)response.StatusCode}, Body={content}");
                    lastResult = ApiCallResult.Fail($"API返回 {(int)response.StatusCode} ({response.ReasonPhrase})");
                    continue;
                }

                return ApiCallResult.Ok(content);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return ApiCallResult.Fail("排行榜请求已取消");
            }
            catch (Exception ex)
            {
                lastResult = ApiCallResult.Fail(ex.Message);
            }
        }

        return lastResult ?? ApiCallResult.Fail("排行榜接口请求失败");
    }

    private static async Task<ApiCallResult> SendJsonRequestAsync(HttpMethod method, string url, string? body)
    {
        ApiCallResult? lastResult = null;
        foreach (var token in GetApiTokensInTryOrder())
        {
            try
            {
                using var request = new HttpRequestMessage(method, url);
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 BedwarsBot/1.0");
                if (!string.IsNullOrWhiteSpace(token))
                {
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                }

                if (!string.IsNullOrWhiteSpace(body))
                {
                    request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                }

                using var response = await _httpClient.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    lastResult = ApiCallResult.Fail($"API返回 {(int)response.StatusCode} ({response.ReasonPhrase})");
                    continue;
                }

                return ApiCallResult.Ok(content);
            }
            catch (Exception ex)
            {
                lastResult = ApiCallResult.Fail(ex.Message);
            }
        }

        return lastResult ?? ApiCallResult.Fail("API请求失败");
    }

    private static List<string> LoadApiKeys(IConfiguration config, string singleApiKey)
    {
        var keys = new List<string>();

        if (!string.IsNullOrWhiteSpace(singleApiKey))
        {
            keys.Add(singleApiKey.Trim());
        }

        var inline = config["Bedwars:ApiKeys"];
        if (!string.IsNullOrWhiteSpace(inline))
        {
            foreach (var part in inline.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!string.IsNullOrWhiteSpace(part)) keys.Add(part);
            }
        }

        foreach (var child in config.GetSection("Bedwars:ApiKeys").GetChildren())
        {
            var value = child.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(value)) keys.Add(value);
        }

        return keys
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<string> GetApiTokensInTryOrder()
    {
        lock (_apiKeyLock)
        {
            if (_apiKeys.Count == 0)
            {
                return new[] { string.Empty };
            }

            if (_apiKeys.Count == 1)
            {
                return new[] { _apiKeys[0] };
            }

            var start = _apiKeyCursor % _apiKeys.Count;
            _apiKeyCursor = (_apiKeyCursor + 1) % _apiKeys.Count;

            var ordered = new List<string>(_apiKeys.Count);
            for (var i = 0; i < _apiKeys.Count; i++)
            {
                ordered.Add(_apiKeys[(start + i) % _apiKeys.Count]);
            }

            return ordered;
        }
    }

    private static bool HasUuid(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            var obj = JObject.Parse(json);
            var uuid = obj.SelectToken("data.uuid")?.ToString();
            return !string.IsNullOrWhiteSpace(uuid);
        }
        catch
        {
            return false;
        }
    }

    private static int? ParseApiCode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var obj = JObject.Parse(json);
            var token = obj["code"];
            if (token == null) return null;
            if (token.Type == JTokenType.Integer) return token.Value<int>();
            return int.TryParse(token.ToString(), out var code) ? code : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ParseApiMessage(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var obj = JObject.Parse(json);
            var message = obj["message"]?.ToString();
            return string.IsNullOrWhiteSpace(message) ? null : message;
        }
        catch
        {
            return null;
        }
    }

    private static string? ParseBwxpShow(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var obj = JObject.Parse(json);
            var raw = obj.SelectToken("data.bwxp_show")?.ToString();
            return string.IsNullOrWhiteSpace(raw) ? null : raw;
        }
        catch
        {
            return null;
        }
    }

    private static string? ParseSwxpShow(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var obj = JObject.Parse(json);
            var raw = obj.SelectToken("data.swxp_show")?.ToString();
            return string.IsNullOrWhiteSpace(raw) ? null : raw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task StartAspNetWebhookHostAsync(BotConfig botConfig, CancellationToken token)
    {
        // 防止重复启动 Kestrel
        if (_aspNetWebhookApp != null)
        {
            return;
        }

        var webhookConfig = botConfig.Webhook ?? new WebhookConfig();
        var listenUrls = ResolveAspNetWebhookListenUrls(webhookConfig);
        var callbackPath = NormalizeWebhookCallbackPath(webhookConfig.CallbackPath);
        const string defaultCallbackPath = "/api/qqbot/webhook";

        var builder = WebApplication.CreateBuilder();

        builder.WebHost.UseUrls(listenUrls.ToArray());
        builder.Services.AddControllers();

        var app = builder.Build();

        // 解析 Nginx 传来的 X-Forwarded-For / X-Forwarded-Proto
        var forwardedOptions = BuildForwardedHeadersOptions(webhookConfig);
        app.UseForwardedHeaders(forwardedOptions);

        // 允许通过配置修改回调路径，并复用既有控制器处理逻辑。
        if (!string.Equals(callbackPath, defaultCallbackPath, StringComparison.OrdinalIgnoreCase))
        {
            app.Use(async (context, next) =>
            {
                if (string.Equals(context.Request.Method, "POST", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(context.Request.Path.Value, callbackPath, StringComparison.OrdinalIgnoreCase))
                {
                    context.Request.Path = defaultCallbackPath;
                }

                await next();
            });
        }

        // 不启用 HTTPS 重定向；直接走 HTTP 即可
        app.MapControllers();

        _aspNetWebhookApp = app;
        _ = app.RunAsync(token);

        Console.WriteLine($"[Webhook][ASP.NET] 监听地址: {string.Join(", ", listenUrls)}");
        Console.WriteLine($"[Webhook][ASP.NET] 回调路径: {callbackPath}");
        if (!string.Equals(callbackPath, defaultCallbackPath, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[Webhook][ASP.NET] 路径映射: {callbackPath} -> {defaultCallbackPath}");
        }

        Console.WriteLine("[Webhook][ASP.NET] 已启用 ForwardedHeaders（X-Forwarded-For / X-Forwarded-Proto）");
    }

    private static List<string> ResolveAspNetWebhookListenUrls(WebhookConfig webhookConfig)
    {
        var urls = new List<string>();
        var prefixes = webhookConfig.ListenPrefixes ?? new List<string>();
        foreach (var raw in prefixes)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var text = raw.Trim();
            var schemeSeparator = text.IndexOf("://", StringComparison.Ordinal);
            if (schemeSeparator <= 0)
            {
                Console.WriteLine($"[Webhook][ASP.NET] 忽略无效 ListenPrefixes: {raw}");
                continue;
            }

            var scheme = text[..schemeSeparator].ToLowerInvariant();
            if (scheme is not "http" and not "https")
            {
                Console.WriteLine($"[Webhook][ASP.NET] 忽略无效 ListenPrefixes: {raw}");
                continue;
            }

            var rest = text[(schemeSeparator + 3)..];
            var slashIndex = rest.IndexOf('/');
            var authority = (slashIndex >= 0 ? rest[..slashIndex] : rest).Trim();
            if (string.IsNullOrWhiteSpace(authority))
            {
                Console.WriteLine($"[Webhook][ASP.NET] 忽略无效 ListenPrefixes: {raw}");
                continue;
            }

            if (authority.StartsWith("+:", StringComparison.Ordinal) || authority.StartsWith("*:", StringComparison.Ordinal))
            {
                authority = $"0.0.0.0:{authority[2..]}";
            }
            else if (authority is "+" or "*")
            {
                authority = "0.0.0.0";
            }

            urls.Add($"{scheme}://{authority}");
        }

        if (urls.Count == 0)
        {
            urls.Add("http://0.0.0.0:5001");
        }

        return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string NormalizeWebhookCallbackPath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return "/api/qqbot/webhook";
        }

        var path = rawPath.Trim();
        var delimiterIndex = path.IndexOfAny(new[] { '?', '#' });
        if (delimiterIndex >= 0)
        {
            path = path[..delimiterIndex];
        }

        if (!path.StartsWith("/", StringComparison.Ordinal))
        {
            path = "/" + path;
        }

        while (path.Length > 1 && path.EndsWith("/", StringComparison.Ordinal))
        {
            path = path[..^1];
        }

        return path;
    }

    private static ForwardedHeadersOptions BuildForwardedHeadersOptions(WebhookConfig webhookConfig)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 3
        };

        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();

        if (!webhookConfig.TrustForwardedHeaders)
        {
            // 不信任转发头时，只信任本机回环地址
            options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Loopback, 8));
            options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.IPv6Loopback, 128));
            return options;
        }

        var trusted = webhookConfig.TrustedProxyIps ?? new List<string>();
        if (trusted.Count == 0)
        {
            // 未配置白名单时：信任任意来源转发头（建议配合防火墙/Nginx ACL）
            options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse("0.0.0.0"), 0));
            options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse("::"), 0));
            return options;
        }

        foreach (var raw in trusted)
        {
            if (!TryParseTrustedProxyRule(raw, out var ip, out var prefixLength))
            {
                Console.WriteLine($"[Webhook][ASP.NET] 忽略无效 TrustedProxyIps: {raw}");
                continue;
            }

            var maxPrefix = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            if (prefixLength == maxPrefix)
            {
                options.KnownProxies.Add(ip);
            }
            else
            {
                options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(ip, prefixLength));
            }
        }

        return options;
    }

    private static bool TryParseTrustedProxyRule(string? raw, out IPAddress ip, out int prefixLength)
    {
        ip = IPAddress.None;
        prefixLength = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var text = raw.Trim();
        if (!text.Contains('/'))
        {
            if (!IPAddress.TryParse(text, out var parsed))
            {
                return false;
            }

            ip = NormalizeIpAddress(parsed);
            prefixLength = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            return true;
        }

        var parts = text.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var cidrIp))
        {
            return false;
        }

        var normalizedIp = NormalizeIpAddress(cidrIp);
        if (!int.TryParse(parts[1], out var cidrPrefix))
        {
            return false;
        }

        var max = normalizedIp.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (cidrPrefix < 0 || cidrPrefix > max)
        {
            return false;
        }

        ip = normalizedIp;
        prefixLength = cidrPrefix;
        return true;
    }

    private static IPAddress NormalizeIpAddress(IPAddress ip)
    {
        return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
    }

    private static string ResolveShoutLogDbPath(IConfiguration config, string rootDir, BotConfig? botConfig)
    {
        if (botConfig != null && !string.IsNullOrWhiteSpace(botConfig.ShoutLogDbPath))
        {
            if (!botConfig.ShoutLogDbPath.Contains("你的Node项目路径", StringComparison.Ordinal))
            {
                return Path.IsPathRooted(botConfig.ShoutLogDbPath)
                    ? botConfig.ShoutLogDbPath
                    : Path.Combine(rootDir, botConfig.ShoutLogDbPath);
            }
        }

        var configured = config["ShoutLog:DbPath"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return Path.Combine(rootDir, "pz", "shoutlog.db");
        }

        if (Path.IsPathRooted(configured))
        {
            return configured;
        }

        return Path.Combine(rootDir, configured);
    }

    private static string ResolveSessionDbPath(IConfiguration config, string rootDir)
    {
        var configured = config["Session:DbPath"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return Path.Combine(rootDir, "pz", "session.db");
        }

        if (Path.IsPathRooted(configured))
        {
            return configured;
        }

        return Path.Combine(rootDir, configured);
    }

    private static string ResolveBwHistoryDbPath(IConfiguration config, string rootDir)
    {
        var configured = config["BwHistory:DbPath"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return Path.Combine(rootDir, "pz", "bw_history.db");
        }

        if (Path.IsPathRooted(configured))
        {
            return configured;
        }

        return Path.Combine(rootDir, configured);
    }

    private static string ResolveLeaderboardSnapshotDbPath(IConfiguration config, string rootDir)
    {
        var configured = config["LeaderboardHistory:DbPath"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return Path.Combine(rootDir, "pz", "leaderboard_history.db");
        }

        if (Path.IsPathRooted(configured))
        {
            return configured;
        }

        return Path.Combine(rootDir, configured);
    }

    private static string ResolveRootDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        DirectoryInfo? firstProjectDir = null;
        for (var i = 0; i < 8 && dir != null; i++)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }

            if (firstProjectDir == null && File.Exists(Path.Combine(dir.FullName, "BedwarsBot.csproj")))
            {
                firstProjectDir = dir;
            }

            dir = dir.Parent;
        }

        if (firstProjectDir?.Parent != null)
        {
            return firstProjectDir.Parent.FullName;
        }

        return Directory.GetCurrentDirectory();
    }

    private static void InitializeStartupLog(string rootDir, DateTime startupTime)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        _originalConsoleOut = originalOut;
        _originalConsoleError = originalError;

        try
        {
            var logDir = Path.Combine(rootDir, "log");
            Directory.CreateDirectory(logDir);

            var baseName = startupTime.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var logPath = Path.Combine(logDir, $"{baseName}.log");
            var suffix = 1;
            while (File.Exists(logPath))
            {
                logPath = Path.Combine(logDir, $"{baseName}_{suffix}.log");
                suffix++;
            }

            var fileStream = new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
            _startupLogWriter = TextWriter.Synchronized(new StreamWriter(fileStream, new UTF8Encoding(false)) { AutoFlush = true });

            Console.SetOut(TextWriter.Synchronized(new TeeTextWriter(originalOut, _startupLogWriter)));
            Console.SetError(TextWriter.Synchronized(new TeeTextWriter(originalError, _startupLogWriter)));
            _startupLogPath = logPath;
            Console.WriteLine($"[日志] 本次启动日志文件: {logPath}");
        }
        catch (Exception ex)
        {
            try
            {
                originalOut.WriteLine($"[日志] 初始化失败: {ex.Message}");
            }
            catch
            {
                // ignored
            }
        }
    }

    private static void CloseStartupLog()
    {
        try
        {
            if (_originalConsoleOut != null)
            {
                Console.SetOut(_originalConsoleOut);
            }

            if (_originalConsoleError != null)
            {
                Console.SetError(_originalConsoleError);
            }
        }
        catch
        {
            // ignored
        }

        try
        {
            _startupLogWriter?.Flush();
            _startupLogWriter?.Dispose();
        }
        catch
        {
            // ignored
        }

        _startupLogWriter = null;
        _startupLogPath = null;
    }

    private static string NormalizeCommand(string rawCmd)
    {
        if (string.IsNullOrWhiteSpace(rawCmd)) return string.Empty;

        var cmd = rawCmd.Trim();
        if (cmd.Length == 0) return string.Empty;

        var first = cmd[0];
        if ((first == '!' || first == '/' || first == '=' || first == '／' || first == '＝') && cmd.Length > 1)
        {
            cmd = cmd[1..];
        }

        return cmd.ToLowerInvariant();
    }

    private static void LogCommandInvocation(string channel, string? groupId, string? userId, string raw)
    {
        var safeRaw = CompactLogText(raw, 200);
        if (string.Equals(channel, "group", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[调用][群] group={groupId ?? string.Empty}, user={userId ?? string.Empty}, cmd={safeRaw}");
            return;
        }

        Console.WriteLine($"[调用][私聊] user={userId ?? string.Empty}, cmd={safeRaw}");
    }

    private static void LogCallEchoText(string channel, string? groupId, string? userId, string callText, string status)
    {
        var text = CompactLogText(callText, 200);
        if (string.Equals(channel, "group", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[叫词][群] group={groupId ?? string.Empty}, user={userId ?? string.Empty}, status={status}, text={text}");
            return;
        }

        Console.WriteLine($"[叫词][私聊] user={userId ?? string.Empty}, status={status}, text={text}");
    }

    private static void LogBotTextSend(string channel, string targetId, string content)
    {
        var text = CompactLogText(content, 200);
        if (string.Equals(channel, "group", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[发送][群文本] group={targetId}, text={text}");
            return;
        }

        Console.WriteLine($"[发送][私聊文本] user={targetId}, text={text}");
    }

    private static void LogBotImageSend(string channel, string targetId, string? caption)
    {
        var cap = string.IsNullOrWhiteSpace(caption) ? "-" : CompactLogText(caption, 120);
        if (string.Equals(channel, "group", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[发送][群图片] group={targetId}, caption={cap}");
            return;
        }

        Console.WriteLine($"[发送][私聊图片] user={targetId}, caption={cap}");
    }

    private static string CompactLogText(string? text, int maxLength)
    {
        var normalized = (text ?? string.Empty)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        if (normalized.Length == 0)
        {
            return "<empty>";
        }

        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized[..maxLength] + "...";
    }

    private static bool IsNapcatMessageSource()
    {
        return _currentMessageSource.Value is MessageSource.NapcatGroup or MessageSource.NapcatPrivate;
    }

    private static bool IsNapcatGroupMessageSource()
    {
        return _currentMessageSource.Value == MessageSource.NapcatGroup;
    }

    private static bool IsOfficialGroupMessageSource()
    {
        return _currentMessageSource.Value == MessageSource.OfficialGroup;
    }

    private static string GetDisplayPlayerIdForCurrentSource(string playerId)
    {
        if (!IsOfficialGroupMessageSource())
        {
            return playerId;
        }

        return MaskPlayerId(playerId);
    }

    private static string MaskPlayerId(string playerId)
    {
        if (string.IsNullOrWhiteSpace(playerId))
        {
            return playerId;
        }

        var trimmed = playerId.Trim();
        if (trimmed.Length <= 1)
        {
            return "*";
        }

        if (trimmed.Length == 2)
        {
            return $"{trimmed[0]}*";
        }

        return $"{trimmed[0]}{new string('*', trimmed.Length - 2)}{trimmed[^1]}";
    }

    private static string? BuildGroupImageAtPrefix()
    {
        var requesterUserId = _currentGroupRequesterUserId.Value;
        if (string.IsNullOrWhiteSpace(requesterUserId))
        {
            return null;
        }

        if (IsOfficialGroupMessageSource())
        {
            return $"<@{requesterUserId}>";
        }

        if (IsNapcatGroupMessageSource())
        {
            return $"[CQ:at,qq={requesterUserId}]";
        }

        return null;
    }

    private static string? CombineImageCaptionWithAtPrefix(string? atPrefix, string? caption)
    {
        if (string.IsNullOrWhiteSpace(atPrefix))
        {
            return caption;
        }

        if (string.IsNullOrWhiteSpace(caption))
        {
            return atPrefix;
        }

        return $"{atPrefix} {caption}";
    }

    private static async Task<string?> SendGroupMessageWithIdAsync(string groupId, string? msgId, string content)
    {
        if (string.IsNullOrEmpty(groupId))
        {
            return null;
        }

        if (IsNapcatMessageSource())
        {
            if (_napcatBot == null)
            {
                return null;
            }

            LogBotTextSend("group", groupId, content);
            return await _napcatBot.SendTextAndGetMessageIdAsync(groupId, content);
        }

        if (IsOfficialGroupMessageSource())
        {
            if (_qqBot == null)
            {
                return null;
            }

            var officialReferenceMsgId = msgId ?? string.Empty;
            int? officialMsgSeq = null;
            if (!string.IsNullOrWhiteSpace(officialReferenceMsgId))
            {
                officialMsgSeq = GetNextMsgSeq(officialReferenceMsgId);
            }

            LogBotTextSend("group", groupId, content);
            return await _qqBot.SendTextAndGetMessageIdAsync(groupId, officialReferenceMsgId, content, officialMsgSeq, useEventId: true);
        }

        if (_napcatBot != null && (_qqBot == null || string.IsNullOrWhiteSpace(msgId)))
        {
            LogBotTextSend("group", groupId, content);
            return await _napcatBot.SendTextAndGetMessageIdAsync(groupId, content);
        }

        if (_qqBot == null)
        {
            return null;
        }

        var referenceMsgId = msgId ?? string.Empty;
        int? msgSeq = null;
        if (!string.IsNullOrWhiteSpace(referenceMsgId))
        {
            msgSeq = GetNextMsgSeq(referenceMsgId);
        }

        LogBotTextSend("group", groupId, content);
        return await _qqBot.SendTextAndGetMessageIdAsync(groupId, referenceMsgId, content, msgSeq);
    }

    private static Task SendGroupMessageAsync(string groupId, string msgId, string content)
    {
        if (string.IsNullOrEmpty(groupId)) return Task.CompletedTask;

        if (IsNapcatMessageSource())
        {
            if (_napcatBot == null) return Task.CompletedTask;
            LogBotTextSend("group", groupId, content);
            return _napcatBot.SendTextAsync(groupId, content);
        }

        if (IsOfficialGroupMessageSource())
        {
            if (_qqBot == null || string.IsNullOrEmpty(msgId)) return Task.CompletedTask;
            var msgSeq = GetNextMsgSeq(msgId);
            LogBotTextSend("group", groupId, content);
            return _qqBot.SendTextAsync(groupId, msgId, content, msgSeq, useEventId: true);
        }

        if (_napcatBot != null && (_qqBot == null || string.IsNullOrEmpty(msgId)))
        {
            LogBotTextSend("group", groupId, content);
            return _napcatBot.SendTextAsync(groupId, content);
        }

        if (_qqBot == null || string.IsNullOrEmpty(msgId)) return Task.CompletedTask;
        var fallbackMsgSeq = GetNextMsgSeq(msgId);
        LogBotTextSend("group", groupId, content);
        return _qqBot.SendTextAsync(groupId, msgId, content, fallbackMsgSeq);
    }

    private static async Task SendGroupImageAsync(string groupId, string msgId, Stream img, string? caption = null)
    {
        _ = await SendGroupImageAndGetMessageIdAsync(groupId, msgId, img, caption);
    }

    private static async Task<string?> SendGroupImageAndGetMessageIdAsync(string groupId, string msgId, Stream img, string? caption = null)
    {
        if (string.IsNullOrEmpty(groupId)) return null;
        var imageAtPrefix = BuildGroupImageAtPrefix();

        if (IsNapcatGroupMessageSource())
        {
            if (_napcatBot == null) return null;
            var napcatCaption = CombineImageCaptionWithAtPrefix(imageAtPrefix, caption);
            LogBotImageSend("group", groupId, napcatCaption);
            return await _napcatBot.SendImageAndGetMessageIdAsync(groupId, img, napcatCaption);
        }

        if (IsOfficialGroupMessageSource())
        {
            if (_qqBot == null || string.IsNullOrEmpty(msgId)) return null;
            var officialCaption = CombineImageCaptionWithAtPrefix(imageAtPrefix, caption);
            var imageMsgSeq = GetNextMsgSeq(msgId);
            LogBotImageSend("group", groupId, officialCaption);
            return await _qqBot.SendImageAndGetMessageIdAsync(groupId, msgId, img, imageMsgSeq, officialCaption, useEventId: true);
        }

        if (_napcatBot != null && (_qqBot == null || string.IsNullOrEmpty(msgId)))
        {
            LogBotImageSend("group", groupId, caption);
            return await _napcatBot.SendImageAndGetMessageIdAsync(groupId, img, caption);
        }

        if (_qqBot == null || string.IsNullOrEmpty(msgId)) return null;
        var fallbackMsgSeq = GetNextMsgSeq(msgId);
        LogBotImageSend("group", groupId, caption);
        return await _qqBot.SendImageAndGetMessageIdAsync(groupId, msgId, img, fallbackMsgSeq, caption);
    }

    private static Task SendPrivateMessageAsync(string userId, string content)
    {
        if (_napcatBot == null || string.IsNullOrWhiteSpace(userId))
        {
            return Task.CompletedTask;
        }

        LogBotTextSend("private", userId, content);
        return _napcatBot.SendPrivateTextAsync(userId, content);
    }

    private static Task SendPrivateImageAsync(string userId, Stream img, string? caption = null)
    {
        if (_napcatBot == null || string.IsNullOrWhiteSpace(userId))
        {
            return Task.CompletedTask;
        }

        LogBotImageSend("private", userId, caption);
        return _napcatBot.SendPrivateImageAsync(userId, img, caption);
    }

    private static void CountNapcatCommandInvocationIfNeeded(string cmd, string[] parts)
    {
        if (!IsNapcatMessageSource() || string.IsNullOrWhiteSpace(cmd))
        {
            return;
        }

        if (cmd == "bw" && IsBwHistoryQueryParts(parts))
        {
            return;
        }

        var tracked = cmd is "bw" or "lb" or "sess" or "session" or "help" or "帮助" or "菜单" or "喊话"
            or "sw"
            or "bind" or "skin" or "bg" or "ch" or "群发" or "群发编辑" or "群发编辑文本"
            or "update" or "更新" or "开关ai" or "ai开关" or "起床文本"
            or "成语接龙" or "接龙" or "结束接龙" or "停止接龙" or "退出接龙";
        if (tracked)
        {
            _dataStore.IncrementNapcatUsage();
        }
    }

    private static async Task RunNapcatDailyUsageReporterAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_dataStore.TryBuildDailyNapcatReport(DateTime.Now, out var message))
                {
                    await SendPrivateMessageAsync(NapcatDailyReportUserId, message);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NapCat统计] 发送每日调用量失败: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private static async Task RunLowMemoryMaintenanceAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                MaintainApiCacheIfNeeded(force: true);
                TrimCallModerationCacheIfNeeded();
                TrimMsgSeqMapIfNeeded();
                await CloseIdleRenderersIfNeededAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[低内存] 维护任务异常: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(2), token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private static void MaintainApiCacheIfNeeded(bool force = false)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var nowTicks = nowUtc.UtcDateTime.Ticks;

        if (!force)
        {
            var lastTicks = Interlocked.Read(ref _lastApiCacheMaintenanceTicksUtc);
            if (lastTicks > 0
                && (nowTicks - lastTicks) < ApiCacheMaintenanceInterval.Ticks
                && _apiResultCache.Count <= ApiCacheMaxEntries)
            {
                return;
            }
        }

        Interlocked.Exchange(ref _lastApiCacheMaintenanceTicksUtc, nowTicks);

        foreach (var entry in _apiResultCache)
        {
            if (entry.Value.ExpiresAtUtc <= nowUtc)
            {
                _apiResultCache.TryRemove(entry.Key, out _);
            }
        }

        var overflow = _apiResultCache.Count - ApiCacheMaxEntries;
        if (overflow <= 0)
        {
            return;
        }

        var keysToRemove = _apiResultCache
            .OrderBy(kvp => kvp.Value.ExpiresAtUtc)
            .Take(overflow)
            .Select(kvp => kvp.Key)
            .ToArray();
        foreach (var key in keysToRemove)
        {
            _apiResultCache.TryRemove(key, out _);
        }
    }

    private static void TrimCallModerationCacheIfNeeded()
    {
        var count = _callModerationCache.Count;
        if (count <= CallModerationCacheMaxEntries)
        {
            return;
        }

        _callModerationCache.Clear();
        Console.WriteLine($"[低内存] 叫词审核缓存超限({count})，已清空。");
    }

    private static void TrimMsgSeqMapIfNeeded()
    {
        lock (_msgSeqLock)
        {
            TrimMsgSeqMapIfNeededNoLock();
        }
    }

    private static void TrimMsgSeqMapIfNeededNoLock()
    {
        if (_msgSeqMap.Count <= MsgSeqMapMaxEntries)
        {
            return;
        }

        _msgSeqMap.Clear();
        Console.WriteLine("[低内存] 消息序号缓存已清空。");
    }

    private static void TouchRendererUsage(ref long ticksUtc)
    {
        Interlocked.Exchange(ref ticksUtc, DateTimeOffset.UtcNow.UtcTicks);
    }

    private static bool IsRendererIdle(long lastUsedTicksUtc, TimeSpan timeout, DateTimeOffset nowUtc)
    {
        if (lastUsedTicksUtc <= 0)
        {
            return false;
        }

        return nowUtc.UtcTicks - lastUsedTicksUtc >= timeout.Ticks;
    }

    private static async Task CloseIdleRenderersIfNeededAsync()
    {
        var nowUtc = DateTimeOffset.UtcNow;

        if (_sessionInitialized
            && _sessionService != null
            && IsRendererIdle(Interlocked.Read(ref _lastSessionRendererUseTicksUtc), RendererIdleTimeout, nowUtc)
            && await _sessionInitLock.WaitAsync(0))
        {
            try
            {
                if (_sessionInitialized
                    && _sessionService != null
                    && IsRendererIdle(Interlocked.Read(ref _lastSessionRendererUseTicksUtc), RendererIdleTimeout, nowUtc))
                {
                    await _sessionService.CloseAsync();
                    _sessionInitialized = false;
                    Interlocked.Exchange(ref _lastSessionRendererUseTicksUtc, 0);
                    Console.WriteLine("[低内存] Session 渲染器空闲超时，已自动释放。");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[低内存] Session 渲染器释放失败: {ex.Message}");
            }
            finally
            {
                _sessionInitLock.Release();
            }
        }

        if (_swInitialized
            && _swService != null
            && IsRendererIdle(Interlocked.Read(ref _lastSwRendererUseTicksUtc), RendererIdleTimeout, nowUtc)
            && await _swInitLock.WaitAsync(0))
        {
            try
            {
                if (_swInitialized
                    && _swService != null
                    && IsRendererIdle(Interlocked.Read(ref _lastSwRendererUseTicksUtc), RendererIdleTimeout, nowUtc))
                {
                    await _swService.CloseAsync();
                    _swInitialized = false;
                    Interlocked.Exchange(ref _lastSwRendererUseTicksUtc, 0);
                    Console.WriteLine("[低内存] SW 渲染器空闲超时，已自动释放。");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[低内存] SW 渲染器释放失败: {ex.Message}");
            }
            finally
            {
                _swInitLock.Release();
            }
        }

        if (_lbInitialized
            && _lbService != null
            && IsRendererIdle(Interlocked.Read(ref _lastLbRendererUseTicksUtc), RendererIdleTimeout, nowUtc)
            && await _lbInitLock.WaitAsync(0))
        {
            try
            {
                if (_lbInitialized
                    && _lbService != null
                    && IsRendererIdle(Interlocked.Read(ref _lastLbRendererUseTicksUtc), RendererIdleTimeout, nowUtc))
                {
                    await _lbService.CloseAsync();
                    _lbInitialized = false;
                    Interlocked.Exchange(ref _lastLbRendererUseTicksUtc, 0);
                    Console.WriteLine("[低内存] 排行榜渲染器空闲超时，已自动释放。");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[低内存] 排行榜渲染器释放失败: {ex.Message}");
            }
            finally
            {
                _lbInitLock.Release();
            }
        }

        if (_shoutInitialized
            && _shoutLogService != null
            && IsRendererIdle(Interlocked.Read(ref _lastShoutRendererUseTicksUtc), RendererIdleTimeout, nowUtc)
            && await _shoutInitLock.WaitAsync(0))
        {
            try
            {
                if (_shoutInitialized
                    && _shoutLogService != null
                    && IsRendererIdle(Interlocked.Read(ref _lastShoutRendererUseTicksUtc), RendererIdleTimeout, nowUtc))
                {
                    await _shoutLogService.CloseAsync();
                    _shoutInitialized = false;
                    Interlocked.Exchange(ref _lastShoutRendererUseTicksUtc, 0);
                    Console.WriteLine("[低内存] 喊话渲染器空闲超时，已自动释放。");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[低内存] 喊话渲染器释放失败: {ex.Message}");
            }
            finally
            {
                _shoutInitLock.Release();
            }
        }

        if (_helpInitialized
            && _helpService != null
            && IsRendererIdle(Interlocked.Read(ref _lastHelpRendererUseTicksUtc), RendererIdleTimeout, nowUtc)
            && await _helpInitLock.WaitAsync(0))
        {
            try
            {
                if (_helpInitialized
                    && _helpService != null
                    && IsRendererIdle(Interlocked.Read(ref _lastHelpRendererUseTicksUtc), RendererIdleTimeout, nowUtc))
                {
                    await _helpService.CloseAsync();
                    _helpInitialized = false;
                    Interlocked.Exchange(ref _lastHelpRendererUseTicksUtc, 0);
                    Console.WriteLine("[低内存] 帮助渲染器空闲超时，已自动释放。");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[低内存] 帮助渲染器释放失败: {ex.Message}");
            }
            finally
            {
                _helpInitLock.Release();
            }
        }
    }

    private static string ConvertStreamToBase64(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Convert.ToBase64String(ms.ToArray());
    }

    private static int GetNextMsgSeq(string msgId)
    {
        if (string.IsNullOrWhiteSpace(msgId)) return 1;

        lock (_msgSeqLock)
        {
            TrimMsgSeqMapIfNeededNoLock();
            if (!_msgSeqMap.TryGetValue(msgId, out var seq))
            {
                seq = 0;
            }

            seq = Math.Min(seq + 1, 4);
            _msgSeqMap[msgId] = seq;
            return seq;
        }
    }

    private static bool NeedMsgIdButMissing(string? msgId)
    {
        return IsOfficialGroupMessageSource() && string.IsNullOrWhiteSpace(msgId);
    }
}
