using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using CUE4Parse.Compression;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Unreal;
using SCSKiller.Core.Vendors;

const string Usage = """
    usage: scskiller <command>
      scan [--rescan]                             扫描游戏并显示状态（--rescan：重新检测引擎）
      add <exe> [<安装目录>]                      添加平台未收录的游戏，指定它的 exe（带目录：录制器可以放进去）
      status [游戏]                               列出所有游戏，或显示某个游戏的详情
      compile <游戏|--all-ready> [--threads N] [--idle | --when-idle] [--careful | --fast]
                                                  --idle：后台优先级；--when-idle：并且只在没人用电脑时进行；
                                                  --careful（AMD）：本次及之后编译该游戏时，分批在少量线程上编译它录制到的
                                                  管线，让驱动保留更多结果（更慢）；--fast：恢复为单趟编译
      queue [命令]                                编译队列的调试外壳：先给命令，再从标准输入逐行给参数：
                                                  list | add <游戏> | move <游戏> <位置> | remove <游戏> | start | stop | wait | quit
      rewarm-stale [--if-driver-changed]          计划任务运行的内容；遵循驱动更新设置
      cache get | cache set <GB|unlimited|default>
      cache clear <游戏> [--game-precache]        删除该游戏的着色器缓存：驱动、Windows（D3DSCache）（会先确认）；
                                                  --game-precache：连游戏自己写的缓存一起删（*.ushaderprecache、
                                                  Dawn 的 bin\pipelineLibrary.cache、RE Engine 的 shader.cache2 与
                                                  piplinelist.bin、GTA V Enhanced 和 HELLDIVERS 2 的管线库），
                                                  游戏下次启动时会重建
      nvidia-snapshot                             只读：全部全局 DRS 设置、Auto Shader Compilation 及其空闲任务
      nvidia-auto-shader off|low|medium|high      切换 NVIDIA 的 Auto Shader Compilation（需要管理员，会先确认）
        （cache set 与 nvidia-auto-shader 和 App 一样以提权方式运行：--yes 跳过确认，--result <文件>
          把结果写成一行 JSON，--for-user <SID> 拒绝为其他账号提权）
      record install|uninstall <游戏>
      record clear <游戏>                         删除该游戏的录制数据（录制器的数据文件与本机保存的副本；会先确认）
      record alongside <游戏> on|off              允许录制器串接到某个 mod 的 d3d12.dll（改名接入，移除时还原）
      record anti-cheat <游戏> on|off             录制反作弊正在运行的游戏（不开风险会话、不绑定 pid）：
                                                  on 同时开启录制器；off 再把它移除
      launch-args <游戏> [<参数> | --clear]       SCSKiller 自己启动游戏 exe 时传入的参数（“游玩时录制”）：手动添加的
                                                  游戏可能需要它启动器的参数。不带参数：显示当前值。--clear：清空，只启 exe
      key <游戏> <hex>                            设置加密游戏的 AES 密钥（会先校验，只保存在本机）
      index <游戏> --out <目录>                   调试：导出引擎读取器解析出的着色器索引
      rehydrate <游戏> <hash-only.db> --out <db> [--expect <内容哈希>]
                                                  从本机安装补上 hash-only 录制所引用的着色器字节
      reference export <游戏> [--out <文件>]      导出该游戏版本的着色器清单，供社区服务的 `admin reference import` 使用：
                                                  索引里的着色器，以及本机录制见过的那些（默认 <游戏 id>.reference.json）
      task register|unregister                    登录/空闲时运行的“驱动更新后需要重新预编译”任务
      fetch-codecs <目录>                         打包用：把固定版本的 Oodle/zlib DLL 下载到 <目录>（缺了才下载）
      --version                                   命令行的版本
    <游戏> 可以是 id（steam:2909400），也可以是游戏名的一部分（不区分大小写）。
    """;

Console.OutputEncoding = System.Text.Encoding.UTF8;  // game names carry ™ and ®
RouteFailover.Product = "SCSKiller-CLI";
if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Console.WriteLine(Usage); return 0; }
// before anything reads the data folder: a portable copy's first read copies the installed one's
if (args[0] is "-v" or "--version" or "version") { Console.WriteLine(AppVersion.Current); return 0; }
// The app is handing over to Update.exe, which replaces this folder: start nothing (the scheduled task runs again later).
// Packaging never reads the app's data folder.
if (args[0] != "fetch-codecs" && Busy.Applying(AppStore.DefaultDir, DateTimeOffset.UtcNow))
{
    const string busy = "正在安装更新，请稍后重试";
    Console.Error.WriteLine(busy);
    // run elevated by the app, the exit code alone (0, for the scheduled task) would read as done
    try { if (Opt(Elevated.ResultArg) is { } file) Elevated.WriteResult(file, false, busy); }
    catch (ArgumentException) { }
    return 0;
}
int code;
try
{
    code = args[0] switch
    {
        "scan" => await Scan(args.Contains("--rescan")),
        "status" => await Status(args.Length > 1 ? args[1] : null),
        "add" => await AddGame(),
        "compile" => await Compile(),
        "queue" => await QueueShell(),
        "rewarm-stale" => await RewarmStale(args.Contains("--if-driver-changed")),
        "cache" => args.ElementAtOrDefault(1) == "clear" ? await CacheClear() : Cache(),
        "record" => await Record(),
        "launch-args" => await LaunchArgsCmd(),
        "index" => await Index(),
        "rehydrate" => await RehydrateCommand(),
        "reference" when args.ElementAtOrDefault(1) == "export" => await ReferenceExport(),
        "key" => await Key(),
        "task" => TaskCommand(),
        "proton-env" => await ProtonEnv(),
        "nvidia-snapshot" => NvidiaSnapshot(),
        "nvidia-auto-shader" => NvidiaAutoShader(),
        "fetch-codecs" => FetchCodecs(args.ElementAtOrDefault(1) ?? throw new ArgumentException("用法：scskiller fetch-codecs <dir>")),
        _ => Fail($"未知命令“{args[0]}”\n{Usage}"),
    };
}
catch (Exception e)
{
    code = Fail(e is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException ? e.Message : e.ToString());
}
// Run elevated by the app (Elevated.Run): runas can't redirect stdout, so the outcome goes to a file.
string? resultFile = null;
try { resultFile = Opt(Elevated.ResultArg); }
catch (ArgumentException e) { code = Fail(e.Message); }
if (resultFile != null) Elevated.WriteResult(resultFile, code == 0, Outcome.Message ?? "");
return code;

