using PuppeteerSharp;

namespace BedwarsBot;

public class HelpService
{
    private IBrowser _browser;
    private static readonly string[] EdgeCandidatePaths =
    {
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Edge\Application\msedge.exe")
    };

    public async Task InitializeAsync()
    {
        var edgePath = ResolveEdgeExecutablePath();
        if (string.IsNullOrWhiteSpace(edgePath))
        {
            throw new FileNotFoundException("未找到 Edge 可执行文件。请设置环境变量 EDGE_PATH 或安装 Edge 到默认路径。");
        }

        var profileDir = Path.Combine(AppContext.BaseDirectory, "pw-profiles", "help");
        Directory.CreateDirectory(profileDir);
        try
        {
            _browser = await LaunchBrowserAsync(edgePath, profileDir);
        }
        catch
        {
            var fallbackProfileDir = Path.Combine(Path.GetTempPath(), "bedwarsbot-pw", $"help-{Guid.NewGuid():N}");
            Directory.CreateDirectory(fallbackProfileDir);
            _browser = await LaunchBrowserAsync(edgePath, fallbackProfileDir);
        }
    }

    private static Task<IBrowser> LaunchBrowserAsync(string edgePath, string profileDir)
    {
        return Puppeteer.LaunchAsync(new LaunchOptions
        {
            Headless = true,
            ExecutablePath = edgePath,
            UserDataDir = profileDir,
            DumpIO = true,
            Timeout = 60000,
            Args = new[]
            {
                "--no-first-run",
                "--no-default-browser-check",
                "--disable-gpu",
                "--disable-software-rasterizer",
                "--disable-extensions",
                "--disable-background-networking",
                "--disable-sync",
                "--metrics-recording-only",
                "--mute-audio",
                "--hide-scrollbars",
                "--disable-crash-reporter",
                "--disable-dev-shm-usage",
                "--renderer-process-limit=2",
                "--disable-site-isolation-trials",
                "--no-sandbox",
                "--disable-setuid-sandbox",
                "--disable-features=RendererCodeIntegrity,site-per-process,BackForwardCache,Translate"
            }
        });
    }

    public async Task<Stream> GenerateHelpImageAsync()
    {
        var html = GetHtmlTemplate();

        using var page = await _browser.NewPageAsync();
        await page.SetViewportAsync(new ViewPortOptions { Width = 1040, Height = 100 });
        await page.SetContentAsync(html);

        var body = await page.QuerySelectorAsync(".help-card");
        if (body == null) throw new Exception("帮助菜单渲染失败");

        return await body.ScreenshotStreamAsync(new  ElementScreenshotOptions { OmitBackground = true });
    }

    public async Task CloseAsync()
    {
        if (_browser == null)
        {
            return;
        }

        try
        {
            await _browser.CloseAsync();
        }
        catch
        {
        }

        try
        {
            _browser.Dispose();
        }
        catch
        {
        }

        _browser = null!;
    }