static int Fail(string message)
{
    Console.Error.WriteLine("错误：" + message);
    Outcome.Message = message;
    return 1;
}

/// <summary>False (reported as an error) unless the answer is y.</summary>
static bool Confirm(string question)
{
    Console.Write(question + " [y/N] ");
    var piped = Console.IsInputRedirected;
    var answer = piped ? Elevated.ReadAnswer(Console.OpenStandardInput()) : Console.ReadLine()?.Trim();
    if (piped) Console.WriteLine(answer);
    if (string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)) return true;
    Fail(answer == null ? "已取消：标准输入没有回答（可传入 y 确认）" : $"已取消：输入的是“{answer}”，不是 y");
    return false;
}

static async Task<ScsKiller> Open(bool rescan = false)
{
    var k = ScsKiller.CreateDefault();
    k.Log = new StderrLog();   // synchronous: a message logged just before exit still prints
    var account = new Account(AppStore.DefaultDir);
    k.DbEntitled = () => account.SignedIn;   // entitlements are never read here: signed in counts as entitled
    await (rescan ? k.RescanAsync(CancellationToken.None) : k.ScanAsync(CancellationToken.None));
    return k;
}

static void BackgroundPriority()   // index/plan/materialize run in this process; the warm itself gets --priority idle
{
    using var self = Process.GetCurrentProcess();
    self.PriorityClass = ProcessPriorityClass.Idle;
}

static string StatusName(GameState s) => ScsKiller.NeedsOfflineSession(s) ? "NeedsOfflineSession" : s.Status.ToString();

static string Hms(TimeSpan? t) => t?.ToString(@"h\:mm\:ss") ?? "-";

static void Row(GameState s) =>
    Console.WriteLine($"{StatusName(s),-19} {s.Game.Id,-24} {s.Game.Name}{(s.AntiCheat != AntiCheat.None ? $"  [{s.AntiCheat}]" : "")}{(s.KnownStutter is { } ks ? $"  [known stutter: {ks.Severity.ToString().ToLowerInvariant()}]" : "")}{(s.NoStutter != null ? "  [no shader stutter]" : "")}{(s.RecorderInstalled ? "  [recorder]" : "")}{(s.CacheOnDisk is { } c ? $"  [cache {Format.Bytes(c)}]" : "")}  - {(ScsKiller.NeverRecorded(s) ? $"{ScsKiller.NothingRecordedTitle}. {s.RecorderRefused}" : s.StatusReason)}");

/// <summary>The tags' <see cref="Format.Middleware"/> lines joined with " · ", with the DLL names when <paramref name="dlls"/>.</summary>
static string MiddlewareLine(IReadOnlyList<MiddlewareTag> tags, bool dlls) => string.Join(" · ", tags.Select(t =>
    Format.Middleware(t) + (dlls ? $" [{string.Join(", ", t.Dlls)}]" : "")));

static GameState Match(IReadOnlyList<GameState> games, string query)
{
    var hit = games.FirstOrDefault(s => s.Game.Id.Equals(query, StringComparison.OrdinalIgnoreCase))
              ?? games.FirstOrDefault(s => s.Game.Name.Equals(query, StringComparison.OrdinalIgnoreCase));
    if (hit != null) return hit;
    var many = games.Where(s => s.Game.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    return many.Count switch
    {
        1 => many[0],
        0 => throw new ArgumentException($"没有匹配“{query}”的游戏"),
        _ => throw new ArgumentException($"“{query}”匹配到多个游戏：{string.Join("、", many.Select(s => $"{s.Game.Name}（{s.Game.Id}）"))}"),
    };
}

// --adapter-luid: the adapter the app that asked runs on, which a pick since then (Settings.Gpu) only changes at its restart
IGpuVendorBackend RunningGpu()
{
    if (Opt(ScsKiller.AdapterArg) is not { } hex) return ScsKiller.DetectGpu();
    return long.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var luid)
        && GpuBackends.Adapters().FirstOrDefault(a => a.Gpu.AdapterLuid == luid) is { } running
        ? ScsKiller.DetectGpu(GpuBackends.ChoiceOf(running))
        : throw new InvalidOperationException("SCSKiller 所用的显卡已不在列表中（驱动已重新加载）：请重启 SCSKiller 后重试");
}

string? Opt(string name)
{
    int i = Array.IndexOf(args, name);
    if (i < 0) return null;
    return i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : throw new ArgumentException($"{name} needs a value");
}

/// <summary>Refuses an option the command doesn't take (a typo would otherwise run with the default).</summary>
void Only(params string[] known)
{
    if (args.Skip(1).FirstOrDefault(a => a.StartsWith("--") && !known.Contains(a)) is { } unknown) throw new ArgumentException($"未知选项“{unknown}”");
}

async Task<int> Scan(bool rescan)
{
    var k = await Open(rescan);
    Console.WriteLine($"显卡：{k.Vendor.Gpu.Name}，驱动 {k.Vendor.Gpu.DriverVersion}（{k.Vendor.Caps.Profile}）");
    foreach (var s in k.Games) Row(s);
    return 0;
}

async Task<int> Status(string? query)
{
    var k = await Open();
    if (query == null)
    {
        foreach (var s in k.Games)
        {
            Row(s);
            if (s.Middleware is { Count: > 0 } m) Console.WriteLine($"{"",15}中间件：{MiddlewareLine(m, false)}");
        }
        return 0;
    }
    var g = Match(k.Games, query);
    var rec = k.Store.LoadGame(g.Game.Id);
    Console.WriteLine($"""
        {g.Game.Name} ({g.Game.Id}, {g.Game.Store})
          install    {g.Game.InstallDir}
          exe        {g.Game.ExePath}
          runs as    {(rec.LaunchedExeName is { } l0 ? $"{l0} (seen {rec.LaunchedExeSeenAt:yyyy-MM-dd HH:mm})" : "尚未出现")}; warms stage {ScsKiller.WarmExeName(g.Game, rec)}
          engine     {(g.Engine is { } e ? string.Join(' ', new[] { Format.Engine(e) + (e.VersionGuessed ? "（推测）" : ""), e.Fork, e.GraphicsApi }.OfType<string>()) + (e.Encrypted ? " encrypted" : "") : "-")}
          status     {StatusName(g)}: {g.StatusReason}{(ScsKiller.DeliveryLine(g) is { } asd ? $"\n             {asd}" : "")}
          anti-cheat {g.AntiCheat}
          shaders    {g.ShaderCount?.ToString() ?? "-"}
          plan       {(g.Plan is { } p ? $"{p.Recorded} 条录制 + {p.Generated} 条生成 + {p.D3D11Shaders} 条 DirectX 11 着色器{(p.D3D11Draws > 0 ? $"（其中 {p.D3D11Draws} 个顶点着色器有录制到的布局）" : "")} + {p.MiddlewareItems} 个中间件，{p.RootSignatures} 个根签名，规则已验证：{p.RootSigRuleVerified}" : "-")}
          stutter    {(g.KnownStutter is { } st ? $"已知，{st.Severity.ToString().ToLowerInvariant()}：{st.Reason}（{st.Source}，检查于 {st.Date}）" : g.NoStutter is { } ns ? $"无：{ns}" : "-")}
          middleware {(g.Middleware is { Count: > 0 } mw ? MiddlewareLine(mw, true) : "-")}
          estimate   {(g.EstimatedCacheBytes is { } est ? Format.Bytes(est) : "-")} of cache, {Hms(g.EstimatedWarmTime)}
          careful    {CarefulLine(g.Careful, g.Plan)}
          warmed     {(g.WarmedAt is { } w ? $"{w:yyyy-MM-dd HH:mm} for driver {g.WarmedDriverVersion} in {Hms(g.LastWarmTime)}" + (ScsKiller.WarmCounts(g.LastWarmFailed ?? 0, g.LastWarmSkipped ?? 0, g.LastWarmCrashed ?? 0, g.LastWarmDriverSkipped ?? 0, g.LastWarmUnbuilt ?? 0) is { } counts ? $"; {counts}" : "") : "never")}
          cache      {(g.CacheOnDisk is { } c ? $"{Format.Bytes(c)} on disk (driver-cache files its warms or the game had open)" : "-")}{(g.WindowsCache is { } wc ? $"，其中 {Format.Bytes(wc)} 在其 D3DSCache 目录" : "")}
          keys       {(rec.CacheKeys.Count > 0 ? string.Join(", ", rec.CacheKeys.Order()) : "-")}{(k.WarmAgs(g.Game.Id) is { } ags ? $"（编译时会登记其 AGS 应用名 {ags.App}，键 {k.AgsKey(g.Game.Id)}：exe 名大小写无关）" : AmdAppCache.IsNameHashed(rec.CacheKeys, ScsKiller.WarmExeName(g.Game, rec)) == false ? " (an app profile's key, not the exe name's hash: the name's case doesn't matter)" : "")}
          recorder   {(g.RecorderInstalled ? "installed" : "未安装")}{(g.RecorderRefused is { } why ? $"; {(ScsKiller.NeverRecorded(g) ? ScsKiller.NothingRecordedTitle + ". " : "")}{why}" : "")}
          recording  {(g.RecordingBytes > 0 ? $"{(g.RecordingBytes < 1 << 20 ? $"{Math.Max(1, g.RecordingBytes >> 10)} KB" : Format.Bytes(g.RecordingBytes))} (the game folder's files and SCSKiller's copy)" : "-")}, limit {ScsKiller.LimitText(k.Settings.RecordingLimitMB)} per game{(g.RecordingPaused ? $"; {ScsKiller.PausedNote(k.Settings)}" : "")}
          last play  {(g.LastSession is { } l ? $"{Hms(l.Duration)}, {l.Requests} pipelines: {l.FromGameLibrary} from the game's library, {l.CacheHits} cache hits, {l.Compiles} compiles (worst {l.WorstCompileMs:0.0} ms){(l.StartupCompiles > 0 ? $", {l.StartupCompiles} compiles while the game started or loaded" : "")}{(l.RayQueryRecompiles > 0 ? $", {l.RayQueryRecompiles} ray-traced pipelines the driver partly recompiles every launch" : "")}{(l.StateObjectsReady + l.StateObjectsCompiled + l.StateObjectsStartupCompiled > 0 ? $"; ray tracing state objects: {l.StateObjectsReady} ready, {l.StateObjectsCompiled} compiled{(l.StateObjectsStartupCompiled > 0 ? $", {l.StateObjectsStartupCompiled} compiled while the game started or loaded" : "")}" : "")}" : "-")}
          1st launch {(rec.FirstLaunch is { } fl ? $"{fl.At.LocalDateTime:yyyy-MM-dd HH:mm}, after the last compile: {fl.Hits} cache hits, {fl.Compiles} compiles ({fl.Compiled * 100:0.0}% compiled)" : "-")}
        """);
    return 0;
}