    private static string GetHtmlTemplate()
    {
        var (customFontFaceCss, globalFontFamily) = RenderFontHelper.BuildCustomFontCss();
        return $$"""
        <!DOCTYPE html>
        <html lang="zh-CN">
        <head>
            <meta charset="UTF-8">
            <title>布吉岛 Bot 帮助菜单</title>
            <style>
                @import url('https://fonts.googleapis.com/css2?family=Noto+Sans+SC:wght@500;700;900&family=Nunito:wght@700;800;900&display=swap');
                {{customFontFaceCss}}
                * { margin: 0; padding: 0; box-sizing: border-box; }

                body {
                    font-family: {{globalFontFamily}};
                    background: url('https://images.unsplash.com/photo-1618005182384-a83a8bd57fbe?q=80&w=2064&auto=format&fit=crop') no-repeat center center fixed;
                    background-size: cover;
                    padding: 36px 20px;
                    display: flex;
                    justify-content: center;
                    align-items: center;
                    min-height: 100vh;
                }

                .help-card {
                    width: 980px;
                    background: rgba(255, 255, 255, 0.90);
                    backdrop-filter: blur(24px) saturate(180%);
                    -webkit-backdrop-filter: blur(24px) saturate(180%);
                    border-radius: 28px;
                    border: 1px solid rgba(255, 255, 255, 0.8);
                    box-shadow: 0 30px 60px rgba(0,0,0,0.15), inset 0 2px 5px rgba(255,255,255,1);
                    overflow: hidden;
                    display: flex;
                    flex-direction: column;
                }

                .header {
                    padding: 32px 42px;
                    background: linear-gradient(135deg, #0ea5e9 0%, #2563eb 52%, #1d4ed8 100%);
                    color: #fff;
                    display: flex;
                    align-items: center;
                    gap: 20px;
                    position: relative;
                    overflow: hidden;
                }
                .header::before {
                    content: '';
                    position: absolute;
                    top: 0;
                    left: 0;
                    right: 0;
                    bottom: 0;
                    background-image: radial-gradient(rgba(255,255,255,0.2) 1px, transparent 1px);
                    background-size: 18px 18px;
                    pointer-events: none;
                }
                .icon-box {
                    background: rgba(255,255,255,0.2);
                    padding: 14px;
                    border-radius: 16px;
                    border: 1px solid rgba(255,255,255,0.35);
                    z-index: 1;
                    box-shadow: 0 8px 16px rgba(0,0,0,0.1);
                }
                .title-area { z-index: 1; }
                .title {
                    font-size: 30px;
                    font-weight: 900;
                    letter-spacing: 1px;
                    margin-bottom: 6px;
                    text-shadow: 0 2px 4px rgba(0,0,0,0.12);
                }
                .subtitle {
                    font-size: 14px;
                    font-weight: 700;
                    opacity: 0.93;
                }

                .content {
                    padding: 32px 36px;
                    display: grid;
                    grid-template-columns: 1fr 1fr;
                    gap: 22px;
                    background: rgba(248, 250, 252, 0.62);
                }

                .category {
                    background: #fff;
                    border-radius: 20px;
                    padding: 20px;
                    border: 1px solid rgba(226, 232, 240, 0.86);
                    box-shadow: 0 10px 25px rgba(15, 23, 42, 0.04);
                }
                .category + .category { margin-top: 18px; }

                .cat-title {
                    font-size: 17px;
                    font-weight: 900;
                    color: #0f172a;
                    margin-bottom: 14px;
                    display: flex;
                    align-items: center;
                    gap: 10px;
                    padding-bottom: 10px;
                    border-bottom: 2px dashed #e2e8f0;
                }
                .cat-title svg { color: #2563eb; }

                .cmd-list { display: flex; flex-direction: column; gap: 12px; }
                .cmd-item {
                    display: grid;
                    grid-template-columns: 32px 1fr;
                    gap: 10px;
                    align-items: start;
                }
                .cmd-icon {
                    width: 32px;
                    height: 32px;
                    border-radius: 9px;
                    display: flex;
                    align-items: center;
                    justify-content: center;
                    background: #eff6ff;
                    border: 1px solid #bfdbfe;
                    color: #2563eb;
                    flex-shrink: 0;
                }

                .cmd-syntax {
                    display: inline-block;
                    background: #eff6ff;
                    color: #1d4ed8;
                    padding: 5px 10px;
                    border-radius: 8px;
                    font-family: {{globalFontFamily}}, monospace;
                    font-weight: 900;
                    font-size: 14px;
                    margin-bottom: 6px;
                    border: 1px solid #bfdbfe;
                }
                .cmd-desc {
                    font-size: 13px;
                    color: #475569;
                    font-weight: 700;
                    line-height: 1.5;
                }

                .badge-new {
                    background: linear-gradient(135deg, #f43f5e 0%, #e11d48 100%);
                    color: #fff;
                    font-size: 10px;
                    padding: 2px 7px;
                    border-radius: 999px;
                    margin-left: 6px;
                    vertical-align: middle;
                    box-shadow: 0 2px 4px rgba(225, 29, 72, 0.35);
                    font-family: {{globalFontFamily}};
                    letter-spacing: 1px;
                }
                .highlight { color: #7c3aed; font-weight: 900; }

                .footer {
                    text-align: center;
                    padding: 18px;
                    background: rgba(255, 255, 255, 0.9);
                    color: #64748b;
                    font-size: 12px;
                    font-weight: 800;
                    border-top: 1px solid #e2e8f0;
                }
            </style>
        </head>
        <body>
            <div class="help-card">
                <div class="header">
                    <div class="icon-box">
                        <svg width="34" height="34" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.3" stroke-linecap="round" stroke-linejoin="round">
                            <path d="M12 3l8 4v10l-8 4-8-4V7z"></path>
                            <path d="M8.5 11h7"></path>
                            <path d="M8.5 14h5"></path>
                        </svg>
                    </div>
                    <div class="title-area">
                        <div class="title">布吉岛 Bot 指令大全</div>
                        <div class="subtitle">支持官方群与 NapCat，以下均为最新参数说明</div>
                    </div>
                </div>

                <div class="content">
                    <div class="col">
                        <div class="category">
                            <div class="cat-title">
                                <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.3" stroke-linecap="round" stroke-linejoin="round"><path d="M3 3v18h18"></path><path d="M7 14l3-3 3 2 4-5"></path></svg>
                                核心查询
                            </div>
                            <div class="cmd-list">
                                <div class="cmd-item">
                                    <div class="cmd-icon">
                                        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.3" stroke-linecap="round" stroke-linejoin="round"><path d="M4 6h16"></path><path d="M4 12h16"></path><path d="M4 18h10"></path></svg>
                                    </div>
                                    <div>
                                        <div class="cmd-syntax">!bw [玩家名] [模式]</div>
                                        <div class="cmd-desc">模式关键词支持：solo/单八/1s，2s/双八，4s/44，xp32/48，bw16/64/46（原有关键词保留）。</div>
                                    </div>
                                </div>
                                <div class="cmd-item">
                                    <div class="cmd-icon">
                                        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.3" stroke-linecap="round" stroke-linejoin="round"><path d="M21 10v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-6"></path><path d="M7 10l5 5 5-5"></path><path d="M12 15V3"></path></svg>
                                    </div>
                                    <div>
                                        <div class="cmd-syntax">!bw &lt;玩家名&gt; &lt;x年x月x日&gt; <span class="badge-new">NEW</span></div>
                                        <div class="cmd-desc">查询历史快照，不计入调用数；例如 <span class="highlight">!bw bailan_duck 2026年2月18日</span>。</div>
                                    </div>
                                </div>
                                <div class="cmd-item">
                                    <div class="cmd-icon">
                                        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.3" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"></circle><path d="M12 6v6l4 2"></path></svg>
                                    </div>
                                    <div>
                                        <div class="cmd-syntax">自动扫描 02:00-05:00 <span class="badge-new">NEW</span></div>
                                        <div class="cmd-desc">系统会随机扫描已查询过玩家并存档；若当天手动查过 <span class="highlight">!bw</span>，会自动记为当天最后记录。</div>
                                    </div>
                                </div>
                                <div class="cmd-item">
                                    <div class="cmd-icon">
                                        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.3" stroke-linecap="round" stroke-linejoin="round"><path d="M8 2v4"></path><path d="M16 2v4"></path><rect x="3" y="5" width="18" height="16" rx="2"></rect><path d="M3 10h18"></path></svg>
                                    </div>
                                    <div>
                                        <div class="cmd-syntax">/sess bw &lt;玩家名&gt; [t天数]</div>
                                        <div class="cmd-desc">查看近期涨幅趋势；例如 <span class="highlight">t3</span> 表示对比 3 天前。</div>
                                    </div>
                                </div>
                                <div class="cmd-item">
                                    <div class="cmd-icon">
                                        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.3" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2l3 6 7 1-5 5 1 7-6-3-6 3 1-7-5-5 7-1z"></path></svg>
                                    </div>
                                    <div>
                                        <div class="cmd-syntax">!lb &lt;玩家名&gt;</div>
                                        <div class="cmd-desc">查看排行榜数据与排名面板。</div>
                                    </div>
                                </div>
                                <div class="cmd-item">
                                    <div class="cmd-icon">
                                        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.3" stroke-linecap="round" stroke-linejoin="round"><path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"></path></svg>
                                    </div>
                                    <div>
                                        <div class="cmd-syntax">/喊话 [几月几日几点几分]</div>
                                        <div class="cmd-desc">查询大厅喊话记录，时间参数可省略。</div>
                                    </div>
                                </div>
                            </div>
                        </div>
                    </div>

                    <div class="col">
                        <div class="category">
                            <div class="cat-title">
                                <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.3" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2v20"></path><path d="M17 5H9.5a3.5 3.5 0 0 0 0 7H14a3.5 3.5 0 0 1 0 7H6"></path></svg>
                                个性化
                            </div>
                            <div class="cmd-list">
                                <div class="cmd-item">
                                    <div class="cmd-icon">
                                        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.3" stroke-linecap="round" stroke-linejoin="round"><path d="M16 11V7a4 4 0 0 0-8 0v4"></path><rect x="4" y="11" width="16" height="10" rx="2"></rect></svg>
                                    </div>
                                    <div>
                                        <div class="cmd-syntax">!bind &lt;布吉岛用户名&gt;</div>
                                        <div class="cmd-desc">绑定后可直接用 <span class="highlight">!bw</span> 查询本人。</div>
                                    </div>
                                </div>
                                <div class="cmd-item">
                                    <div class="cmd-icon">
                                        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.3" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="18" height="18" rx="3"></rect><circle cx="8.5" cy="8.5" r="1.5"></circle><path d="M21 15l-5-5L5 21"></path></svg>
                                    </div>
                                    <div>
                                        <div class="cmd-syntax">!skin add &lt;正版ID&gt; / /skin up</div>
                                        <div class="cmd-desc">add: 从API绑定头像；up: 管理员上传MC皮肤源文件并自动提取头像。</div>
                                    </div>
                                </div>
                                <div class="cmd-item">
                                    <div class="cmd-icon">
                                        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.3" stroke-linecap="round" stroke-linejoin="round"><path d="M3 5h18v14H3z"></path><path d="M3 9l4-3 3 2 3-2 4 3"></path></svg>
                                    </div>
                                    <div>
                                        <div class="cmd-syntax">!bg / !bg set / !bg icon / !bg id / !bg cl</div>
                                        <div class="cmd-desc">背景上传与样式参数；set/icon/id/cl 为管理员参数。</div>
                                    </div>
                                </div>
                                <div class="cmd-item">
                                    <div class="cmd-icon">
                                        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.3" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3l3 6 6 .8-4.5 4.3 1 6-5.5-3-5.5 3 1-6L3 9.8 9 9z"></path></svg>
                                    </div>
                                    <div>
                                        <div class="cmd-syntax">/ch add &lt;称号&gt; [颜色代码] <span class="badge-new">NEW</span></div>
                                        <div class="cmd-desc">颜色可不填，默认 <span class="highlight">478978</span>；管理员直通，普通用户进审核群后由管理员发送“同意”通过（回复申请消息可精准通过对应申请）。</div>
                                    </div>
                                </div>
                            </div>
                        </div>

                        <div class="category">
                            <div class="cat-title">
                                <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.3" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="3"></circle><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 0 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 0 1-4 0v-.1a1.7 1.7 0 0 0-1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 0 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 0 1 0-4h.1a1.7 1.7 0 0 0 1.5-1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 0 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3h.1a1.7 1.7 0 0 0 1-1.5V3a2 2 0 0 1 4 0v.1a1.7 1.7 0 0 0 1 1.5h.1a1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 0 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8v.1a1.7 1.7 0 0 0 1.5 1H21a2 2 0 0 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z"></path></svg>
                                其他
                            </div>
                            <div class="cmd-list">
                                <div class="cmd-item">
                                    <div class="cmd-icon">
                                        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.3" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"></circle><path d="M9.1 9a3 3 0 1 1 5.8 1c-.6 1.2-1.9 1.5-2.5 2.2-.2.2-.4.6-.4 1.1"></path><path d="M12 17h.01"></path></svg>
                                    </div>
                                    <div>
                                        <div class="cmd-syntax">!help / 帮助</div>
                                        <div class="cmd-desc">发送当前帮助菜单图片。</div>
                                    </div>
                                </div>
                                <div class="cmd-item">
                                    <div class="cmd-icon">
                                        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.3" stroke-linecap="round" stroke-linejoin="round"><path d="M22 12h-4l-3 7-4-14-3 7H2"></path></svg>
                                    </div>
                                    <div>
                                        <div class="cmd-syntax">/群发 /群发编辑</div>
                                        <div class="cmd-desc">NapCat 模式可用，用于群发文案与编辑内容。</div>
                                    </div>
                                </div>
                            </div>
                        </div>
                    </div>
                </div>

                <div class="footer">
                    提示：群内使用指令时请先 @机器人；模式参数与称号参数已同步到本帮助页。
                </div>
            </div>
        </body>
        </html>
        """;
    }

    private static string? ResolveEdgeExecutablePath()
    {
        var envPath = Environment.GetEnvironmentVariable("EDGE_PATH");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
        {
            return envPath;
        }

        foreach (var path in EdgeCandidatePaths)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }
}