static string CarefulLine(CarefulCompile? c, PlanStats? plan) => c is not { } x ? "-（仅 AMD）"
    : (x.On ? $"开启：录制的管线最多用 {ScsKiller.AmdCarefulThreads} 个线程分批编译，其余保持全速" : "关闭（使用 compile <game> --careful 开启）")
      + (x.Recorded == 0 && plan is { D3D11Shaders: > 0, Recorded: 0 } ? "；仅对 DirectX 12 有效：此游戏的 DirectX 11 着色器始终全速编译"
         : x.Recorded == 0 ? "；没有已录制的 DirectX 12 管线，因此不做谨慎编译"
         : x.Estimate is { } t ? $"；预计耗时约 {ScsKiller.Duration(t)}" : "");

static void PrintQueueChanges(ScsKiller k)
{
    var lastPrint = DateTime.MinValue;
    var lastStage = new Dictionary<string, QueueStage>();
    k.QueueChanged += q =>
    {
        lock (lastStage)
        {
            bool stageChanged = !lastStage.TryGetValue(q.GameId, out var st) || st != q.Stage;
            if (!stageChanged && DateTime.Now - lastPrint < TimeSpan.FromSeconds(2)) return;
            lastStage[q.GameId] = q.Stage;
            lastPrint = DateTime.Now;
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} {q.GameId} {q.Stage}{ScsKiller.ProgressText(q)}{(q.Error != null ? " - " + q.Error : "")}");
        }
    };
}

async Task<int> RunQueue(ScsKiller k, IReadOnlyList<GameState> targets, bool whenIdle = false)
{
    if (targets.Count == 0) { Console.WriteLine("没有需要编译的游戏"); return 0; }
    PrintQueueChanges(k);
    bool cancelled = false;
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cancelled = true;
        Console.WriteLine("正在停止；正在跑的编译会跑完，以便驱动写入缓存…");
        foreach (var q in k.Queue.Where(q => q.Stage == QueueStage.Waiting)) k.Remove(q.GameId);
        k.StopQueue();
    };
    foreach (var t in targets)
        if (whenIdle) k.EnqueueWhenIdle(t.Game.Id); else k.Enqueue(t.Game.Id);
    if (whenIdle) Console.WriteLine($"等待电脑空闲 {k.IdleAfter.TotalMinutes:0} 分钟；有输入时暂停");
    else k.StartQueue();
    await k.WhenQueueIdle();
    return !cancelled && k.Queue.Count > 0 && k.Queue.All(q => q.Stage == QueueStage.Done) ? 0 : 1;   // Ctrl+C: not everything compiled
}

// A debugging shell: the queue lives in this process, so commands come on stdin (or piped: echo add ff7 | scskiller queue).
async Task<int> QueueShell()
{
    var k = await Open();
    PrintQueueChanges(k);
    string Id(string[] w, int from, int to) => Match(k.Games, string.Join(' ', w[from..to])).Game.Id;
    var lines = (args.Length > 1 ? [string.Join(' ', args[1..])] : Array.Empty<string>()).Concat(StdinLines());
    foreach (var line in lines)
    {
        var w = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        try
        {
            switch (w.ElementAtOrDefault(0))
            {
                case null: break;
                case "list":
                    var all = k.Queue;
                    var queue = all.Where(q => !q.PlanCheck).ToList();
                    for (int i = 0; i < queue.Count; i++)
                        Console.WriteLine($"{i,3} {queue[i].Stage,-13} {queue[i].GameId}{(queue[i].Note is { } n ? "  (" + n + ")" : "")}");
                    Console.WriteLine(k.QueueRunning ? "队列运行中" : "queue not running");
                    if (ScsKiller.PlanCheckLine(all) is { } checks) Console.WriteLine(checks);
                    break;
                case "add" when w.Length > 1: k.Enqueue(Id(w, 1, w.Length)); break;
                case "remove" when w.Length > 1: k.Remove(Id(w, 1, w.Length)); break;
                case "move" when w.Length > 2 && int.TryParse(w[^1], out var to): k.MoveInQueue(Id(w, 1, w.Length - 1), to); break;
                case "start": k.StartQueue(); break;
                case "stop": k.StopQueue(); break;
                case "wait": await k.WhenQueueIdle(); break;
                case "quit": k.StopQueue(); await k.WhenQueueIdle(); return 0;   // graceful: the driver writes its cache
                default: Console.WriteLine("可用命令：list | add <game> | move <game> <index> | remove <game> | start | stop | wait | quit"); break;
            }
        }
        catch (ArgumentException e) { Console.WriteLine("错误：" + e.Message); }
    }
    await k.WhenQueueIdle();   // end of input: let a started queue finish
    return 0;

    static IEnumerable<string> StdinLines()
    {
        while (Console.ReadLine() is { } l) yield return l;
    }
}

async Task<int> CacheClear()
{
    if (args.Length < 3) return Fail("cache clear <game>");
    var k = await Open();
    var g = Match(k.Games, args[2]);
    var precache = args.Contains("--game-precache");
    var parts = k.GameCaches(g.Game.Id, precache);
    var gap = k.DriverCacheGap(g.Game.Id);
    if (parts.Count == 0) return Fail($"未找到 {g.Game.Name} 的着色器缓存文件" + (gap != null ? $" ({gap})" : ""));
    foreach (var p in parts)
    {
        Console.WriteLine($"{p.Name}  {Format.Bytes(p.Bytes)}");
        foreach (var f in p.Files) Console.WriteLine($"  {f}");
    }
    if (gap != null) Console.WriteLine($"提示：{gap}");
    if (!precache && k.GameCaches(g.Game.Id, true).Where(p => p.Name is ScsKiller.PipelinePart or ScsKiller.PrecachePart).ToList() is { Count: > 0 } own)
        Console.WriteLine($"已保留：游戏自身的缓存（{string.Join("、", own.Select(p => $"{p.Name} {Format.Bytes(p.Bytes)}"))}）；--game-precache 会一并删除");
    if (!Confirm($"删除 {g.Game.Name} 的着色器缓存（{string.Join("、", parts.Select(p => $"{p.Name} {Format.Bytes(p.Bytes)}"))}，即上面这些文件）？" +
                 "下次运行将是冷启动，之后会显示为未预编译。")) return 1;
    k.ClearGameCache(g.Game.Id, precache);   // a refusal throws InvalidOperationException: printed as "error: <reason>"
    Console.WriteLine($"已清理 {g.Game.Name} 的着色器缓存");
    return 0;
}

async Task<int> Compile()
{
    if (args.Length < 2) return Fail("compile <game|--all-ready> [--threads N] [--idle | --when-idle] [--careful | --fast]");
    Only("--all-ready", "--threads", "--idle", "--when-idle", "--careful", "--fast");
    int? threads = Opt("--threads") is { } t ? int.TryParse(t, out var n) && n > 0 ? n : throw new ArgumentException("--threads 需要正整数线程数") : null;
    var k = await Open();
    if (threads != null) k.ThreadsOverride = threads;
    bool whenIdle = args.Contains("--when-idle");
    k.Background = args.Contains("--idle");
    if (k.Background || whenIdle) BackgroundPriority();
    var targets = args[1] == "--all-ready"
        ? k.Games.Where(s => s.Status is GameStatus.Ready or GameStatus.Stale && s.NoStutter == null && !s.CompileUnreached).ToList()
        : [Match(k.Games, args[1])];
    if (targets is [{ StatusReason: ScsKiller.CantReachReason } refused]) return Fail($"{refused.Game.Name}: {refused.StatusReason}");
    if (args.Contains("--careful") || args.Contains("--fast"))
        foreach (var g in targets)
        {
            k.SetCarefulCompile(g.Game.Id, args.Contains("--careful"));
            Console.WriteLine($"{g.Game.Name}: {(k.Games.Single(s => s.Game.Id == g.Game.Id) is var gs ? CarefulLine(gs.Careful, gs.Plan) : "")}");
        }
    return await RunQueue(k, targets, whenIdle);
}

async Task<int> RewarmStale(bool ifDriverChanged)
{
    var k = await Open();
    var stale = ifDriverChanged ? k.DriverStaleGames() : k.StaleGames();
    if (stale.Count == 0) { Console.WriteLine("没有需要重新预热的游戏"); return 0; }
    switch (k.Settings.OnDriverUpdate)
    {
        case DriverUpdateMode.Off:
            Console.WriteLine($"{stale.Count} game(s) need a re-warm; automatic re-warm is off.");
            return 0;
        case DriverUpdateMode.Ask:
            if (!k.ShouldNotifyStale()) { Console.WriteLine($"{stale.Count} game(s) need a re-warm; the user skipped them for this driver."); return 0; }
            if (ScheduledTask.AppExe() is { } app)   // the app shows the notification (compile now / when idle / skip)
            {
                Process.Start(new ProcessStartInfo(app, ScheduledTask.DriverUpdatedArg) { UseShellExecute = false })?.Dispose();
                Console.WriteLine($"{stale.Count} game(s) need a re-warm: notified via {app}");
                return 0;
            }
            Console.WriteLine($"{stale.Count} game(s) need a re-warm (driver {k.Vendor.Gpu.DriverVersion}):");
            foreach (var s in stale) Row(s);
            Console.WriteLine("运行 scskiller compile <game> 或打开 SCSKiller 重新预热。");
            return 0;
        default:
            BackgroundPriority();
            return await RunQueue(k, ifDriverChanged ? k.IdleRewarmGames() : stale, whenIdle: true);
    }
}

int Cache()
{
    var v = RunningGpu();
    Console.WriteLine($"显卡：{v.Gpu.Name}（{v.Vendor}），驱动 {v.Gpu.DriverVersion}，配置 {v.Caps.Profile}");
    if (args.Length > 1 && args[1] == "set")
    {
        if (args.Length < 3) return Fail("cache set <GB|unlimited|default>");
        if (!v.Caps.CacheSizeConfigurable) return Fail("此显卡的缓存上限不可调整");
        var limit = args[2].ToLowerInvariant() switch
        {
            "default" => new CacheLimit(null, true),
            "unlimited" => new CacheLimit(null, false),
            var n when double.TryParse(n, System.Globalization.CultureInfo.InvariantCulture, out var gb) && gb > 0 => new CacheLimit((long)(gb * 1073741824), false),
            _ => throw new ArgumentException("容量必须是 GB 数值、unlimited 或 default"),
        };
        if (!Elevated.IsAdmin) return Fail("调整驱动着色器缓存上限需要管理员权限");
        var what = limit.IsDriverDefault ? "驱动默认值" : limit.Bytes == null ? "unlimited" : Format.Bytes(limit.Bytes);
        if (!args.Contains(Elevated.YesArg) && !Confirm($"将 NVIDIA 全局着色器缓存上限设为 {what}？此驱动设置会影响所有游戏。"))
            return 1;
        v.SetCacheLimit(limit);
        Outcome.Message = $"着色器缓存上限已设为 {what}。";
    }
    var usage = v.GetCacheUsage();
    if (v is AmdBackend)   // GetCacheUsage sums both folders
        Console.WriteLine($"缓存：磁盘占用 {(usage.UpperBound ? "<= " : "")}{Format.Bytes(usage.BytesOnDisk)} on disk = {AmdBackend.CacheDir} {Format.Bytes(AmdBackend.Bytes(AmdBackend.CacheDir))} (D3D12)" +
                          $" + {AmdBackend.D3D11CacheDir} {Format.Bytes(AmdBackend.Bytes(AmdBackend.D3D11CacheDir))} (D3D11)");
    else Console.WriteLine($"缓存：{usage.Path}，磁盘占用 {(usage.UpperBound ? "<= " : "")}{Format.Bytes(usage.BytesOnDisk)} on disk");
    var l =v.GetCacheLimit();
    Console.WriteLine($"limit: {(l == null ? "not readable" : l.Bytes == null ? "unlimited" : Format.Bytes(l.Bytes))}{(l?.IsDriverDefault == true ? "（驱动默认值）" : "")}");
    if (v is NvidiaBackend nv)
    {
        var (id, raw, def) = nv.ReadCacheSetting();
        Console.WriteLine($"       setting 0x{id:X8} '{NvidiaBackend.CacheSizeSettingName}' = {(raw is { } r ? $"0x{r:X8}" : "not set")}, driver default 0x{def:X8}");
    }
    return 0;
}

async Task<int> Record()
{
    bool alongside = args.Length > 3 && args[1] == "alongside" && args[3] is "on" or "off";
    bool direct = args.Length > 3 && args[1] == "anti-cheat" && args[3] is "on" or "off";
    if (!alongside && !direct && (args.Length < 3 || args[1] is not ("install" or "uninstall" or "clear")))
        return Fail("record install|uninstall|clear <game> | record alongside <game> on|off | record anti-cheat <game> on|off");
    var k = await Open();
    var g = Match(k.Games, args[2]);
    if (args[1] == "clear")
    {
        if (g.RecordingBytes == 0) return Fail($"{g.Game.Name} 还没有录制数据");
        if (!Confirm($"删除 {g.Game.Name} 的录制数据（{Format.Bytes(g.RecordingBytes)}）？已录到的管线会被移除；只有在开启录制后由游戏再次创建时才能重新获得，" +
                     "编译计划会退回到游戏文件可提供的内容。")) return 1;
        k.ClearRecording(g.Game.Id);   // a refusal throws InvalidOperationException: printed as "error: <reason>"
        Console.WriteLine($"已清理 {g.Game.Name} 的录制数据");
        return 0;
    }
    if (alongside)
    {
        var mod = k.RecorderMod(g.Game.Id);
        k.SetRecordAlongsideMod(g.Game.Id, args[3] == "on");
        var s = k.Games.Single(x => x.Game.Id == g.Game.Id);
        Console.WriteLine($"随 mod 一起录制（{mod ?? "某个 mod"}）：{args[3]}；录制器{(s.RecorderInstalled ? "已安装" : "未安装")}"
            + ((s.RecorderSkip == ScsKiller.SkipModNotChainable ? ScsKiller.NotChainableReason(s.RecorderMod) : s.RecorderSkip ?? s.RecorderNote) is { } why ? $"（{why}）" : ""));
        return 0;
    }
    if (direct)
    {
        if (args[3] == "on" && !g.AnyAntiCheat)
            return Fail($"{g.Game.Name} 目前没有检测到反作弊：用普通录制器即可 —— record install {args[2]}");
        k.SetAntiCheatRecording(g.Game.Id, args[3] == "on");
        var s = k.Games.Single(x => x.Game.Id == g.Game.Id);
        Console.WriteLine($"{g.Game.Name} 的反作弊录制：{args[3]}；反作弊 {g.AntiCheat}，录制器{(s.RecorderInstalled ? "已安装" : "未安装")}"
            + ((s.RecorderSkip ?? s.RecorderNote) is { } why ? $"（{why}）" : ""));
        if (args[3] == "on" && !k.RecorderAcceptsAntiCheat)
            Console.WriteLine("警告：此副本的 native\\d3d12.dll 是启用 allow_ac= 之前构建的，在带反作弊的游戏中会直接放行。请换成重新构建的版本（见 SCSKiller_NoACGate_1.3.0_移植.md）。");
        return 0;
    }
    if (args[1] == "install") k.InstallRecorder(g.Game.Id); else k.UninstallRecorder(g.Game.Id);
    Console.WriteLine($"录制器{(args[1] == "install" ? "已安装" : "已卸载")}：{Path.GetDirectoryName(g.Game.ExePath)}");
    return 0;
}

async Task<int> LaunchArgsCmd()
{
    if (args.Length < 2) return Fail("launch-args <game> [<arguments> | --clear]");
    var k = await Open();
    var g = Match(k.Games, args[1]);
    if (args.Length > 2) k.SetLaunchArgs(g.Game.Id, args[2] == "--clear" && args.Length == 3 ? null : string.Join(' ', args[2..].Select(Quoted)));
    // read back: the record is what a session starts the exe with
    Console.WriteLine($"{g.Game.Name}: started with {(k.Store.LoadGame(g.Game.Id).LaunchArgs is { Length: > 0 } now ? now : "no arguments")}");
    return 0;
}

// an argument the shell split on a space goes back in quotes: the exe's own parser would see two arguments otherwise
static string Quoted(string arg) => arg.Contains(' ') && !arg.Contains('"') ? $"\"{arg}\"" : arg;

async Task<int> AddGame()
{
    if (args.Length < 2) return Fail("add <exe> [<install folder>]");
    var k = await Open();
    var (game, existed) = k.AddManualGame(args[1], args.ElementAtOrDefault(2));
    await k.ScanAsync(CancellationToken.None);
    Console.WriteLine($"{(existed ? "already listed" : "added")}: {game.Id}  {game.Name}  {game.ExePath}");
    return 0;
}

async Task<int> ProtonEnv()
{
    if (args.Length < 2) return Fail("proton-env <game> [--warm]");
    if (!Wine.Running) return Fail("proton-env 用于 Wine（Proton）环境下的游戏");
    var k = await Open();
    var g = Match(k.Games, args[1]);
    var root = Directory.CreateDirectory(Path.Combine(k.Store.GameDir(g.Game.Id), "proton-cache"));
    foreach (var d in new[] { "nvidia", "vkd3d" }) root.CreateSubdirectory(d);   // NVIDIA's driver keeps no cache in a folder that doesn't exist
    if (Wine.UnixPath(root.FullName) is not { } unix) return Fail($"{root.FullName} 没有对应的 Linux 路径");
    var (warm, game) = Wine.CacheEnvironment(unix, Path.GetFileName(g.Game.ExePath));
    foreach (var (name, value) in args.Contains("--warm") ? warm : game) Console.WriteLine($"{name}={value}");
    return 0;
}

async Task<int> Key()
{
    if (args.Length < 3) return Fail("key <game> <hex>");
    var k = await Open();
    var g = Match(k.Games, args[1]);
    if (!k.SetEncryptionKey(g.Game.Id, args[2])) return Fail($"此密钥无法打开 {g.Game.Name} 的文件");
    await k.RescanAsync(CancellationToken.None);
    Console.WriteLine($"已为 {g.Game.Name} 接受密钥：{k.Games.First(s => s.Game.Id == g.Game.Id).StatusReason}");
    return 0;
}

static async Task<(ScsKiller K, GameState G, IEngineReader Reader, EngineInfo Engine, ShaderIndex Index)> OpenIndex(string query, IProgress<string>? log = null)
{
    var k = await Open();
    var g = Match(k.Games, query);
    var reader = ScsKiller.DefaultReaders();
    var engine = g.Engine ?? reader.Detect(g.Game) ?? throw new InvalidOperationException($"{g.Game.Name}: engine not supported");
    return (k, g, reader, engine, reader.Index(g.Game, engine, log, CancellationToken.None));
}

async Task<int> Index()
{
    if (args.Length < 2 || Opt("--out") is not { } outDir) return Fail("index <game> --out <dir>");
    var (_, _, _, engine, index) = await OpenIndex(args[1], new Progress<string>(Console.WriteLine));
    Directory.CreateDirectory(outDir);
    await File.WriteAllTextAsync(Path.Combine(outDir, "index.json"), JsonSerializer.Serialize(new { engine, index }, AppStore.Json));
    Console.WriteLine($"{index.Shaders.Count} shaders, {index.Maps.Count} shader maps, content hash {index.ContentHash} -> {outDir}");
    return 0;
}

async Task<int> RehydrateCommand()
{
    if (args.Length < 3 || Opt("--out") is not { } outDb) return Fail("rehydrate <game> <hash-only.db> --out <db> [--expect <content hash>]");
    var (_, g, reader, engine, index) = await OpenIndex(args[1]);
    Console.WriteLine($"索引内容哈希 {index.ContentHash}");
    var r = SCSKiller.Core.Planning.Rehydrate.Run(args[2], outDb, g.Game, engine, reader, index, Opt("--expect"),
        moreBlobs: h => SCSKiller.Core.Planning.Middleware.Blobs(g.Game, h));
    Console.WriteLine($"引用 {r.Referenced}、数据库中已有 {r.AlreadyPresent}、找到 {r.Found}（中间件 DLL 中 {r.FoundInMiddleware}）、缺失 {r.Missing.Count}" +
        (r.ContentHashMatches is { } m ? $", content hash {(m ? "matches" : "DIFFERS")}" : "") + $" -> {outDb} ({r.OutputBytes:N0} bytes)");
    foreach (var h in r.Missing) Console.WriteLine($"  缺失 {h}");
    return r.Complete ? 0 : 2;
}

async Task<int> ReferenceExport()
{
    if (args.Length < 3) return Fail("reference export <game> [--out <file>]");
    var (k, g, _, _, index) = await OpenIndex(args[2]);
    // middleware and runtime-built shaders aren't in the index: the recording has their bytes
    var recording = Path.Combine(k.Store.GameDir(g.Game.Id), "recording.db");
    var seen = File.Exists(recording)
        ? SCSKiller.Core.Planning.PsoDb.Read(recording).Where(r => r.Tag == 'B' && r.Payload.Length > 20 && !SCSKiller.Core.Carved.Dxbc.IsRootSignatureOnly(r.Payload.AsSpan(20)))
            .Select(r => Convert.ToHexStringLower(r.Payload.AsSpan(0, 20)))
        : [];
    var shaders = index.Shaders.Keys.Concat(seen).Distinct().Order(StringComparer.Ordinal).ToArray();
    var outFile = Opt("--out") ?? g.Game.Id.Replace(':', '_') + ".reference.json";
    await File.WriteAllTextAsync(outFile, JsonSerializer.Serialize(new
    {
        content_hash = index.ContentHash,
        store_build_key = g.Game.Version is { } v ? $"{g.Game.Id}@{v}" : null,
        shaders,
    }));
    Console.WriteLine($"{shaders.Length} shaders ({index.Shaders.Count} indexed), content hash {index.ContentHash} -> {outFile}");
    return 0;
}

// Read-only. Run it before and after switching something in the NVIDIA App and diff the two outputs.
static int NvidiaSnapshot()
{
    if (ScsKiller.DetectGpu() is not NvidiaBackend nv) return Fail("未检测到 NVIDIA 显卡");
    Console.WriteLine($"驱动 {nv.Gpu.DriverVersion}");
    Console.WriteLine($"自动着色器编译：{nv.GetAutoShaderCompilation()?.ToString() ?? "无法读取"}（设置 0x{NvidiaBackend.AutoShaderCompilationId:X8}）");
    Console.WriteLine($"NvOSC.exe：{NvidiaBackend.NvOscPath() ?? "未找到"}");
    Console.WriteLine("基础配置：");
    foreach (var (id, name, type, value) in NvidiaBackend.BaseProfileSettings())
        Console.WriteLine($"  0x{id:X8} 类型 {type} = 0x{value:X8}  {name}");
    foreach (var task in new[] { $@"\Users\{Environment.UserName}{NvidiaBackend.AutoShaderTaskName}", NvidiaBackend.AutoShaderTaskName })
    {
        var psi = new ProcessStartInfo("schtasks.exe", ["/Query", "/TN", task, "/V", "/FO", "LIST"]) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        Console.WriteLine($"任务 {task}：" + (p.ExitCode == 0 ? output.Trim() : "未注册"));
    }
    Console.WriteLine($"NvOSC.exe 正在运行：{Process.GetProcessesByName("NvOSC").Length > 0}");
    return 0;
}

// What the Settings toggle does, for an elevated prompt: the driver setting + NvOSC.exe -register/-deregister.
int NvidiaAutoShader()
{
    if (!Enum.TryParse<AutoShaderCompilation>(args.ElementAtOrDefault(1), true, out var level) || !Enum.IsDefined(level) || int.TryParse(args[1], out _))
        return Fail("nvidia-auto-shader off|low|medium|high");
    if (RunningGpu() is not NvidiaBackend nv) return Fail("未检测到 NVIDIA 显卡");
    if (!Elevated.IsAdmin) return Fail("调整 NVIDIA 自动着色器编译需要管理员权限");
    // NvOSC.exe -register registers the idle task for the account running it: refuse when UAC elevated a different
    // administrator (over-the-shoulder), whose DXCache it would compile into. Same account + elevated token is fine.
    if (Opt(Elevated.ForUserArg) is { } sid && sid != Elevated.CurrentUserSid)
        return Fail($"Windows 以其他账号提权运行（{WindowsIdentity.GetCurrent().Name}），此 NVIDIA 计划任务会为该账号编译，" +
                    "而不是为你。请用管理员账号登录 Windows，或请管理员把你的账号加入 Administrators 组。");
    if (!args.Contains(Elevated.YesArg) && !Confirm($"将 NVIDIA 自动着色器编译设为 {level}？这会修改全局驱动设置及空闲任务。"))
        return 1;
    Outcome.Message = nv.SetAutoShaderCompilation(level);
    Console.WriteLine($"{Outcome.Message} now: {nv.GetAutoShaderCompilation()?.ToString() ?? "not readable"}");
    return 0;
}

int TaskCommand()
{
    switch (args.ElementAtOrDefault(1))
    {
        case "register":
            ScheduledTask.Register(ScheduledTask.TaskExe() ?? Environment.ProcessPath!);
            Console.WriteLine($"已注册任务 {ScheduledTask.Name}");
            return 0;
        case "unregister":
            ScheduledTask.Unregister();
            Console.WriteLine($"已移除任务 {ScheduledTask.Name}");
            return 0;
        default:
            return Fail("task register|unregister");
    }
}

static int FetchCodecs(string dir)
{
    // Packaging (publish.ps1): the pinned DLLs in the build's cache, copied next to the exes, where they seed each user's
    // codecs folder (Codecs)
    try
    {
        Console.WriteLine(Codecs.Ensure(OodleHelper.OodleFileName, Codecs.DownloadOodle, dir));
        Console.WriteLine(Codecs.Ensure(ZlibHelper.DllName, Codecs.DownloadZlib, dir));
        return 0;
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return Fail(e.Message); }
}

/// <summary>What the command's last word was (its success line or the error), for <see cref="Elevated.ResultArg"/>.</summary>
static class Outcome { public static string? Message; }

// Not Progress<T>: it posts to the thread pool, so its last messages could be lost when the process exits.
sealed class StderrLog : IProgress<string>
{
    public void Report(string value) => Console.Error.WriteLine("  " + value);
}
