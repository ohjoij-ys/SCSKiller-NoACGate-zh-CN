using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;
using SCSKiller.Core.Vendors;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using Windows.UI;

namespace SCSKiller.App;

// View models are computed views over IScsKiller: Refresh() re-reads the facade and tells x:Bind to update everything.
public abstract class Bindable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed() => PropertyChanged?.Invoke(this, new(string.Empty));
    protected void Changed(params string[] names) { foreach (var n in names) PropertyChanged?.Invoke(this, new(n)); }
}

static class Fmt
{
    public static string RecorderDll(GameState s) => s.AgsHost ? ScsKiller.AgsName : s.Records11 ? ScsKiller.Dll11 : ScsKiller.Dll12;
    public static string N(long? n) => n?.ToString("N0") ?? Format.Dash;
    /// <summary>"today at 20:14", "yesterday at 20:14", else "on 26/09/2026" (the culture's short date).</summary>
    public static string When(DateTimeOffset at)
    {
        var t = at.LocalDateTime;
        return t.Date == DateTime.Today ? $"今天 {t:t}" : t.Date == DateTime.Today.AddDays(-1) ? $"昨天 {t:t}" : $"{t:M月d日}";
    }
    public static string KeptSize(KeptCache k) => $"{Format.Bytes(k.Bytes)} · " + (k.Frees > 0 ? $"可释放 {Format.Bytes(k.Frees)}" : "takes no extra space");
    public static string Vendor(GpuVendor v) => v switch
    {
        GpuVendor.Nvidia => "NVIDIA", GpuVendor.Amd => "AMD", GpuVendor.Intel => "Intel", _ => "GPU",
    };
    public static string Engine(EngineInfo? e) => e is null ? "未知引擎" : Format.Engine(e, e.Family == "Unreal" ? "UE" : null) + Guessed(e);
    public static string Guessed(EngineInfo e) => e.VersionGuessed ? "（推测）" : "";
    public static Style Style(string key) => (Style)Application.Current.Resources[key];
    /// <summary>"Severe shader-compilation stutter: reason", then the source and when it was checked; null when not listed.</summary>
    public static string? StutterTip(GameState s) => s.KnownStutter is { } k
        ? $"{(k.Severity == StutterSeverity.Severe ? "严重" : "中等")}着色器编译卡顿：{k.Reason}。\n来源：{k.Source}（检查于 {k.Date}）" : null;
    /// <summary>Library sections, in list order. GOG, Ubisoft, Battle.net, PURPLE, HoYoPlay, Gaijin and Riot Client games are Store.Other: told apart by their id prefix.</summary>
    public static readonly string[] Stores = ["Steam", "Epic Games", "Xbox / Game Pass", "EA app", "GOG", "Ubisoft Connect", "Battle.net", "PURPLE", "HoYoPlay", "Gaijin", "Riot Client", "Other", AddedByYou];
    public const string AddedByYou = "手动添加";
    public static string StoreName(Game g) => g.Store switch
    {
        Store.Steam => "Steam", Store.Epic => "Epic Games", Store.Xbox => "Xbox / Game Pass", Store.EA => "EA app", Store.Manual => AddedByYou,
        _ => g.Id[..Math.Max(0, g.Id.IndexOf(':'))] switch { "gog" => "GOG", "ubisoft" => "Ubisoft Connect", "battlenet" => "Battle.net", "purple" => "PURPLE", "hoyoplay" => "HoYoPlay", "gaijin" => "Gaijin", "riot" => "Riot Client", _ => "Other" },
    };
    /// <summary>Measured driver cache when known, else the estimate.</summary>
    public static string Cache(GameState s) => s.DiskBytes is { } c ? Format.Bytes(c) : s.EstimatedCacheBytes is { } b ? "≈ " + Format.Bytes(b) : Format.Dash;
    /// <summary>The last compile's measured time, else the estimate: the estimate's rate is measured on a cold cache only.</summary>
    public static TimeSpan? CompileTime(GameState s) => s.LastWarmTime ?? s.EstimatedWarmTime;
    /// <summary>Tooltip for an estimated (not measured) cache value; null when measured.</summary>
    public static string? CacheTip(GameState s) => s.DiskBytes == null && s.EstimatedCacheBytes != null ? "编译后预计占用的驱动缓存" : null;
    /// <summary>One middleware tag: what it compiles and its DLLs.</summary>
    public static string TagTip(MiddlewareTag t) => Format.Middleware(t) + "\n    " + string.Join(", ", t.Dlls);
    public static bool Active(QueueItem q) => q.Stage is not (QueueStage.Done or QueueStage.Failed or QueueStage.Stopped);
    public static void PauseOrResume(QueueItem? running)
    {
        if (running?.Stage == QueueStage.Paused) App.Core.ResumeQueue(); else App.Core.PauseQueue();
    }

    /// <summary>The queue page's run order: the running compile (number 1), then the waiting ones; plan checks left out.</summary>
    public static (QueueItem? Running, List<QueueItem> Waiting) RunOrder(IEnumerable<QueueItem> all)
    {
        var compiles = all.Where(q => !q.PlanCheck).ToList();
        return (compiles.FirstOrDefault(Format.Running), compiles.Where(q => q.Stage == QueueStage.Waiting).ToList());
    }
    public static string Stage(QueueItem q) => q.Stage switch
    {
        QueueStage.Indexing => "正在读取着色器", QueueStage.Planning => "正在生成计划", QueueStage.Materializing => "正在准备文件",
        QueueStage.Paused => q.Note is { } n ? "已暂停：" + n : "已暂停",
        QueueStage.Warming when q.Note is { } n => "正在编译：" + n,   // stalled (ScsKiller.StalledNote)
        _ => "正在编译",
    };
    /// <summary>"54.8% · 1,234 of 5,000 pipelines · 480/s" (reading shaders: packages); null before the warm knows its total.</summary>
    public static string? ProgressText(QueueItem q) => q.Progress is { Total: > 0 } p
        ? $"{Percent(q):0.0}% · {p.Done:N0}/{p.Total:N0} 个{(q.Stage == QueueStage.Indexing ? "包" : "管线")}" + (p.PerSecond > 0 && !ScsKiller.Stalled(q) ? $" · {p.PerSecond:N0}/秒" : "") + (p.Failed > 0 ? $" · {p.Failed:N0} 条失败" : "") + (p.Skipped > 0 ? $" · {p.Skipped:N0} 条跳过" : "")   // short (the Done note explains them); a count wraps whole
            + (ScsKiller.MemoryLimitNote(p) is { } m ? " · " + m : "")
        : null;
    public static double Percent(QueueItem? q) => q?.Progress is { Total: > 0 } p ? 100.0 * p.Done / p.Total : 0;
    public static bool Indeterminate(QueueItem? q) => q != null && q.Progress is not { Total: > 0 };
    public static TimeSpan? Left(QueueItem q, GameState? g) => ScsKiller.Stalled(q) ? null   // no estimate for a warm that stopped moving
        : q.Progress is { PerSecond: > 0 } p ? TimeSpan.FromSeconds((p.Total - p.Done) / p.PerSecond)
        : g is not null ? g.EstimatedWarmTime ?? g.LastWarmTime : null;

    /// <summary>The game's compile runs (or is paused), or is the next the running queue starts: Play waits, since the warm
    /// stages a process under the game's exe name. A stopped queue's items and "when idle" items don't block it.</summary>
    public static bool CompilingSoon(IEnumerable<QueueItem> queue, string gameId)
    {
        var compiles = queue.Where(q => !q.PlanCheck).ToList();
        return compiles.Any(q => q.GameId == gameId && Format.Running(q))
            || App.Core.QueueRunning && compiles.FirstOrDefault(q => q.Stage == QueueStage.Waiting && q.Note != ScsKiller.WhenIdleNote)?.GameId == gameId;
    }

    public const string CompilingTip = "正在编译，请完成后再启动游戏";

    /// <summary>What IScsKiller doesn't carry, from the real core or the design data.</summary>
    public static ManualSource? Added => App.Core is ScsKiller k ? k.Manual : (App.Core as Design.FakeScsKiller)?.Manual;
    public static string? ScanProgress => App.Core is ScsKiller k ? k.ScanProgress : (App.Core as Design.FakeScsKiller)?.ScanProgress;
    public static string? AfterUpdateLine => App.Core is ScsKiller k ? k.AfterUpdateLine : (App.Core as Design.FakeScsKiller)?.AfterUpdateLine;
    public static void Rename(string gameId, string name)
    {
        if (App.Core is ScsKiller k) k.RenameManualGame(gameId, name);
        else (App.Core as Design.FakeScsKiller)?.RenameManualGame(gameId, name);
    }


    /// <summary>Starts the game through its store (<see cref="StoreLaunch"/>); the error to show, or null. The design data starts nothing.</summary>
    public static string? Play(GameState s)
    {
        var g = s.Game;
        if (StoreLaunch.OwnLauncherOnly(s)) return $"请从它自己的启动器启动 {g.Name}";
        if (App.Core is Design.FakeScsKiller) return null;
        try
        {
            if (StoreLaunch.Command(g) is not { } command) return $"找不到 {StoreName(g)} 启动 {g.Name} 的方式，请刷新游戏库。";
            System.Diagnostics.Process.Start(command)?.Dispose();
            return null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"Couldn't start {g.Name}{(g.Store == Store.Manual ? "" : " through " + StoreName(g))}: {e.Message}";
        }
    }

    /// <summary>The Play button's tooltip when it can start the game.</summary>
    public static string PlayVia(Game g) => g.Store == Store.Manual ? $"启动 {Path.GetFileName(g.ExePath)}"
        // the store starts the collection, which opens the game played last
        : Collections.Member(g.Id) != null ? $"通过 {StoreName(g)} 启动，如果打开了别的游戏，再在游戏菜单里选择 {g.Name}"
        : $"通过 {StoreName(g)} 启动";
}

/// <summary>Folds a burst of Core events into one run on the UI thread: a scan raises GameChanged once per game (62 at
/// once on a real library), and a page refresh per event kept the UI thread busy for seconds. Request from any thread.</summary>
public sealed class Coalesced(DispatcherQueue ui, Action run)
{
    int pending;

    public void Request()
    {
        if (Interlocked.Exchange(ref pending, 1) == 0 && !ui.TryEnqueue(() => { Volatile.Write(ref pending, 0); run(); }))
            Volatile.Write(ref pending, 0);   // the window is closing
    }
}

/// <summary>A read that must not run on the UI thread, such as the driver cache's usage and limit (NVIDIA's limit loads the
/// driver's profile database: ~130 ms measured; the usage walks the cache folder). <paramref name="read"/> runs on a
/// background thread, <paramref name="apply"/> on the UI thread. One read at a time: requests meanwhile fold into one more
/// read, so results apply in order. Request on the UI thread.</summary>
public sealed class BackgroundRead<T>(Func<T> read, Action<T> apply)
{
    bool busy, again;

    public async void Request()
    {
        if (busy) { again = true; return; }
        busy = true;
        try
        {
            do
            {
                again = false;
                apply(await Task.Run(read));
            } while (again);
        }
        finally { busy = false; }
    }
}

/// <summary>Game icons: a packaged (Xbox / Game Pass) game's own logo, else the exe's shell thumbnail (its own icon); null
/// when the exe isn't there or the icon failed to load after retries (the row shows its tile). UI thread only.</summary>
static class Icons
{
    // The stream stays open as long as its image: XAML may decode again from it (another size: Library 32 px, detail 56 px).
    static readonly Dictionary<string, (BitmapImage Image, IDisposable? Source)> cache = [];
    static readonly HashSet<string> failed = [];

    /// <summary>An icon failed after its row was built: Get now returns null for it, so rebuild the rows (UI thread).</summary>
    public static event Action? Failed;

    public static ImageSource? Get(string exePath)
    {
        if (failed.Contains(exePath) || !File.Exists(exePath)) return null;
        if (!cache.TryGetValue(exePath, out var entry))
        {
            var bmp = new BitmapImage();
            bmp.ImageFailed += (_, _) => Fail(exePath);   // a decode failure
            cache[exePath] = entry = (bmp, null);
            _ = LoadAsync(exePath, bmp);
        }
        return entry.Image;
    }

    static async Task LoadAsync(string path, BitmapImage bmp)
    {
        var logo = await Task.Run(() => PackageLogo(path));
        for (var attempt = 1; ; attempt++)
        {
            IRandomAccessStream? stream = null;
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(logo ?? path);
                stream = logo != null ? await file.OpenReadAsync()
                    : await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 64) ?? throw new IOException("无缩略图");
                cache[path] = (bmp, stream);
                await bmp.SetSourceAsync(stream);
                return;
            }
            catch (Exception)
            {
                stream?.Dispose();
                // The shell often answers E_PENDING (0x8000000A) at first, while many icons are asked for at once (seen on
                // Elden Ring's and SCSKiller's own exe): retry for ~8 s before the tile. An unreadable exe fails every time.
                if (attempt == 5) { Fail(path); return; }
                await Task.Delay(250 << attempt);
            }
        }
    }

    /// <summary>The Square44x44Logo of the package the exe sits in (appxmanifest.xml in a parent folder): a GDK game's exe
    /// often has no icon of its own (Atomic Heart), and the shell then shows the generic application icon. Null when not
    /// packaged, unreadable (WindowsApps) or the logo is only there scale-qualified.</summary>
    static string? PackageLogo(string exePath)
    {
        try
        {
            for (var dir = Path.GetDirectoryName(exePath); dir != null; dir = Path.GetDirectoryName(dir))
            {
                var manifest = Path.Combine(dir, "appxmanifest.xml");
                if (!File.Exists(manifest)) continue;
                var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(manifest), "Square44x44Logo=\"([^\"]+)\"");
                return m.Success && Path.Combine(dir, m.Groups[1].Value) is var logo && File.Exists(logo) ? logo : null;
            }
        }
        catch (Exception) { }
        return null;
    }

    static void Fail(string path)
    {
        if (!failed.Add(path)) return;
        if (cache.Remove(path, out var entry)) entry.Source?.Dispose();
        Failed?.Invoke();
    }
}

/// <summary>A small tag on a library row (a middleware label, or "+N").</summary>
public sealed record TagChip(string Text, string Tip, double Width = 90, double Side = 6)   // Width: the text's most, trimmed past it
{
    public Thickness Pad => new(Side, 0, Side, 0);
}

/// <summary>A game's row in the Library. The list keeps one row per game and <see cref="Update"/>s it in place, so the
/// row's container, the scroll position and hover survive a re-evaluation.</summary>
public sealed class GameRow(GameState s, bool queued = false, bool compiling = false) : Bindable
{
    static readonly Color[] Tiles =
    [
        Color.FromArgb(255, 0x3b, 0x4a, 0x8c), Color.FromArgb(255, 0x6b, 0x3b, 0x5e), Color.FromArgb(255, 0x2f, 0x6b, 0x4f),
        Color.FromArgb(255, 0x2f, 0x5f, 0x73), Color.FromArgb(255, 0x7a, 0x4a, 0x2a), Color.FromArgb(255, 0x5a, 0x6b, 0x2f),
        Color.FromArgb(255, 0x3d, 0x55, 0x66), Color.FromArgb(255, 0x5c, 0x3b, 0x7a),
    ];

    public GameState State => s;
    public bool Playing => s.Playing;
    public string Id => s.Game.Id;
    public string Name => s.Game.Name;
    public ImageSource? Icon { get; private set; } = Icons.Get(s.Game.ExePath);
    public bool NoIcon => Icon is null;
    public Brush Tile { get; } = TileOf(s.Game.Name);
    public string Initials => InitialsOf(s.Game.Name);
    public static Brush TileOf(string name) => new SolidColorBrush(Tiles[name.Sum(c => c) % Tiles.Length]);
    public static string InitialsOf(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsUpper(w[0]) || char.IsDigit(w[0])).ToArray();
        return words.Length >= 2 ? $"{words[0][0]}{words[1][0]}" : name[..Math.Min(2, name.Length)].ToUpperInvariant();
    }
    public string Sub => $"{Fmt.Engine(s.Engine)} · {StoreName}";
    public string StoreName { get; } = Fmt.StoreName(s.Game);
    /// <summary>Search: the name, the store as players name it, or the engine ("UE 5", "Unreal"), case-insensitive.</summary>
    public bool Matches(string term) => Name.Contains(term, StringComparison.OrdinalIgnoreCase) || StoreName.Contains(term, StringComparison.OrdinalIgnoreCase)
        || Fmt.Engine(s.Engine).Contains(term, StringComparison.OrdinalIgnoreCase) || s.Engine?.Family.Contains(term, StringComparison.OrdinalIgnoreCase) == true;
    public bool HasTags => s.Middleware is { Count: > 0 } || ModChip(s) != null || ScsKiller.DeliveryChip(s) != null;
    public bool HasTagLine => HasTags || IsKnownStutter || IsNoStutter || HasBaChip;
    /// <summary>A before and after recording in progress; the tile has no button for it, the game page has.</summary>
    public string BaChip => BeforeAfterUi.Voided(s.BeforeAfter) ? "" : s.BeforeAfter?.Stage switch
    {
        BeforeAfterStage.AfterReady => "“之后”运行已就绪",
        BeforeAfterStage.Compiling => "前后对比：正在编译",
        BeforeAfterStage.BeforePlaying or BeforeAfterStage.AfterPlaying => "前后对比：游玩中",
        _ => "",
    };
    public bool HasBaChip => BaChip.Length > 0;
    public bool IsKnownStutter => s.KnownStutter != null;
    public string? StutterTip => Fmt.StutterTip(s);
    /// <summary>On game-verdicts.json as no-stutter: compiling stays possible, but the bulk adds leave it out.</summary>
    public bool IsNoStutter => s.NoStutter != null;
    public string? NoStutterTip => s.NoStutter;
    // The shader mod, then the upscalers: the first three, then "+N" with the rest in its tooltip. After the stutter chip
    // only one more chip fits before the Shaders column: the mod, else several upscalers become "N upscalers".
    const int MaxTags = 3;
    public List<TagChip> Tags { get; private set; } = TagsOf(s);
    static List<TagChip> TagsOf(GameState s)
    {
        var m = s.Middleware ?? [];
        var mod = ModChip(s);
        var asd = ScsKiller.DeliveryChip(s) is { } d ? new TagChip(d, ScsKiller.DeliveryTip, 190, 4) : null;
        List<TagChip> tags = [.. mod != null ? [mod] : Array.Empty<TagChip>(), .. asd != null ? [asd] : Array.Empty<TagChip>(), .. m.Select(t => new TagChip(t.Label, Fmt.TagTip(t)))];
        return s.KnownStutter != null || s.NoStutter != null ? tags.Count > 1 ? [mod ?? asd ?? new TagChip($"{m.Count} upscalers", string.Join("\n", m.Select(Fmt.TagTip)))] : tags
            : tags.Count <= MaxTags ? tags
            : [.. tags.Take(MaxTags), new TagChip($"+{tags.Count - MaxTags}", string.Join("\n", tags.Skip(MaxTags).Select(t => t.Tip)))];
    }
    /// <summary>The shader mod by name, else plain ReShade; null = neither, or not looked (anti-cheat).</summary>
    internal static TagChip? ModChip(GameState s) => s.ShaderMod is { } mod
        ? new(mod, s.ShaderModBlocks ? "会阻止此游戏编译" : s.ShaderModLayer ? "Compiled through your mod" : "A few modded shaders aren't precompiled")
        : s.ReShade ? new("ReShade", "不改变任何管线") : null;
    public string Shaders => Fmt.N(s.ShaderCount);
    public string Pipelines => Fmt.N(ScsKiller.PlanPipelines(s));
    public string Cache => Fmt.Cache(s);
    public string? CacheTip => Fmt.CacheTip(s);
    public Style CacheStyle => Fmt.Style(CacheTip != null ? "Secondary" : "BodyTextBlockStyle");
    public string Time => Format.Duration(Fmt.CompileTime(s));

    bool Partly => ScsKiller.IsPartlyWarmed(s);
    bool PartlyCompiled => ScsKiller.IsPartlyCompiled(s);
    public string StatusText => s.Status switch
    {
        _ when s.CompileUnreached => "编译没有帮助",
        _ when PartlyCompiled => "部分管线被驱动拒绝",
        _ when ScsKiller.NotReached(s.StatusReason) => "尚未进行到这里",
        GameStatus.Warmed when Partly => "部分已预编译",
        GameStatus.Warmed when s.Plan?.RecordedOnly == true => "已预编译（仅录制的管线）",
        GameStatus.Warmed => "已预编译",
        GameStatus.Ready => "可以编译",
        _ when ScsKiller.NeedsOfflineSession(s) => "需要离线会话",
        GameStatus.NeedsRecording when ScsKiller.NeverRecorded(s) => ScsKiller.NothingRecordedTitle,
        GameStatus.NeedsRecording => ScsKiller.RecordedEnough(s) ? "需要录制" : "需要 5 分钟录制",
        GameStatus.Stale => "需要重新编译",
        _ when s.StatusReason == ScsKiller.CheckingReason => "正在检查…",
        _ when s.StatusReason == ScsKiller.NotCheckedReason => "未检查",
        _ when s.StatusReason == ScsKiller.CantReachReason => "不兼容",
        _ => s.ShaderModBlocks ? "尚未编译" : s.Engine?.Encrypted == true ? "游戏文件已加密" : s.NotPlanned ? "不受支持" : "尚不支持",
    };
    /// <summary>The whole reason: the row's tooltip and the game page's status text.</summary>
    public string FullNote => s.Status switch
    {
        _ when s.CompileUnreached || PartlyCompiled || ScsKiller.NotReached(s.StatusReason) => Format.ReasonText(s.StatusReason),
        GameStatus.Warmed when Partly => $"驱动 {s.WarmedDriverVersion} · 首次启动仍编译了 {s.Careful!.LaunchCompiled * 100:0}%",
        GameStatus.Warmed => $"驱动 {s.WarmedDriverVersion}" + (ScsKiller.IsPartial(s.Plan) ? " · 一次录制可编译其余部分"
            : ScsKiller.RtAfterRecording(s) ? " · " + ScsKiller.RtAfterRecordingNote : ""),
        _ when ScsKiller.NeedsOfflineSession(s) => Format.ReasonText(s.StatusReason),
        GameStatus.NeedsRecording when s.AnyAntiCheat && !s.RiskEligible => $"{ScsKiller.AntiCheatName(s)} 会阻止录制",
        GameStatus.NeedsRecording when s.RecordingPaused => ScsKiller.PausedNote(App.Core.Settings),
        GameStatus.NeedsRecording when ScsKiller.NeverRecorded(s) => s.RecorderRefused!,
        GameStatus.NeedsRecording when s.RecorderInstalled && !ScsKiller.RecordedEnough(s) => Format.RecorderOnNote(s),
        _ => Format.ReasonText(s.StatusReason),
    };
    /// <summary>The row's note under the status: a few words (<see cref="Format.ShortNote"/>); null when the status says it all.</summary>
    public string? Note => Format.ShortNote(s);
    // a mod the compile runs through takes the note's place; the tooltip keeps the full reason
    string? ListNote => s.ShaderModLayer ? $"{(s.Status == GameStatus.Warmed ? "已编译" : "编译")}经由 {s.ShaderMod}" : Note;
    public string RowNote => Playing ? "正在游玩" + (ListNote is { } n ? " · " + n : "") : ListNote ?? "";
    public bool HasRowNote => RowNote.Length > 0;
    public string RowTip => (Playing ? "正在游玩 · " : "") + FullNote;
    public Style StatusStyle => Fmt.Style(s.Status switch
    {
        _ when s.CompileUnreached || PartlyCompiled || ScsKiller.NotReached(s.StatusReason) => "StatusWarn",
        GameStatus.Warmed when Partly => "StatusWarn",
        GameStatus.Warmed => "StatusAccent",
        GameStatus.Ready => "StatusPrimary",
        GameStatus.Unsupported => "StatusMuted",
        _ => "StatusWarn",
    });

    // One action per status (Main.dc.html): Details / Add to queue / Record / disabled Why?
    public bool Queued => queued;
    public bool InAddAllReady => ScsKiller.InAddAllReady(s);
    public bool IsAdd => s.Status is GameStatus.Ready or GameStatus.Stale && !s.CompileUnreached;
    public string AddText => queued ? "已在队列中" : "加入队列";
    public bool CanAdd => !queued;
    public bool IsRecord => s.Status == GameStatus.NeedsRecording && !s.RecorderInstalled && s.NoStutter == null && !ScsKiller.NeedsOfflineSession(s) && !s.RiskEligible && !Shared;
    bool Shared => Collections.RecordedBy(s.Game.Id) != null;   // recorded by its collection's first game's recorder, switched on that game's page
    public bool CanRecord => !s.AnyAntiCheat;
    public bool IsDetails => s.Status == GameStatus.Warmed || ScsKiller.IsUnchecked(s) || s.CompileUnreached || ScsKiller.NeedsOfflineSession(s) || (s.Status == GameStatus.NeedsRecording && (s.RecorderInstalled || s.NoStutter != null || s.RiskEligible || Shared));
    public bool IsWhy => s.Status == GameStatus.Unsupported && !ScsKiller.IsUnchecked(s);
    public string Reason => s.StatusReason;

    // Play goes through the store (a game the user added: its exe); never for an anti-cheat game (SCSKiller never launches them)
    public bool CanLaunch { get; private set; } = BeforeAfterUi.Launchable(s);
    public bool Compiling => compiling;
    public bool CanPlay => !Playing && !compiling;
    public string PlayText => Playing ? "运行中" : "开始游戏";
    public string PlayTip => Playing ? $"{Name} is running" : compiling ? Fmt.CompilingTip : Fmt.PlayVia(s.Game);

    public override string ToString() => $"{Name}, {StatusText}, {RowTip}";   // list item name for screen readers

    /// <summary>The game's latest state; no change raised when it is the one shown. A re-evaluated state is unequal even
    /// with nothing new (its lists are other instances): the bindings then set the same values again, which shows nothing.</summary>
    public void Update(GameState now, bool nowQueued, bool nowCompiling)
    {
        var icon = Icons.Get(now.Game.ExePath);
        if (now == s && nowQueued == queued && nowCompiling == compiling && icon == Icon) return;
        (s, queued, compiling, Icon) = (now, nowQueued, nowCompiling, icon);
        CanLaunch = BeforeAfterUi.Launchable(s);
        // a new list makes the row build its chips again
        if (TagsOf(s) is var tags && !tags.SequenceEqual(Tags)) Tags = tags;
        Changed();
    }
}

/// <summary>One store's section of the library list (the rows shown after the search filter).</summary>
public sealed class StoreGroup(string store) : ObservableCollection<GameRow>
{
    public const string Recommended = "建议编译";
    public string Store => store;
    public string CountText => $"{Count} 个游戏" + (store == Recommended ? RecommendedNote() : "");
    // a row updated in place (compiled, cleared) changes the count of compiled ones without a collection change
    public void Recount() => OnPropertyChanged(new PropertyChangedEventArgs(nameof(CountText)));
    string RecommendedNote() => this.Count(r => r.State.Status == GameStatus.Warmed) is var done and > 0
        ? $" · 不编译就会卡顿 · 已编译 {done}" : " · 不编译就会卡顿";
}

public sealed record HiddenRow(string Id, string Name, string Store)
{
    public string ShowName => $"在游戏库中显示 {Name}";
}

public sealed class LibraryVm : Bindable
{
    static readonly LibraryScan firstScan = new();
    readonly BackgroundRead<(CacheUsage Usage, CacheLimit? Limit, long? Capped, long OldDriver)> cache;

    public LibraryVm() => cache = new(() => (App.Core.Vendor.GetCacheUsage(), App.Core.Vendor.GetCacheLimit(), (App.Core.Vendor as AmdBackend)?.AppCache.DxcBytes(),
        App.Core.OldDriverBytes()), ShowCache);

    public IReadOnlyList<GameRow> Games { get; private set; } = [];   // every game, sorted; the list shows Groups
    /// <summary>The list: rows matching <see cref="Filter"/> per store, empty stores left out. Filtering reuses the rows
    /// (no icon reads, no IO).</summary>
    public ObservableCollection<StoreGroup> Groups { get; } = [];
    string filter = "";
    public string Filter { get => filter; set { if (value.Trim() != filter) { filter = value.Trim(); ApplyFilter(); Changed(); } } }
    LibraryShow show;
    public static readonly string[] ShowLabels = ["全部游戏", "Not supported", "可以编译", "Compiled", "Compiled by SCSKiller"];
    public int ShowIndex { get => (int)show; set { if (value >= 0 && (LibraryShow)value != show) { show = (LibraryShow)value; ApplyFilter(); Changed(); } } }
    int shown;
    public bool Filtering => filter.Length > 0 || show != LibraryShow.All;
    public bool Searching => filter.Length > 0;
    public string ShownText => $"{shown} of {Games.Count} shown";
    public bool NoMatch => Filtering && Games.Count > 0 && shown == 0;
    public string NoMatchText => Searching ? $"没有匹配“{filter}”的游戏" : "No games with this status";
    /// <summary>The games the user removed from the library, listed while <see cref="ShowHidden"/> is on.</summary>
    public IReadOnlyList<HiddenRow> Hidden { get; private set; } = [];
    public bool HasHidden => Hidden.Count > 0;
    bool showHidden;
    public bool ShowHidden { get => showHidden; set { if (value != showHidden) { showHidden = value; Changed(); } } }
    public bool HiddenShown => showHidden && HasHidden;
    public async void ShowInLibrary(HiddenRow r)
    {
        try { await Task.Run(() => App.Core.ShowGame(r.Id)); }
        catch (ArgumentException) { }   // shown again meanwhile
        Refresh();
    }

    LibrarySort sort = App.Store.LoadLibrarySort();
    /// <summary>A column header's click: its first order, then the reverse. Status goes back to the default order.</summary>
    public void SortBy(LibrarySort first, LibrarySort second)
    {
        sort = sort == first ? second : first;
        try { App.Store.SaveLibrarySort(sort); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // sorted all the same, just not remembered
        Refresh();
    }
    string Arrow(LibrarySort up, LibrarySort down) => sort == up ? " ↑" : sort == down ? " ↓" : "";
    public string GameHeader => "游戏" + Arrow(LibrarySort.NameAscending, LibrarySort.NameDescending);
    public string PipelinesHeader => "管线" + Arrow(LibrarySort.PipelinesAscending, LibrarySort.PipelinesDescending);
    public string CacheHeader => "缓存" + Arrow(LibrarySort.CacheAscending, LibrarySort.CacheDescending);
    public bool Scanning { get; private set; }
    int refreshing;   // Rescan calls not finished: the scan, then a user refresh's server fetches
    public bool Refreshing => refreshing > 0;
    string? refreshNote;
    public string? RefreshNote => (ScanEmpty ? null : Fmt.AfterUpdateLine) ?? refreshNote;
    public bool HasRefreshNote => RefreshNote != null;
    public string? Error { get; private set; }
    public bool HasError => Error != null;
    public string? GpuNotice => Format.GpuNotice(App.Core.Vendor.Gpu, App.Core.Settings.GpuNoticeDismissed, App.Core.Vendor is IntelBackend);
    public bool HasGpuNotice => GpuNotice != null;
    public void DismissGpuNotice()
    {
        App.Core.Settings = App.Core.Settings with { GpuNoticeDismissed = App.Core.Vendor.Gpu.Name };
        Changed();
    }

    public string Summary { get; private set; } = "";
    public int ReadyCount { get; private set; }     // ready, not queued yet and not a no-stutter game
    public int WaitingCount { get; private set; }
    public string AddAllText => $"全部加入队列（{ReadyCount}）";
    public bool CanAddAll => ReadyCount > 0 && !Scanning;
    public int RecommendedCount { get; private set; }   // stutters without a compile, ready and not queued yet
    public string AddRecommendedText => $"将建议游戏全部加入队列（{RecommendedCount}）";
    public bool CanAddRecommended => RecommendedCount > 0 && !Scanning;
    public bool CannotAddRecommended => !CanAddRecommended;   // the plain, disabled twin of the accent button (theme brushes stay in XAML)
    public string CompileQueueText => $"编译队列（{WaitingCount}）";
    public bool CanCompileQueue => WaitingCount > 0;

    public string CacheUsed { get; private set; } = "";
    public string CacheLimitText { get; private set; } = "";
    public double CachePercent { get; private set; }
    public bool CacheWarn { get; private set; }
    public string CacheWarnText { get; private set; } = "";
    public bool CanRaiseLimit => App.Core.Vendor.Caps.CacheSizeConfigurable;
    /// <summary>Below this, what an older NVIDIA driver left (old-type files of desktop apps, a few MB) isn't worth a line.</summary>
    const long OldDriverShown = 1L << 30;
    public string? OldDriverNote { get; private set; }
    public bool HasOldDriver => OldDriverNote != null;

    public string DriverMode => App.Core.Settings.OnDriverUpdate switch
    {
        DriverUpdateMode.Ask => "先询问我", DriverUpdateMode.WhenIdle => "电脑空闲时", _ => "Off",
    };
    public string DriverModeNote => App.Core.Settings.OnDriverUpdate switch
    {
        DriverUpdateMode.Ask => "通过通知选择立即编译、空闲时编译或跳过",
        DriverUpdateMode.WhenIdle => "离开电脑时自动重新编译",
        _ => "在游戏库中手动重新编译",
    };
    public string Threads => App.Core.Settings.Threads.ToString();
    public string ThreadsOf => $"/ {Environment.ProcessorCount} 个线程";
    public string SpeedNote { get; private set; } = "";

    public void Load()
    {
        Refresh();
        if (firstScan.Start()) Rescan(force: false);
    }


    // The scan message waits 300 ms so a cached scan doesn't flicker it; the list fills when the scan ends.
    bool slowScan, forced;
    public bool ScanEmpty => slowScan && Games.Count == 0;
    public bool ScanBusy => slowScan && Games.Count > 0;
    public string ScanEmptyNote => Fmt.AfterUpdateLine ?? "正在检查 Steam、Epic、Xbox、EA、GOG、Ubisoft Connect、Battle.net、PURPLE、HoYoPlay、Gaijin 和 Riot Client，然后检查每个游戏使用的引擎。"
        + "首次扫描会读取各游戏的文件，可能需要一段时间。";
    public string ScanTitle => Fmt.ScanProgress ?? "正在检查新增或变更的游戏…";
    public string ScanEmptyTitle => Fmt.ScanProgress ?? "正在查找游戏…";
    /// <summary>A scan's step: the last list, which the first scan puts up before it reads the games, else the progress line.</summary>
    public void ScanStep()
    {
        if (Games.Count == 0 && App.Core.Games.Count > 0) Refresh();
        else Changed();
    }
    public string ScanBusyNote => forced ? "正在重新读取每个游戏的引擎与反作弊信息，检查完一个就更新一个。"
        : "检查完一个就更新一个。";

    public async void Rescan(bool force = true, bool userRequested = false)
    {
        Scanning = true; refreshing++; forced = force; Error = refreshNote = null; Refresh();   // the summary says "正在查找游戏…" while nothing is listed
        var scan = force ? App.Core.RescanAsync(CancellationToken.None, userRequested) : App.Core.ScanAsync(CancellationToken.None, userRequested);
        if (await Task.WhenAny(scan, Task.Delay(300)) != scan) { slowScan = true; Changed(); }
        try { await scan; firstScan.Ended(ok: true); }
        catch (Exception e) { Error = "扫描失败：" + e.Message; firstScan.Ended(ok: false); }
        Scanning = slowScan = false;
        Refresh();
        if (userRequested && Error == null && App.Core is ScsKiller k)
            refreshNote = await k.ServerRefresh switch
            {
                ScsKiller.ServerCheck.TooSoon => "几分钟前刚检查过服务器。",
                ScsKiller.ServerCheck.Unreachable => "无法连接 SCSKiller 服务器，列表保持原样。",
                _ => null,
            };
        refreshing--;
        Changed();
    }

    public void Refresh()
    {
        var core = App.Core;
        var list = Library.Sorted(core.Games, sort).ToList();
        var queue = core.Queue.Where(q => !q.PlanCheck).ToList();
        var queued = queue.Where(Fmt.Active).Select(q => q.GameId).ToHashSet();
        var rows = Games.ToDictionary(r => r.Id);
        Games = [.. list.Select(g =>
        {
            bool q = queued.Contains(g.Game.Id), compiling = Fmt.CompilingSoon(queue, g.Game.Id);
            if (!rows.TryGetValue(g.Game.Id, out var row)) return new GameRow(g, q, compiling);
            row.Update(g, q, compiling);
            return row;
        })];
        ApplyFilter();
        Hidden = [.. core.HiddenGames.Select(g => new HiddenRow(g.Id, g.Name, Fmt.StoreName(g)))];

        int ready = list.Count(g => g.Status is GameStatus.Ready or GameStatus.Stale);
        ReadyCount = Games.Count(r => r.InAddAllReady && !r.Queued);
        RecommendedCount = RecommendedToAdd().Count;
        WaitingCount = queue.Count(q => q.Stage == QueueStage.Waiting);
        var stores = Fmt.Stores.Where(n => n != Fmt.AddedByYou && Games.Any(r => r.StoreName == n)).ToList();
        var storeText = stores.Count > 2 ? $"{stores.Count} 个平台" : string.Join(" 和 ", stores);
        bool allUnreal = list.Count > 0 && list.All(g => g.Engine?.Family == "Unreal");
        Summary = Scanning && list.Count == 0 ? "正在查找游戏…"
            : $"{list.Count} {(allUnreal ? "Unreal Engine " : "")}个游戏{(storeText.Length > 0 ? "，来自 " + storeText : "")} · {ready} 个可编译";

        cache.Request();

        var rate = ScsKiller.MeasuredRate(list);
        SpeedNote = (core.Settings.Priority == WarmPriority.BelowNormal ? "低于正常优先级" : "空闲优先级")
                    + (rate is { } r ? $" · 实测约 {r:N0} 条管线/秒" : "");
        Changed();
    }

    /// <summary>Games -> Groups: same order within a store (<see cref="SortBy"/>). Sections and rows are kept and only moved,
    /// added or removed (<see cref="ListSync"/>): a Reset scrolls the list to the top and builds every row again.</summary>
    void ApplyFilter()
    {
        var visible = Games.Where(r => (filter.Length == 0 || r.Matches(filter)) && Library.Shows(r.State, show)).ToList();
        // known-stutter games go on top (StutterList.Recommended), out of their store's section, compiled or not
        var byId = visible.ToDictionary(r => r.Id);
        var recommended = StutterList.Recommended(visible.Select(r => r.State)).Select(s => byId[s.Game.Id]).ToList();
        if (sort != LibrarySort.Status) recommended = visible.Intersect(recommended).ToList();   // in the column's order
        var groups = visible.Except(recommended).GroupBy(r => r.StoreName)
            .OrderBy(g => Array.IndexOf(Fmt.Stores, g.Key)).Select(g => (g.Key, Rows: g.ToList())).ToList();
        if (recommended.Count > 0) groups.Insert(0, (StoreGroup.Recommended, recommended));
        shown = groups.Sum(g => g.Rows.Count);
        var wanted = groups.ToDictionary(g => g.Key, g => g.Rows.ToHashSet());
        // a row leaves its old section before it joins its new one: in two sections at once it would get two containers
        foreach (var have in Groups)
            for (int i = have.Count - 1; i >= 0; i--)
                if (!wanted.TryGetValue(have.Store, out var keep) || !keep.Contains(have[i])) have.RemoveAt(i);
        var kept = Groups.ToDictionary(g => g.Store);
        ListSync.Sync(Groups, [.. groups.Select(g => kept.GetValueOrDefault(g.Key) ?? new StoreGroup(g.Key))]);
        foreach (var (group, (_, rows)) in Groups.Zip(groups))
        {
            ListSync.Sync(group, rows);
            group.Recount();
        }
    }

    // Capped: AMD's limit is of its DirectX 12 cache alone, which the usage counts with the DirectX 11 one
    void ShowCache((CacheUsage Usage, CacheLimit? Limit, long? Capped, long OldDriver) c)
    {
        var (usage, limit, capped, old) = c;
        OldDriverNote = old >= OldDriverShown ? $"新驱动已弃用 {Format.Bytes(old)}" : null;
        CacheUsed = (usage.UpperBound ? "≤ " : "") + Format.Bytes(usage.BytesOnDisk);
        CacheLimitText = limit is null ? "上限未知" : limit.Bytes is { } lb ? capped is { } cb ? $"· DirectX 12 {Format.Bytes(cb)} / {Format.Bytes(lb)} 上限" : $"/ {Format.Bytes(lb)} 上限" : "无上限";
        CachePercent = limit?.Bytes is { } max && max > 0 ? Math.Min(100, 100.0 * (capped ?? usage.BytesOnDisk) / max) : 0;
        CacheWarn = limit?.Bytes is not null && (limit.IsDriverDefault || CachePercent > 75);
        CacheWarnText = limit?.IsDriverDefault == true ? "驱动默认上限可能导致旧游戏缓存被清除" : "缓存即将占满";
        Changed();
    }

    /// <summary>The Recommended section's games that can be queued now (not the ones that need a recording), every row, not the filtered view.</summary>
    List<GameRow> RecommendedToAdd()
    {
        var byId = Games.ToDictionary(r => r.Id);
        return StutterList.Recommended(Games.Select(r => r.State)).Select(s => byId[s.Game.Id]).Where(r => r.IsAdd && !r.Queued).ToList();
    }

    public void AddAllRecommended()
    {
        foreach (var g in RecommendedToAdd()) App.Core.Enqueue(g.Id);
        Refresh();
    }

    public async void Play(GameRow row)
    {
        Error = await BeforeAfterUi.PlayAsync(row.State);
        Changed();
    }

    public void AddAllReady()
    {
        foreach (var g in Games.Where(r => r.InAddAllReady && !r.Queued).ToList()) App.Core.Enqueue(g.Id);
        Refresh();
    }
}

public sealed class DetailVm(string id) : Bindable
{
    GameState s = App.Core.Games.First(g => g.Game.Id == id);
    public GameRow Row { get; private set; } = new(App.Core.Games.First(g => g.Game.Id == id));
    public string? Error { get; set; }
    public bool HasError => Error != null;

    public void Refresh()
    {
        place = InQueue(id);
        var now = App.Core.Games.FirstOrDefault(g => g.Game.Id == id);
        if (now != null && s.Playing && !now.Playing) ReadCaches();   // the game exited: its Windows and pipeline caches grew
        if (now != null && (now != s || Row.Icon != Icons.Get(now.Game.ExePath))) { s = now; Row = new GameRow(now); }   // or its icon failed: the tile
        blocked = Blocked(id);
        Changed();
    }

    static string Sentence(string t) => t.Length > 0 ? char.ToUpperInvariant(t[0]) + t[1..] : t;
    bool Partial => ScsKiller.IsPartial(s.Plan);
    /// <summary>Needs a recording for its ray tracing only: the rest compiles (a partial compile, offered but not the primary action).</summary>
    bool RtPartial => s.Status == GameStatus.NeedsRecording && ScsKiller.NeedsRtRecording(s);
    /// <summary>Compiled, and a recording would add its ray tracing: the record tip, not the warning callout.</summary>
    bool RtAfter => ScsKiller.RtAfterRecording(s);
    public bool NoAntiCheat => !s.AnyAntiCheat;
    /// <summary>The recorder may go next to the game: no anti-cheat, not a game added by hand whose folder isn't confirmed, and
    /// not one whose first d3d12.dll Streamline loads on NVIDIA.</summary>
    bool CanRecord => NoAntiCheat && !s.RootUnconfirmed && !s.StreamlineFirst;

    public string Name => s.Game.Name;
    public string Sub => $"{Path.GetFileName(s.Game.ExePath)} · {Fmt.StoreName(s.Game)}";   // engine and API are tags

    // Status: the state in its colour and why; the actions sit beside it.
    public string StatusGlyph => s.Status switch
    {
        _ when s.CompileUnreached || ScsKiller.IsPartlyCompiled(s) => "",
        GameStatus.Warmed => "", GameStatus.Ready => "", GameStatus.NeedsRecording => "", GameStatus.Stale => "", _ => "",
    };
    // The queue page's progress for this game: its number in the run order while it waits, its stage and progress while it runs.
    (QueueItem? Item, int Number) place = InQueue(id);
    static (QueueItem?, int) InQueue(string id)
    {
        var (running, waiting) = Fmt.RunOrder(App.Core.Queue);
        if (running?.GameId == id) return (running, 1);
        int i = waiting.FindIndex(q => q.GameId == id);
        return i < 0 ? (null, 0) : (waiting[i], i + (running != null ? 2 : 1));
    }
    /// <summary>A warm's tick: only the progress changes, unless the item started, finished or left the queue.</summary>
    public void RefreshProgress()
    {
        var was = place.Item?.Stage;
        place = InQueue(id);
        if (place.Item?.Stage != was) Refresh();
        else Changed(nameof(StatusTitle), nameof(StatusReason), nameof(Percent), nameof(Indeterminate));
    }
    QueueItem? Running => place.Item is { } q && Format.Running(q) ? q : null;
    public bool IsCompiling => Running != null;
    public double Percent => Fmt.Percent(Running);
    public bool Indeterminate => Fmt.Indeterminate(Running);

    public string StatusTitle => Running is { } r ? Fmt.Stage(r) : place.Item != null ? $"排队中（第 {place.Number} 位）" : Row.StatusText;
    public string StatusReason => Running is { } r && Fmt.ProgressText(r) is { } p
        ? p + (r.Progress is { PerSecond: > 0 } && Fmt.Left(r, s) is { } t ? $" · 还剩 {Format.Duration(t)}" : "")
        : GameReason;
    string GameReason => Sentence(s.Status switch
    {
        _ when s.CompileUnreached => Format.ReasonText(s.StatusReason) + (s.WarmedAt != null ? ". " + Sentence(ScsKiller.ClearForGameNote) : ""),
        _ when ScsKiller.IsPartlyCompiled(s) => ScsKiller.RefusedNote(s) + "。SCSKiller 更新后可能修复",
        GameStatus.Warmed when ScsKiller.IsPartlyWarmed(s) => s.StatusReason,
        GameStatus.Warmed => $"已为 {(ScsKiller.BothApis(s.Plan) ? "DirectX 11 和 DirectX 12，" : "")}驱动 {s.WarmedDriverVersion} 编译" + (s.WarmedAt is { } t ? $"（{t.LocalDateTime:d}）" : "")
            + (RtAfter ? ". " + Sentence(ScsKiller.RtAfterRecordingNote) : ""),
        // a cache here: played since a clear or a stopped compile; then the planner's note when the row has one to say
        GameStatus.Ready => (s.CacheOnDisk > 0 ? $"尚未完全编译：其中 {Format.Bytes(s.CacheOnDisk)} 已在驱动缓存中"
            : s.LastWarmTime == null ? "尚未编译" : "其着色器缓存是空的：请重新编译")
            + (Row.Note != null ? ". " + Sentence(Format.ReasonText(s.StatusReason)) : ""),
        GameStatus.NeedsRecording => HasDbTeaser ? NoDbNote(Row.FullNote) : Row.FullNote,   // the teaser says it
        _ when s.Engine?.Encrypted == true => "需要游戏的 AES 密钥",   // the title: "游戏文件已加密"
        _ => HasDbTeaser ? NoDbNote(Format.ReasonText(s.StatusReason)) : Format.ReasonText(s.StatusReason),
    })
        + (IsPartlyCompiled || s.CompileUnreached ? "。" : "") + Format.ModNote(s);
    static string NoDbNote(string r) => r.Replace("; " + ScsKiller.InDbSupportersNote, "").Replace("; " + ScsKiller.InDbNote, "");
    public bool IsPartlyCompiled => ScsKiller.IsPartlyCompiled(s);
    public bool NeedsAesKey => ScsKiller.NeedsAesKey(s);
    public const string PartlyCompiledWhy = "SCSKiller 已尝试编译此游戏，但显卡驱动拒绝了其中一部分管线。这可能是因为驱动或 Windows 无法按游戏使用的方式创建管线。被拒绝的管线会在游戏首次使用时自行编译，届时仍可能卡顿。更新驱动后，SCSKiller 会重新尝试编译。";

    // The manifest is public: a PC without "db" sees that the community database covers the game, and where to get it.
    // Shown, it is the page's only mention of the database.
    public bool HasDbTeaser => s is { InCommunityDb: true, Community: null } && !App.Account.HasDb
        && (s.Status == GameStatus.NeedsRecording || RtAfter || s.Status == GameStatus.Unsupported && !NoAntiCheat && s.StatusReason.StartsWith("需要一次录制，", StringComparison.Ordinal));
    public string DbTeaser => $"社区数据库中已有其他玩家录制的 {s.CommunityDbPsos:N0} 条管线。Patreon 支持者无需录制即可编译。";
    public string DbTeaserLink => App.Account.SignedIn ? "上游 Patreon 会员资格" : "使用 Patreon 登录";
    public bool CanCompile => s.Status is GameStatus.Ready or GameStatus.Warmed or GameStatus.Stale || RtPartial;
    public const string MaxCompileWhy = "把此游戏着色器的所有组合都编译一遍，而不是每个组合只编一次。耗时更长、占用磁盘更多，但游玩时需要现编的着色器更少。在 AMD 上效果最明显。";
    public string? GuessNote => s.Engine is { } e && s.Status is GameStatus.Ready or GameStatus.Warmed or GameStatus.Stale ? ScsKiller.GuessNote(e) : null;
    public bool HasGuessNote => GuessNote != null;
    public string? DeliveryLine => ScsKiller.DeliveryLine(s);
    public bool HasDeliveryLine => DeliveryLine != null;
    // warmed: nothing urgent, unless only partly (the careful compile is offered); RT: Record comes first; no shader stutter: not needed
    public Style CompileStyle => Fmt.Style((OffersCareful || s.Status != GameStatus.Warmed && !RtPartial) && s.NoStutter == null ? "AccentButtonStyle" : "DefaultButtonStyle");
    bool Queued => App.Core.Queue.Any(q => q.GameId == id && Fmt.Active(q) && !q.PlanCheck);
    public bool CanAdd => !Queued && !BaInProgressAny;
    /// <summary>A pair before Done: a compile or clear of the user's own would spoil a run (Core refuses them too).</summary>
    bool BaInProgressAny => s.BeforeAfter is { Stage: not BeforeAfterStage.Done };
    const string BaBusyTip = "前后对比录制正在进行";
    public string? CompileTip => BaInProgressAny ? BaBusyTip : null;
    public bool CanClearCache => HasCache && !BaInProgressAny;
    bool CompilingSoon => Fmt.CompilingSoon(App.Core.Queue, id);
    public bool ShowPlay => Row.CanLaunch;
    public bool CanPlay => !s.Playing && !CompilingSoon;
    public string PlayText => s.Playing ? "运行中" : "开始游戏";
    public string PlayTip => s.Playing ? $"{s.Game.Name} is running" : CompilingSoon ? Fmt.CompilingTip : Fmt.PlayVia(s.Game);
    public bool IsManual => s.Game.Store == Store.Manual;
    public string RemoveTip => IsManual ? "从游戏库移除此游戏。录制器会从游戏目录移除，游戏文件本身不受影响。"
        : "隐藏此游戏。游戏文件、存档和已编译缓存都会保留。";
    /// <summary>The confirmation's text for a store's game, which is hidden.</summary>
    public string HideNote => "它将不出现在列表中，也不会被自动编译。游戏文件、存档和已编译缓存都会保留。在游戏库页面点“显示已隐藏”可恢复。"
        + (BaInProgressAny ? " 进行中的前后对比录制会被取消。" : "")
        + (s.RecorderInstalled ? " 录制器会先被关闭并从游戏目录移除。" : "");
    public async void Play()
    {
        Error = await BeforeAfterUi.PlayAsync(s);
        Changed();
    }
    // Before and after recording (doc 6.5): one button in the status card, its note under it, a menu beside it once started
    string? blocked = Blocked(id);
    static string? Blocked(string id)
    {
        if (!App.Core.Settings.BeforeAfter) return null;
        try
        {
            var why = App.Core.BeforeAfterBlocked(id);
            return why != null && why == App.Core.Games.First(g => g.Game.Id == id).StatusReason ? Sentence(why) : why;   // a status reason starts lowercase
        }
        catch (Exception e) { return BeforeAfterUi.Describe(e); }
    }
    BeforeAfterInfo? Ba => s.BeforeAfter;
    (bool Shown, bool Enabled, string Note) BaStart => BeforeAfter.StartButton(App.Core.Settings.BeforeAfter, blocked);
    public bool ShowBa => Ba != null ? App.Core.Settings.BeforeAfter : BaStart.Shown;
    public bool BaHasMenu => ShowBa && Ba != null;
    public bool BaVoided => BeforeAfterUi.Voided(Ba);
    public bool BaInProgress => Ba is { Stage: not BeforeAfterStage.Done };   // voided too: Cancel stays
    public bool BaDone => Ba is { Stage: BeforeAfterStage.Done } && !BaVoided;
    bool BaLaunchable => BeforeAfterUi.Launchable(s);
    public string BaText => Ba switch
    {
        null => "前后对比",
        _ when BaVoided => "重新开始",
        { Stage: BeforeAfterStage.BeforeArmed, ClearPending: true } => "清除缓存后重试",
        { Stage: BeforeAfterStage.BeforeArmed or BeforeAfterStage.AfterArmed } => BaLaunchable ? $"启动 {Name}" : $"Start {Name} as usual",
        { Stage: BeforeAfterStage.BeforePlaying or BeforeAfterStage.AfterPlaying } p => p.Minutes * 60 - p.PlayedSeconds is var left and > 0
            ? $"前后对比：游玩中，还剩 {(int)left / 60}:{(int)left % 60:00}" : "Before and after: time's up, close the game",
        { Stage: BeforeAfterStage.Compiling or BeforeAfterStage.AfterReady } => "继续“之后”的录制",
        _ => "查看前后对比",
    };
    public bool BaEnabled => Ba?.Stage switch
    {
        null => BaStart.Enabled,
        _ when BaVoided || Ba!.ClearPending => !s.Playing,
        BeforeAfterStage.BeforeArmed or BeforeAfterStage.AfterArmed => BaLaunchable && !s.Playing,
        BeforeAfterStage.AfterReady => !s.Playing,
        BeforeAfterStage.Done => true,
        _ => false,
    };
    public Style BaStyle => Fmt.Style(BaVoided || Ba?.Stage is BeforeAfterStage.BeforeArmed or BeforeAfterStage.AfterArmed or BeforeAfterStage.AfterReady ? "AccentButtonStyle" : "DefaultButtonStyle");
    public string? BaTip => Ba == null ? blocked ?? "先冷启动测量此游戏，再编译，然后重测一次，得到可分享的报告"
        : Ba.Stage is BeforeAfterStage.BeforeArmed or BeforeAfterStage.AfterArmed && !BaLaunchable ? $"照常启动 {Name}，SCSKiller 会检测到它启动" : null;
    public string BaNote => Ba switch
    {
        null => BaStart.Note,
        { Problem: { } problem } => problem,
        { Stage: BeforeAfterStage.BeforeArmed } b => $"从一个可再次读取的存档开始游玩 {b.Minutes} 分钟，然后关闭游戏",
        { Stage: BeforeAfterStage.AfterArmed } a => $"读取同一个存档，同样游玩 {a.Minutes} 分钟",
        { Stage: BeforeAfterStage.BeforePlaying or BeforeAfterStage.AfterPlaying } => "时间到了就关闭游戏",
        { Stage: BeforeAfterStage.Compiling } => "等待编译",
        { Stage: BeforeAfterStage.AfterReady } => "已编译：接下来进行“之后”运行",
        _ => "",
    };
    public bool HasBaNote => ShowBa && BaNote.Length > 0;
    /// <summary>The compile failed or was stopped: the note says why.</summary>
    public bool BaCompileAgain => Ba is { Stage: BeforeAfterStage.Compiling, Problem: not null } && !BaVoided && !Queued;

    public string AddText => Queued ? "已在队列中" : OffersCareful ? "谨慎编译" : RtPartial ? "编译非光追部分" : "编译";
    /// <summary>Partly warmed by a fast compile: the compile button turns the careful compile on first.</summary>
    public bool OffersCareful => ScsKiller.IsPartlyWarmed(s) && s.Careful is { On: false };

    // AMD's careful compile: a switch in the compile card, off while the game is queued (a running compile keeps its schedule)
    public bool HasCareful => s.Careful != null && CanCompile;
    public bool? CarefulPending { get; set; }
    public bool LeftOutOn => LeftOutPending ?? s.LeftOutOfAddAll;
    public bool? LeftOutPending { get; set; }
    public bool CarefulOn => CarefulPending ?? s.Careful?.On == true;
    public bool CanToggleCareful => CarefulPending == null && !Queued;
    public string CarefulNote => "Only for games whose compile fails. " + (s.Careful is { Recorded: 0 } ? "分批使用少量线程编译游戏中录制的管线，让驱动保留更多结果。此游戏尚无录制数据，暂时没有可谨慎编译的内容。"
        : $"分批使用 {ScsKiller.AmdCarefulThreads} 个线程编译游戏中录制的管线，让驱动保留更多结果；其余管线全速编译。"
          + (s.Careful?.Estimate is { } est ? $" 约 {Format.Duration(est)}" + (s.Careful.On || Fmt.CompileTime(s) is not { } fast ? "." : $" instead of {Format.Duration(fast)}.") : ""))
        + (s.Careful?.LaunchCompiled is { } lc ? $" 上次编译后，游戏首次启动仍自行编译了 {lc * 100:0}% 的管线。" : "");
    // AMD: the compiled cache kept aside, in the compile card
    public bool HasKept => s.Kept != null && App.Core.Vendor.Vendor == GpuVendor.Amd && App.Core.Settings.KeepCachesAside;
    public string KeptSize => s.Kept is { } k ? Fmt.KeptSize(k) : "";
    public string KeptState => s.Kept switch
    {
        null => "",
        { Unkept: > 0 and var n, HeldBy: var who } => $"{n} 个文件正被 {who ?? "其他程序"} 使用，未另存。",
        { Frees: > 0 } when s.AntiCheat != AntiCheat.None => "游戏启动后几秒内放回。请保持 SCSKiller 运行。",   // no Play button, VAC keeps it
        { Frees: > 0 } => "驱动删除了其中一部分。按下“开始游戏”时放回。",
        { PutBackAt: { } at, Late: true } => $"已于 {Fmt.When(at)} 放回，当时游戏已启动。部分着色器可能被重新编译了一次。",
        { PutBackAt: { } at } => $"已于 {Fmt.When(at)} 放回，早于游戏启动。",
        _ => "现已在驱动缓存中。",
    };

    // 0 = the global setting, then NewShadersMode's values in order
    public static readonly string[] NewShadersLabels = ["Off", "通知我", "自动编译并通知我", "静默自动编译"];
    public List<string> NewShadersChoices => [$"使用全局设置（{NewShadersLabels[(int)NewShaders.Global(App.Core.Settings)]}）", .. NewShadersLabels];
    public int NewShadersIndex => s.NewShadersOverride is { } m ? (int)m + 1 : 0;
    // 0 = the global setting, 1 = on, 2 = off
    public List<string> RayTracingChoices => [$"默认（{(App.Core.Settings.CompileRayTracing ? "开" : "关")}）", "开", "关"];
    public int RayTracingIndex => s.RayTracingOverride switch { null => 0, true => 1, false => 2 };
    // no plan, or one from before inline ray tracing was counted: unknown, so the choice shows
    public bool ShowRayTracing => P?.RtInline == null || s.RayTracingOverride != null || ScsKiller.HasRayTracing(P);
    // Windows' shader cache of the game, read off the UI thread: Clear cache clears it too, so it can be the only cache to clear.
    BackgroundRead<long>? otherCaches;
    long otherBytes;
    public void ReadCaches() => (otherCaches ??= new(OtherCacheBytes, b => { otherBytes = b; Changed(); })).Request();
    long OtherCacheBytes()
    {
        try { return App.Core.GameCaches(id).Where(p => p.Name != ScsKiller.DriverPart).Sum(p => p.Bytes); }
        catch (Exception) { return 0; }   // e.g. the game is gone after a rescan
    }
    public bool HasCache => s.DiskBytes != null || otherBytes > 0 || s.Status is GameStatus.Warmed or GameStatus.Stale;
    // compiled before a verdict changed: the cache stays until the user clears it
    public string? KeptCacheNote => (s.Status == GameStatus.Unsupported || s.NoStutter != null) && s.WarmedAt != null && s.CacheOnDisk > 0
        ? $"SCSKiller 为此游戏编译了 {DiskValue} 缓存，之后它被标记为{(s.NoStutter != null ? "没有着色器卡顿" : "不受支持")}。"
        : null;
    public bool HasKeptCache => KeptCacheNote != null;

    // Maximum mode suggested for this game on this GPU vendor (Games.MaximumMode), or on for it alone
    public bool? MaximumPending { get; set; }
    public bool MaximumSuggested => s.MaximumSuggestion != null;
    // on for every game in Settings: the per-game switch changes nothing, so it isn't offered
    public bool HasMaximumLine => MaximumSuggested || s.MaximumForGame == true && !App.Core.Settings.MaximumPlans;
    public string MaximumLine => MaximumSuggested
        ? "最大模式对此游戏有效：它还会把着色器的所有两两组合都编译一遍，因此游玩时需要现编的更少。"
        : "此游戏已开启最大模式。编译耗时更长，占用磁盘更多。";
    public string? MaximumNote => string.Join(" ", new[] { s.MaximumSuggestion is { Length: > 0 } note ? note : null,
        MaximumSuggested && s.MaximumAddsBytes is { } adds ? $"在 AMD 上，它最多会给驱动共享的 {Format.Bytes(AmdAppCache.DxcCacheCap)} 缓存增加 {Format.Bytes(adds)}。" : null }.OfType<string>()) is { Length: > 0 } t ? t : null;
    public bool HasMaximumNote => MaximumNote != null;
    public string MaximumAction => MaximumSuggested ? "为此游戏开启" : "Turn off for this game";
    public bool CanSetMaximum => MaximumPending == null && !Queued;
    public bool ShowClearCache => HasCache || CanCompile;
    // as Disk space shows it
    public string ClearCacheText => s.DiskBytes != null ? $"清空缓存（{DiskValue}）" : "清理缓存";
    public string ClearCacheTip => BaInProgressAny ? BaBusyTip : HasCache ? "删除此游戏的着色器缓存（驱动和 Windows 缓存；可选择连游戏自带缓存一起删除），下次启动将重新生成"
        : "SCSKiller 首次编译此游戏或检测到它在运行时，会识别对应的驱动缓存文件";

    // Rate this compile (CompatReport) and what players with a similar system reported (CompatFeed)
    string? ReportWhyNot => CompatReport.WhyNot(s, ScsKiller.PackGpu(App.Core.Vendor.Vendor));
    public bool CanReport => ReportWhyNot == null;
    public string ReportTip => ReportWhyNot ?? "告诉其他玩家 SCSKiller 编译后此游戏的运行情况";
    public bool HasSimilar => s.Similar is SimilarSystems.Helped or SimilarSystems.NoHelp;
    public bool HasSimilarWarning => s.Similar == SimilarSystems.Warning;
    public string SimilarText => s.Similar switch
    {
        SimilarSystems.Helped => "配置相近的玩家反馈：编译能消除或减轻此游戏的卡顿。",
        SimilarSystems.NoHelp => "配置相近的玩家反馈：编译对此游戏没有帮助。",
        SimilarSystems.Warning => $"配置相近的玩家反馈：编译后 {Name} 运行更差。",
        _ => "",
    };

    public bool IsKnownStutter => s.KnownStutter != null;
    public string StutterReason => s.KnownStutter?.Reason ?? "";
    public bool IsNoStutter => s.NoStutter != null;
    public string NoStutterReason => s.NoStutter ?? "";
    /// <summary>The source as a link when it's a URL; otherwise <see cref="StutterCitation"/> shows it as text.</summary>
    public Uri? StutterUri => Uri.TryCreate(s.KnownStutter?.Source, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u : null;
    public string StutterLinkText => StutterUri != null ? $"来源（{s.KnownStutter!.Date}）" : "";
    public string StutterCitation => s.KnownStutter is { } k && StutterUri == null ? $"来源：{k.Source}（{k.Date}）" : "";
    public string EngineChip => s.Engine is { } e ? Format.Engine(e, e.Family == "Unreal" ? "Unreal Engine" : null) + Fmt.Guessed(e) + (e.Fork != null ? " · 自定义分支" : "") : "Unknown engine";
    public string ApiChip => s.Engine?.GraphicsApi.Replace("D3D", "DirectX ") ?? "";
    public bool HasApi => ApiChip.Length > 0 && !ShowApiPicker;

    // DirectX picker: every DirectX game (GameState.ApiDetected), or a choice is kept; the chip's place
    public bool ShowApiPicker => s is { ApiDetected: not null } or { ApiOverride: not null } && s.Engine is { Unsupported: null, Encrypted: false };
    public bool? ApiPending { get; set; }
    public bool CanPickApi => ApiPending == null && !App.Core.Queue.Any(q => q.GameId == id && Format.Running(q));   // its plan check too: the core refuses a change meanwhile
    public int ApiIndex => s.ApiOverride switch { DxApi.D3D11 => 1, DxApi.D3D12 => 2, _ => 0 };
    string[]? apiItems;
    public string[] ApiItems
    {
        get
        {
            var auto = s.Engine?.GraphicsApi.EndsWith(ScsKiller.RecordingSuffix, StringComparison.Ordinal) == true ? $"自动（依你的录制：{Dx(s.Engine.GraphicsApi)}）"
                : s.ApiDetected == SCSKiller.Core.Unreal.UnrealRhi.Ambiguous ? "自动（DirectX 11 和 12）"
                : s.ApiDetected is { } d ? $"自动（{Dx(d)}）" : "Auto";
            if (apiItems?[0] != auto) apiItems = [auto, "DirectX 11", "DirectX 12"];   // the same array while the label holds: a new one resets the selection
            return apiItems;
        }
    }
    static string Dx(string api) => api.Contains("D3D12") ? "DirectX 12" : "DirectX 11";
    public string? ApiOverruled => ScsKiller.ApiOverruled(s);
    public bool HasApiOverruled => ApiOverruled != null;
    public string AntiCheatChip => NoAntiCheat ? "未检测到反作弊" : ScsKiller.AntiCheatName(s);
    public string AntiCheatTip => NoAntiCheat ? "未发现反作弊：可以录制此游戏"
        : s.RiskEligible ? $"此游戏带有 {ScsKiller.AntiCheatName(s)}。录制器仅在你确认的会话中运行，风险自负。"
        : $"{ScsKiller.AntiCheatName(s)} treats an extra {RecorderDll} as tampering: this game is never recorded";
    public bool HasMiddleware => s.Middleware is { Count: > 0 };
    public bool HasModChip => GameRow.ModChip(s) != null;
    public string ModChip => GameRow.ModChip(s)?.Text ?? "";
    public string ModTip => GameRow.ModChip(s)?.Tip ?? "";
    public bool HasDeliveryChip => ScsKiller.DeliveryChip(s) != null;
    public string DeliveryChip => ScsKiller.DeliveryChip(s) ?? "";
    public string DeliveryTip => ScsKiller.DeliveryTip;
    IReadOnlyList<MiddlewareTag> Mw => s.Middleware ?? [];
    /// <summary>"Also compiles" only names tags with known pipelines (and DLSS, by the driver); else what was detected.</summary>
    public string MiddlewareChip => Mw.Where(t => t.Pipelines > 0).Select(t => t.Label)
            .Concat(Mw.Any(t => t.Label == "DLSS") ? ["DLSS（由驱动编译）"] : []).ToList() is { Count: > 0 } also
        ? "同时编译：" + string.Join(" · ", also) : "已检测到：" + string.Join(" · ", Mw.Select(t => t.Label));
    public string MiddlewareTip => "游戏目录中的超分辨率组件 DLL。任意游戏的录制识别该 DLL 版本后，就能随游戏一起编译其管线"
        + "；本机录制可识别，FSR 和 XeSS 也可使用其他玩家分享的数据"
        + (s.Plan is { MiddlewareItems: > 0 } p ? $"（本次计划包含 {Fmt.N(p.MiddlewareItems)} 条）" : "") + ".\n\n"
        + string.Join("\n", Mw.Select(Fmt.TagTip));

    // The one tip left: a game that can't compile before a recording, or not its ray tracing (a plan's other gaps are the
    // coverage card's "what's left").
    public bool HasRecordTip => CanRecord && (s.Status == GameStatus.NeedsRecording || RtAfter) && !RecordOn && s.NoStutter == null;
    bool Enough => ScsKiller.RecordedEnough(s);
    public string RecordTipTitle => Enough ? "继续录制游玩过程" : "录制约 5 分钟游玩过程";
    public string RecordTip => RtPartial || RtAfter ? $"{TurnOnAndPlay} with ray tracing on{(Enough ? "" : " for about 5 minutes")}. " + ScsKiller.RtWhy(App.Core.Vendor.Caps, s.Engine)
            + (RtAfter ? "其他部分已编译。" : "其余部分已能直接从游戏文件编译。")
        : Enough ? Sentence(s.StatusReason) + "."
        : $"{TurnOnAndPlay} as usual for about 5 minutes. SCSKiller learns this game's shader layout from it, then it can compile.";
    // a collection's other game is recorded by its first game's recorder, switched on that game's page
    string TurnOnAndPlay => Collections.RecordedBy(s.Game.Id) is { } by ? $"在 {Collections.Member(by)!.Value.Game.Name} 的页面开启录制，然后游玩此游戏" : "Turn on recording and play";
    public bool ShowRecordAction => !RecordOn && CanToggleRecord && NoAntiCheat;   // an anti-cheat game records only through its session card

    // Shader coverage (PlanStats), in words: how much of what the planner found gets compiled, where it comes from, what's
    // left and what helps; every count in Details. No plan yet on a game that can compile: the card says when it's measured.
    PlanStats? P => s.Plan;
    // a compile that didn't help: the ring is greyed out, nothing of it counts for the game
    int? Pct => DriverChanged ? null : ScsKiller.ShownPercent(s);
    // the last compile filled the old driver's cache, which the new driver doesn't use
    bool DriverChanged => s.Status == GameStatus.Stale && s.StatusReason.StartsWith("driver changed:", StringComparison.Ordinal);
    public bool HasCoverageCard => ScsKiller.ShowsCoverage(s) || CanCompile;
    public bool HasPlan => P != null;
    public bool HasMeter => Pct != null;
    public bool LowCoverage => !s.CompileUnreached && (Pct is { } pct ? pct < 90 : Partial);   // more than 10% left out, like ScsKiller.IsPartial (the test for a plan without the count)
    public bool GoodCoverage => HasMeter && !LowCoverage;
    bool Dx11Only => P is { StageSets: 0, Generated: 0, D3D11Shaders: > 0 };
    public string CoverageHeadline => P == null ? "首次编译开始后即可得知覆盖情况"
        : s.CompileUnreached ? "编译结果未被此游戏使用"
        : DriverChanged ? $"上次编译是针对驱动 {s.WarmedDriverVersion}"
        : Dx11Only && Pct is null or 100 ? (Compiled ? "Covers" : "将覆盖") + "游戏文件中的全部 DirectX 11 着色器" + (SCSKiller.Core.Planning.Planner.D3D11CoversVs(App.Core.Vendor.Caps) || P.D3D11Draws > 0 ? "" : " but the vertex shaders")
        : Pct is not { } pct ? "下次编译时重新测量"
        : (Compiled || s.NewOnly ? "Covers" : s.WarmedAt != null ? "Its last compile covered" : "将覆盖")
          + (pct == 100 ? "此游戏已发现的全部着色器组合" : $"此游戏已发现的约 {pct}% 着色器组合");
    bool Rt => ScsKiller.NeedsRtRecording(s);
    bool RtUnseen => s.RtUnseen;
    bool RtInline => ScsKiller.RtInlineCovers(P);
    bool Compiled => s.WarmedAt != null && s.Status != GameStatus.Stale;

    /// <summary>Where the plan's pipelines come from, in a player's words; the recording's row carries the community note,
    /// and without one the community database's status is its own row. Ray tracing: covered or what it needs.</summary>
    public IReadOnlyList<DetailRow> Sources => P is not { } p ? [] : new DetailRow?[]
    {
        p.Generated > 0 ? new(ScsKiller.BothApis(p) ? "DirectX 12：来自游戏文件" : "来自游戏文件", Fmt.N(p.Generated)) : null,
        p.Recorded > 0 ? new(RecordingSource, Fmt.N(p.Recorded), CommunityLine) : null,
        p.CacheBudgetLeftOut is > 0 and var left ? new(ScsKiller.CacheBudgetTitle(App.Core.Vendor.Caps), Fmt.N(left), ScsKiller.CacheBudgetNote(App.Core.Vendor.Caps)) : null,
        s.Official is { } o ? new("官方录制", $"{o.Psos:N0} 条管线", $"由 SCSKiller 为本版本发布，下载于 {o.DownloadedAt.LocalDateTime:d}") : null,
        s.Community == null && s.InCommunityDb is { } inDb && !HasDbTeaser ? new("上游社区数据库", inDb ? "已有此版本的录制数据" : "尚无此版本的数据") : null,
        p.MiddlewareItems > 0 ? new(p.MiddlewareSharedItems == 0 ? "超分辨率组件：来自录制"
            : p.MiddlewareSharedItems == p.MiddlewareItems ? "超分辨率组件：来自共享数据包" : "超分辨率组件：来自录制与共享数据包", Fmt.N(p.MiddlewareItems)) : null,
        p.D3D11Shaders > 0 ? new(ScsKiller.BothApis(p) ? "DirectX 11：来自游戏文件" : "DirectX 11 着色器", Fmt.N(p.D3D11Shaders)) : null,
        p.RtLibraries > 0 && s.RtSkipped ? new("光线追踪", "skipped (your setting)")
        : p.RtLibraries > 0 ? new("光线追踪效果", s.Engine?.NoRayTracing == true ? "此游戏未启用" : s.Engine?.NoRtPipelines == true ? "内联光追：来自游戏文件" : p.RtUncovered == 0 ? "已覆盖" : s.RtToPlan ? "正在检查" : RtUnseen ? "录制时未观察到" : RtInline ? "已覆盖内联部分" : !Rt ? "大部分已覆盖" : CanRecord ? "需要录制" : "尚未编译") : null,
    }.OfType<DetailRow>().ToList();
    public bool HasSources => Sources.Count > 0;

    /// <summary>The recorded row's label: whose recordings the plan's recorded pipelines come from.</summary>
    string RecordingSource
    {
        get
        {
            if (UsedCommunity == null && s.Official == null) return "本机游玩录制";
            string[] from = [.. (UsedCommunity ?? s.Official)!.WithLocalRecording ? ["你的录制"] : Array.Empty<string>(),
                .. UsedCommunity != null ? ["社区数据库"] : Array.Empty<string>(), .. s.Official != null ? ["the official recording"] : Array.Empty<string>()];
            var text = from.Length == 1 ? from[0] : string.Join(", ", from[..^1]) + " 和 " + from[^1];
            return char.ToUpperInvariant(text[0]) + text[1..];
        }
    }
    /// <summary>The community recording the plan uses: not one that doesn't match the game's files.</summary>
    CommunityInfo? UsedCommunity => s.Community is { OtherVersion: false } c ? c : null;
    /// <summary>The community database's line under the recording row; null = nothing to say.</summary>
    string? CommunityLine => UsedCommunity is { } c
        ? $"已于 {c.DownloadedAt.LocalDateTime:d} 下载社区录制数据" + (s.RecordingSharedAt is { } at ? $" · 本机数据于 {at.LocalDateTime:d} 分享" : "")
        : s.RecordingSharedAt is { } at2 ? $"已于 {at2.LocalDateTime:d} 分享至上游社区数据库" : null;

    // What's left and what helps. LeftOut is mostly Uncovered (root signature slots SCSKiller can't build) or, with a learned
    // lookup, stage sets no recording showed yet (no root signature / template): a longer recording is what helps there.
    // Ray tracing the plan can't compile comes first, in the status's words (ScsKiller.RtNote).
    enum Left { AntiCheat, EngineSlots, UnknownSlots, NotSeen, Recording, PlayedClean, PlayedCompiles, NothingKnown, PlayOnly }
    long Missing => P is { } m ? Math.Max(m.LeftOut, m.Uncovered) : 0;   // a plan without LeftOut: Uncovered only
    Left LeftCase => !CanRecord ? Left.AntiCheat
        : Missing > 0 ? P!.Uncovered * 2 >= Missing ? P.Recorded == 0 ? Left.EngineSlots : Left.UnknownSlots : Left.NotSeen
        : RecordOn ? Left.Recording
        : L is { } l ? PlayCompiles(l) == 0 ? Left.PlayedClean : Left.PlayedCompiles   // what was measured beats what's likely
        : P is { Recorded: > 0 } ? Left.NothingKnown
        : Left.PlayOnly;
    public string LeftText => LeftSentences(tipAsks: false);
    /// <summary>tipAsks: the record tip already gives the ray tracing note and the recording advice.</summary>
    string LeftSentences(bool tipAsks) => string.Join(" ", new string?[]
    {
        RtUnseen ? Sentence(ScsKiller.RtUnseenNote) + "." : RtInline ? Sentence(ScsKiller.RtInlineNote) + "." : !Rt || tipAsks ? null : CanRecord ? (HasDbTeaser ? ScsKiller.RtNeedsRecording : ScsKiller.RtNote(s.InCommunityDb, App.Account.HasDb)) + "."
            : $"光追效果未编译：它们需要一次录制，{(s.RiskEligible ? ScsKiller.RiskNoRecording : !NoAntiCheat ? $"{ScsKiller.AntiCheatName(s)} 会阻止录制" : s.RootUnconfirmed ? ScsKiller.ManualNoRecording : ScsKiller.StreamlineNoRecording)}。",
        LeftCase switch
        {
            Left.AntiCheat => s.RiskEligible ? "其余内容会在你游玩时编译：可在本页开启会话来录制，风险自负。"
                : !NoAntiCheat ? $"其余内容会在你游玩时编译：{ScsKiller.AntiCheatName(s)} 会阻止用于发现它们的录制。"
                : s.RootUnconfirmed ? "其余管线仍会在游玩时编译。请先在上方确认游戏目录，以便录制缺失内容。"
                : "其余管线仍会在游玩时编译。NVIDIA DLSS 的加载顺序使录制器无法收集它们。",
            Left.EngineSlots => ScsKiller.EngineSlotsNote(RecordOn),
            Left.UnknownSlots => "其余部分使用 SCSKiller 暂时无法重建的着色器槽位，因此仍会在游玩时编译。",
            Left.NotSeen => ScsKiller.NotSeenNote(RecordOn),
            Left.Recording => "录制已开启：新收集的内容会在下次编译时加入。",
            Left.PlayedCompiles => $"上次游玩仍有 {PlayCompiles(L!):N0} 条管线临时编译。" + (tipAsks ? "" : "开启录制可收集它们。"),
            _ when Rt || RtUnseen || RtInline => null,   // ray tracing is what's left
            Left.PlayedClean => "上次游玩未发现缺失的管线。",
            Left.NothingKnown => "已知内容没有缺失。",
            _ => "少数效果只在游玩时组合生成。" + (tipAsks ? "" : "短时间录制即可收集。"),
        },
        !Guessed ? null : tipAsks ? "部分着色器状态仍是推测值。" : "Some shader states are guesses. A longer recording makes them exact.",
    }.OfType<string>());
    // per-stage plans (AMD): the planner warns that a wrong guess costs a compile in game
    bool Guessed => P is { } g && g.GuessedUnits > 0.1 * (g.ExactUnits + g.InferredUnits + g.GuessedUnits);
    // not twice: a game that needs a recording has the button in its tip
    public bool ShowLeftRecord => ShowRecordAction && !HasRecordTip && s.NoStutter == null && (LeftCase is Left.EngineSlots or Left.NotSeen or Left.PlayedCompiles or Left.PlayOnly || Guessed || Rt || RtUnseen || RtInline);
    // before the first compile there's nothing left yet. Recording on: what's missing comes from playing, without the button
    public bool ShowLeftCallout => HasPlan && (ShowLeftRecord || RecordOn && s.NoStutter == null && LeftCase is Left.NotSeen or Left.EngineSlots);
    public string LeftTitle => Rt || RtUnseen ? "光追效果尚未编译" : "部分着色器仍会在游玩时编译";

    public string CoverageValue => Pct is { } pct ? $"{pct}%" : Dx11Only && !s.CompileUnreached ? "All" : Format.Dash;
    public string CoverageLabel => s.CompileUnreached ? "未生效" : Pct != null || Dx11Only ? "已覆盖" : "尚无数据";
    // A geometry can't be shared between two Paths: each arc gets its own (120 px box, 12 px stroke).
    public Geometry? GoodArc => GoodCoverage ? Arc() : null;
    public Geometry? LowArc => LowCoverage ? Arc() : null;
    Geometry Arc()
    {
        const double c = 60, r = 54;
        double a = Math.Min(Pct ?? 0, 99.99) / 100 * 2 * Math.PI;
        var figure = new PathFigure { StartPoint = new(c, c - r) };
        figure.Segments.Add(new ArcSegment { Point = new(c + r * Math.Sin(a), c - r * Math.Cos(a)), Size = new(r, r), IsLargeArc = a > Math.PI, SweepDirection = SweepDirection.Clockwise });
        var g = new PathGeometry();
        g.Figures.Add(figure);
        return g;
    }
    public bool HasTimeValue => Fmt.CompileTime(s) != null;
    public string TimeLabel => s.LastWarmTime != null ? "编译耗时" : "预计编译耗时";
    public string TimeValue => s.LastWarmTime is { TotalMinutes: >= 1, TotalHours: < 1 } t ? $"{t.Minutes} 分 {t.Seconds} 秒"   // measured: to the second
        : Format.Duration(Fmt.CompileTime(s));
    public bool HasDiskValue => s.DiskBytes != null || s.EstimatedCacheBytes != null;
    public string DiskLabel => s.DiskBytes != null ? "磁盘占用" : "预计磁盘占用";
    public string DiskValue => Format.Bytes(OnDisk ?? s.EstimatedCacheBytes);
    // the kept-aside copy of what the driver removed takes disk space too
    long? OnDisk => s.DiskBytes + (s.Kept?.Frees ?? 0);
    public bool HasShaderCount => s.ShaderCount != null;
    public string ShaderCount => Fmt.N(s.ShaderCount);
    // measured frames when the recorder timed them, else the compile count
    long? StutterCount => F is { } f ? Format.NoPlay(f.Low1PctFps) ? null : ShaderHitches.Count : L is { } l ? PlayCompiles(l) : null;
    public bool HasStutterTile => HasSession && StutterCount != null;
    public string StuttersValue => StutterCount is not { } n ? "" : n == 0 ? "None" : Fmt.N(n);
    public bool HasStutters => StutterCount > 0;
    public bool NoStutters => StutterCount == 0;
    // without frame times the count is of compiles, which may or may not have stuttered
    public string StuttersLabel => F != null ? "上次游玩的卡顿次数" : "游玩期间编译的管线";

    /// <summary>Every count, for power users.</summary>
    public IReadOnlyList<DetailRow> Details => new DetailRow?[]
    {
        P is { } p ? new("计划中的管线", Fmt.N(p.Recorded + p.Generated + p.MiddlewareItems)) : null,
        s.ShaderCount != null ? new("游戏文件中的着色器", Fmt.N(s.ShaderCount)) : null,
        P is { StageSets: > 0 } ? new("发现的着色器组合", Fmt.N(P.StageSets)) : null,
        P is { LeftOut: > 0 } ? new("未纳入计划", ScsKiller.LeftOutValue(P)) : null,
        P is { StageSets: 0, Uncovered: > 0 } ? new("因着色器槽位无法重建而遗漏", Fmt.N(P.Uncovered)) : null,
        P != null ? new("已录制的管线", Fmt.N(P.Recorded)) : null,
        P != null ? new("从游戏文件构建", Fmt.N(P.Generated)) : null,
        P is { MiddlewareItems: > 0 } ? new("超分辨率组件管线", Fmt.N(P.MiddlewareItems)) : null,
        P is { D3D11Shaders: > 0 } ? new("DirectX 11 着色器", Fmt.N(P.D3D11Shaders)) : null,
        P is { RtLibraries: > 0 } ? new("光追着色器库", $"{P.RtLibraries:N0}" + (s.Engine?.NoRtPipelines == true ? "（游戏未使用）" : s.RtSkipped ? " (skipped, your setting)" : P.RtUncovered > 0 ? $"（{P.RtUncovered:N0} 条未编译）" : "")) : null,
        P is { RtInline: > 0 } ? new("内联光追着色器", Fmt.N(P.RtInline)) : null,
        s.InCommunityDb is { } db && !HasDbTeaser ? new("适用于你的版本的社区数据", db ? $"{s.CommunityDbPsos:N0} 条管线" : "暂无") : null,
        s.Community is { } c ? new("已下载的社区录制", c.OtherVersion ? "与你的游戏文件不匹配" : $"{c.Psos:N0} 条管线") : null,
        s.Official is { } o ? new("官方录制", $"{o.Psos:N0} 条管线") : null,
        P != null ? new("合成的管线模板", Fmt.N(P.SynthesizedTemplates)) : null,
        P != null ? new("着色器布局", $"{P.RootSignatures:N0} · {RootSigSource(P)}") : null,
        P is { } u && u.ExactUnits + u.InferredUnits + u.GuessedUnits > 0 ? new("阶段单元：精确 / 推断 / 推测", $"{u.ExactUnits:N0} / {u.InferredUnits:N0} / {u.GuessedUnits:N0}") : null,
        P is { LayoutCoverage: > 0 } ? new("录制得到的顶点布局覆盖率", $"{P.LayoutCoverage:P1}") : null,
        s.CacheOnDisk is { } disk ? new("驱动缓存", Format.Bytes(disk) + "（实测）") : s.EstimatedCacheBytes is { } est ? new("驱动缓存", "≈ " + Format.Bytes(est) + "（估计）") : null,
        s.WindowsCache is { } w ? new(ScsKiller.WindowsPart, Format.Bytes(w)) : null,
        s.DeliveredShaders is { } asd ? new("由高级着色器交付（ASD）提供的着色器", Format.Bytes(asd)) : null,
    }.OfType<DetailRow>().ToList();


    // "Shipped with the shaders" is the carver's case.
    string RootSigSource(PlanStats p) =>
        p.RootSigRuleVerified ? "已在此引擎版本验证"
        : p.RootSignatures == 0 ? "不需要（DirectX 11）"
        : s.Engine?.Family == "Unreal" ? (p.Recorded > 0 ? "从录制中学到" : "not tested on this engine version yet")
        : p.Recorded > 0 ? "来自录制" : "随着色器提供";

    // Last compile: when, how long, what failed or was skipped and what to do; never compiled: what the first one costs.
    public bool HasCompileCard => CanCompile || s.LastWarmTime != null;
    public string CompileTitle => s.LastWarmTime == null ? "首次编译" : "上次编译";
    long Failed => s.LastWarmFailed ?? 0;
    long Skipped => s.LastWarmSkipped ?? 0;
    long Crashed => s.LastWarmCrashed ?? 0;
    long DriverSkipped => s.LastWarmDriverSkipped ?? 0;
    long Unbuilt => s.LastWarmUnbuilt ?? 0;
    public string CompileText => s.WarmedAt is { } at
        ? $"编译时间：{Fmt.When(at)}" + (s.LastWarmTime is { } t ? $" in {Format.Duration(t)}" : "") + $"；适用驱动 {s.WarmedDriverVersion}。"
          + (ScsKiller.NothingFailed(s) ? "没有失败项。" : "")   // null = not known (a warm from before these were kept): no claim
          + (ScsKiller.RefusedLine(s) is { } refused ? " " + refused : "")
          + (OnDisk is { } c ? $"占用磁盘 {Format.Bytes(c)}。" : "")
          + " " + OwnShaderStep
        : s.LastWarmTime is { } last ? $"游戏着色器缓存已被清除。上次编译耗时 {Format.Duration(last)}。"
        : (s.EstimatedWarmTime is { } est ? $"It takes about {Format.Duration(est)} and runs in the background, so you can keep using the PC." : "编译在后台运行，可继续使用电脑。")
          + (s.EstimatedCacheBytes is { } b ? $"预计增加约 {Format.Bytes(b)} 驱动着色器缓存。" : "");
    // no game file says whether a game runs its own precompile at startup: said on every compiled game's page, not its row
    public const string OwnShaderStep = "游戏启动时仍可能执行自己的着色器步骤。";
    public string SkippedTitle => $"{Skipped + Crashed + DriverSkipped + Unbuilt:N0} skipped";
    long NeedsRecording => Math.Min(Skipped, s.LastWarmNeedsRecording ?? 0);   // of Skipped: flagged by the community recording
    long NotInGame => Skipped - NeedsRecording;
    public string? SkippedText => s.WarmedAt == null || Skipped + Crashed + DriverSkipped + Unbuilt == 0 ? null
        : string.Join(" ", new[]
        {
            NotInGame > 0 ? $"{NotInGame:N0} 条已跳过：其着色器不在此版本中。无需处理。" : null,
            Crashed > 0 ? $"{Crashed:N0} 条已跳过：会导致显卡驱动崩溃。游戏用到时会自行编译。" : null,
            DriverSkipped > 0 ? $"{DriverSkipped:N0} 条本次跳过：显卡驱动在处理它们或它们的依赖项时崩溃或卡死。" : null,
            Unbuilt > 0 ? $"{Unbuilt:N0} 条已跳过：SCSKiller 暂时无法构建。" : null,
            NeedsRecording > 0 ? $"{NeedsRecording:N0} 条需要在本机录制：游戏或某个 mod 在运行时才生成这些着色器，因此不在任何游戏文件中。请开启“游玩时录制”游玩一次以捕获它们。" : null,
        }.OfType<string>());
    public bool HasSkipped => SkippedText != null;

    // Last play session (SessionStats, no timestamp): one bar, three segments, under the Record switch. Ready = from the
    // game's library or the driver cache; ray tracing state objects have their own line, so Requests isn't used.
    SessionStats? L => s.LastSession;
    public bool HasSession => L != null;
    public bool ShowSessionHint => L == null && CanRecord;
    public string SessionHeader => L is { } l ? $"上次游玩 · {Format.Duration(F?.Duration ?? l.Duration)}" : "";
    // ray tracing state objects included, so the headline agrees with the ray tracing line under it
    static long PlayCompiles(SessionStats l) => l.Compiles + l.StateObjectsCompiled;
    public string SessionVerdict
    {
        get
        {
            if (L is not { } l) return "";
            long play = PlayCompiles(l), started = l.StartupCompiles + l.StateObjectsStartupCompiled, ready = l.FromGameLibrary + l.CacheHits + l.StateObjectsReady;
            if (play + started == 0)
                return ready > 0 ? $"游玩期间没有临时编译：游戏使用的 {ready:N0} 个着色器组合均已准备好。" : "游玩期间没有临时编译。";
            // the longest compile call, of the PSOs only; whether a frame stuttered is the frame report's to say
            string worst = l.StateObjectsCompiled > 0 ? "" : play == 1 ? $"（耗时 {l.WorstCompileMs:0} 毫秒）" : $"（最慢一条耗时 {l.WorstCompileMs:0} 毫秒）";
            return (play == 0 ? "游玩期间没有临时编译。" : $"{play:N0} had to compile while you played{worst}.")
                + (started > 0 ? $"启动或加载期间另编译了 {started:N0} 条。" : "")
                + (ready > 0 ? $"其余 {ready:N0} 条已准备好。" : "");
        }
    }
    public bool HasStateObjectLine => L is { } l && l.StateObjectsReady + l.StateObjectsCompiled + l.StateObjectsStartupCompiled > 0;
    public string StateObjectLine => L is { } l ? $"光追管线：{l.StateObjectsReady:N0} 条已准备好，{l.StateObjectsCompiled:N0} 条在游玩时编译"
        + (l.StateObjectsStartupCompiled > 0 ? $"，{l.StateObjectsStartupCompiled:N0} 条在启动或加载时编译。" : ".") : "";
    public bool HasRayQueryLine => L?.RayQueryRecompiles > 0;
    public string RayQueryLine => L is { RayQueryRecompiles: > 0 and var n }
        ? $"{n:N0} 条光追管线每次启动都会被驱动部分重新编译。" : "";

    // The last launch's frame times (FrameLog): its shader stutters, other hitches and loading, the 1% low of play.
    public FrameReport? F => s.LastFrames;
    List<Hitch> ShaderHitches => F?.Hitches.Where(h => h.Cause == HitchCause.Shader).ToList() ?? [];
    public bool HasFrames => F != null;
    public string FramesVerdict => F is not { } f ? "" : Format.Frames(f, ShaderHitches);
    public string GraphEnd => Format.Duration(F?.Duration);
    public string FramesSummary => F is not { } f ? ""
        : $"启动阶段（游戏自身的着色器预编译与首次加载）：{Format.Duration(f.Startup)}。游玩中：{InPlay(f).Count(h => h.Ms >= 50):N0} 帧达到 50 毫秒以上，"
          + $"{InPlay(f).Count(h => h.Ms >= 100):N0} of 100 ms or more. Frame times stay on this PC.";
    // the same list while the report is the same: a new one makes the slow-frames list rebuild every row at each refresh
    public IReadOnlyList<DetailRow> HitchRows => hitchRows.Report == F ? hitchRows.Rows
        : (hitchRows = (F, F?.Hitches.Select(h => new DetailRow($"{Clock(h.At)}  {Cause(h.Cause)}", Ms(h.Ms))).ToList() ?? [])).Rows;
    (FrameReport? Report, IReadOnlyList<DetailRow> Rows) hitchRows = (null, []);
    public static string HitchTip(Hitch h, bool neutral = false) => $"{Clock(h.At)} · {Ms(h.Ms)} · {(neutral ? "卡顿" : Cause(h.Cause))}";
    static string Clock(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    public bool HasHitchRows => F?.Hitches.Count > 0;

    // Earlier launches with frame times (FrameSessions), newest first; the one the section shows is left out. Read again
    // only when the game's state changes.
    public IReadOnlyList<DetailRow> EarlierSessions => earlier.For == s ? earlier.Rows
        : (earlier = (s, App.Core.FrameHistory(id).Where(e => e.LaunchUnixMs != F?.LaunchUnixMs).Select(SessionRow).ToList())).Rows;
    (GameState? For, IReadOnlyList<DetailRow> Rows) earlier = (null, []);
    public bool HasEarlierSessions => EarlierSessions.Count > 0;
    // a cleared recording takes the last launch's frames, not the history
    public bool HasFramesCard => HasFrames || HasEarlierSessions;
    static DetailRow SessionRow(FrameSession e) => new(
        $"{DateTimeOffset.FromUnixTimeMilliseconds(e.LaunchUnixMs).LocalDateTime:g} · {Format.Duration(e.Duration)}",
        Format.Session(e));

    // the frames of play, between startup and quitting, whatever slowed them (a load in play too)
    static IEnumerable<Hitch> InPlay(FrameReport f) => f.Hitches.Where(h => FrameLog.InPlay(h.At.TotalMilliseconds, h.Ms, f.Startup.TotalMilliseconds) && h.Cause != HitchCause.Quitting);
    static string Ms(double ms) => Format.Ms(ms);
    static string Cause(HitchCause c) => c switch { HitchCause.Shader => "着色器编译", HitchCause.Other => "其他卡顿", HitchCause.Quitting => "退出中",
        HitchCause.LoadingShaders => "加载时编译着色器", _ => "加载中" };

    public GridLength LibraryWidth => new(L?.FromGameLibrary ?? 0, GridUnitType.Star);
    public GridLength HitsWidth => new(L?.CacheHits ?? 0, GridUnitType.Star);
    public GridLength CompilesWidth => new(L?.Compiles ?? 0, GridUnitType.Star);
    public double CompilesMinWidth => L?.Compiles > 0 ? 4 : 0;
    public string FromLibrary => Fmt.N(L?.FromGameLibrary);
    public string CacheHits => Fmt.N(L?.CacheHits);
    public string Compiles => Fmt.N(L?.Compiles);
    public string SessionLabel => L is { } l
        ? $"{l.Requests:N0} requests: {l.FromGameLibrary:N0} from the game's own library, {l.CacheHits:N0} already in the driver cache, {l.Compiles:N0} compiled during play"
          + (l.StartupCompiles > 0 ? $"，{l.StartupCompiles:N0} 条在启动或加载时编译" : "")
          + (l.RayQueryRecompiles > 0 ? $"，{l.RayQueryRecompiles:N0} 条光追管线每次启动时被驱动部分重新编译" : "")
          + (l.StateObjectsReady + l.StateObjectsCompiled + l.StateObjectsStartupCompiled > 0 ? $"。光追管线：{l.StateObjectsReady:N0} 条已就绪，{l.StateObjectsCompiled:N0} 条在游玩中编译" : "")
          + (l.StateObjectsStartupCompiled > 0 ? $"，{l.StateObjectsStartupCompiled:N0} 条在启动或加载时编译" : "")
        : "";

    public bool? RecordPending { get; set; }   // the state a recorder change that is running (off the UI thread) asked for
    // NVIDIA, DirectX 11, during a before and after pair: the recorder is in, but records nothing
    bool PairFramesOnly => s.Records11 && ScsKiller.Frames11(s.Engine, App.Core.Vendor.Caps);
    public bool RecordOn => ShowOffline ? OfflineOn : !PairFramesOnly && (RecordPending ?? s.RecorderEffective);
    public bool CanToggleRecord => ShowOffline ? OfflinePending == null && !s.OfflineRunning
        : RecordPending == null && AlongsidePending == null && (NoAntiCheat || s.AntiCheatRecordingAllowed) && s.RecorderSkip == null && !PairFramesOnly;
    public bool ShowUseDefault => RecordPending == null && s.RecorderOverride != RecorderOverride.Default && s.RecorderSkip == null && !PairFramesOnly && !s.RecorderForeign;
    public string UseDefaultText => $"使用默认（{(App.Core.Settings.RecordAllGames && s.NoStutter == null ? "开" : "关")}）";
    string RecorderDll => Fmt.RecorderDll(s);
    string Kept => ScsKiller.SessionKeptNote(s) is { } kept ? " " + kept : "";
    public string RecordNote => ScsKiller.CrashNote(s.Game) is { } crash ? crash
        : Risk
        ? "每次会话开始前都会确认。" + Kept
        : ShowOffline
        ? $"{ScsKiller.AntiCheatName(s)} blocks the recorder, but you can enable it at your own risk with an offline session." + Kept
        : s.RiskNoLaunch ? ScsKiller.NoLaunchNote(Fmt.StoreName(s.Game))
        : !NoAntiCheat
        ? s.AntiCheatRecordingAllowed
            ? $"录制器已放入，因为你在 {ScsKiller.AntiCheatName(s)} 旁允许了它：录制方式与无作弊游戏相同，风险自负。" + Kept
            : $"不可用：{ScsKiller.AntiCheatName(s)} 会把额外的 {RecorderDll} 视为篡改，因此此游戏只能靠其文件编译。"
        : PairFramesOnly ? "仅记录帧时间，用于前后对比录制。"
        : s.RecorderSkip == ScsKiller.SkipManual ? "在你确认游戏目录前不可用（上方“游戏目录…”）：录制前 SCSKiller 会检查整个目录是否存在反作弊。"
        : s.RecorderSkip == ScsKiller.SkipModNotChainable ? $"不可用：{ScsKiller.NotChainableReason(s.RecorderMod, RecorderDll)}。"
        : s.RecorderSkip is { } skip ? $"不可用：{skip}。"
        : $"在游戏旁放入一个小的 {RecorderDll}，用来捕获计划遗漏的内容并为每帧计时，从而在本页显示卡顿点。可随时移除。如果游戏带有 SCSKiller 未检测到的反作弊，请不要开启。"
          + (s.RecorderNote is { } note ? $"（{Sentence(note)}）" : "")
          + (s.RecorderRefused is { } why && !(s.Status == GameStatus.NeedsRecording && ScsKiller.NeverRecorded(s)) ? " " + why : "");   // else the status says it

    // A mod's d3d12.dll where the recorder goes (ReShade, a wrapper): off = the game isn't recorded; on = the recorder chains to it
    public bool HasMod => s.RecorderMod != null && s.RecorderSkip is not (ScsKiller.SkipAntiCheat or ScsKiller.SkipShaderMod or ScsKiller.SkipManual or ScsKiller.SkipUnsupported or ScsKiller.SkipNotDx12 or ScsKiller.SkipStreamline or ScsKiller.SkipCrashes);
    public string AlongsideTitle => $"与 {s.RecorderMod} 一同录制";
    public string AlongsideNote => $"{s.RecorderMod}'s d3d12.dll is renamed while the recorder is in, and restored exactly as it was when it's removed.";
    public bool? AlongsidePending { get; set; }
    public bool AlongsideOn => AlongsidePending ?? s.RecordAlongsideMod;
    public bool CanToggleAlongside => AlongsidePending == null && RecordPending == null;

    // NoACGate: this game's own permission for the plain recorder beside its anti-cheat (IScsKiller.SetAntiCheatRecording).
    // Off by default; on, the recorder goes in as it does for a clean game (no session, no pid binding) and the verdict stays
    // shown. Not offered for VAC: the core refuses it there whatever this says.
    public bool HasAntiCheat => s.AntiCheat != AntiCheat.None;
    public bool? AntiCheatRecordPending { get; set; }
    public bool AntiCheatRecordOn => AntiCheatRecordPending ?? s.AntiCheatRecordingAllowed;
    public bool CanToggleAntiCheatRecord => AntiCheatRecordPending == null && RecordPending == null && !s.RootUnconfirmed
        && (s.AntiCheatRecordingAllowed || s.RecorderSkip == ScsKiller.SkipAntiCheat);   // on, or the anti-cheat is all that blocks it
    public string AntiCheatRecordTitle => $"在有反作弊的游戏中录制（{ScsKiller.AntiCheatName(s)}）";
    public string AntiCheatRecordNote => !App.Core.RecorderAcceptsAntiCheat
        ? $"此副本的录制器（native\\d3d12.dll）是改动前的旧版：无论这里写入什么，它都会拒绝带反作弊的游戏。请替换为重新构建的版本后再开启。"
        : s.AntiCheatRecordingAllowed
            ? "录制器会留在反作弊旁边，像无作弊游戏那样录制。若反作弊会检查自身旁边的文件，可能将其视为篡改。"
            : $"{ScsKiller.AntiCheatName(s)} treats an extra {RecorderDll} as tampering, so the game is compiled from its files only. Turned on, the recorder goes in anyway, at your own risk.";

    // An eligible EasyAntiCheat game (Games.OfflineEac) or an anti-cheat game allowed in Settings (RiskEligible): the card's
    // switch allows sessions, each confirmed before it starts
    public bool ShowOffline => s.OfflineEligible || s.RiskEligible;
    public bool Risk => s.RiskEligible;
    public bool? OfflinePending { get; set; }
    public bool OfflineOn => OfflinePending ?? s.OfflineRecord;
    public bool ShowOfflineOn => ShowOffline && OfflineOn;
    public bool OfflineStarting { get; set; }
    public bool CanStartOffline => s.OfflineRecord && OfflinePending == null && !s.OfflineRunning && !s.Playing && !OfflineStarting;
    public string OfflineButtonText => Risk
        ? s.OfflineRunning ? "会话进行中…" : "Record a session (at your own risk)"
        : s.OfflineRunning ? "离线会话运行中…" : "离线启动并录制（自行承担风险）";
    public string OfflineRiskText => Risk ? RiskText(s) : OfflineRisk;
    public const string OfflineRisk = "游戏将以不加载 EasyAntiCheat 的离线模式启动。退出游戏后 SCSKiller 会移除相关文件；如果游戏崩溃，则会在下次登录时清理。"
        + "下次登录时）。若联网游玩时仍有残留，你可能被封禁。Steam 必须保持运行。会话期间不要卸载 SCSKiller。";
    public static string RiskText(GameState g) => $"此游戏带有 {ScsKiller.AntiCheatName(g)}。启用录制器的风险由你自负。"
        + "若账号被封，SCSKiller 不承担责任。";
    public string? SessionNote => s.SessionNote;
    public bool HasSessionNote => s.SessionNote != null;

    public bool HasRecording => s.RecordingBytes > 0;
    public string RecordingSize => $"录制数据占用 {Format.Bytes(s.RecordingBytes)}（游戏目录与 SCSKiller 副本合计）";
    public bool RecordingPaused => s.RecordingPaused;
    public string RecordingPausedText => ScsKiller.PausedNote(App.Core.Settings) + "。请在设置中提高上限，或清除录制。";
    public bool ClearingRecording { get; set; }
    public bool CanClearRecording => !ClearingRecording && !s.Playing;
    public string ClearRecordingTip => s.Playing ? "请先退出游戏" : "删除已录制的管线，录制器会留在";

    public bool Playing => s.Playing;
    public bool Refreshing { get; private set; }
    public bool CanRefresh => !Refreshing;
    public async void RefreshGame()
    {
        if (Refreshing) return;
        Refreshing = true;
        Changed();
        try { await Task.Run(() => App.Core.RefreshGame(id)); Error = null; }
        catch (Exception e) { Error = e.Message; }
        Refreshing = false;
        ReadCaches();
        Refresh();
    }
}


/// <summary>A label and its value (the detail page's sources and Details); Note: a line under the label.</summary>
public sealed record DetailRow(string Label, string Value, string? Note = null)
{
    public bool HasNote => Note != null;
}

/// <summary>A queue row: the running item (1), a waiting item (2..) or a finished one (Number 0).</summary>
public sealed record QueueRow(string Id, int Number, string Name, string Exe, string Note, string Time, string Cache, QueueStage Stage,
    bool First = false, bool Last = false)
{
    public ImageSource? Icon { get; } = Icons.Get(Exe);   // a field: an icon that failed makes the row unequal, so Sync replaces it
    public bool NoIcon => Icon is null;
    public Brush Tile => GameRow.TileOf(Name);
    public string Initials => Name.Length > 0 ? GameRow.InitialsOf(Name) : "";
    public bool CanUp => !First;
    public bool CanDown => !Last;
    public string Glyph => Stage switch { QueueStage.Done => "\uE73E", QueueStage.Failed => "\uE7BA", _ => "\uE71A" };   // check, warning, stop
    public string TopLabel => $"将 {Name} 移到队首";
    public string UpLabel => $"将 {Name} 上移";
    public string DownLabel => $"将 {Name} 下移";
    public string RemoveLabel => $"从队列中移除 {Name}";
    public override string ToString() => Number > 0 ? $"{Number}. {Name}, {Note}, {Time}, {Cache}" : $"{Name}, {Note}";   // screen readers
}

public sealed class QueueVm : Bindable
{
    QueueItem? cur;
    long planned;
    List<(string Game, long Bytes)> growth = [];
    List<(long Growth, long Estimate)> sizes = [];
    (CacheUsage Usage, CacheLimit? Limit, long? Capped)? driver;
    readonly BackgroundRead<(CacheUsage, CacheLimit?, long?)> cache;

    public QueueVm() => cache = new(() => (App.Core.Vendor.GetCacheUsage(), App.Core.Vendor.GetCacheLimit(), (App.Core.Vendor as AmdBackend)?.AppCache.DxcBytes()),
        c => { driver = c; ShowCache(); Changed(); });
    Settings S { get => App.Core.Settings; set { App.Core.Settings = value; Changed(); } }

    public ObservableCollection<QueueRow> Waiting { get; } = [];
    public ObservableCollection<QueueRow> Finished { get; } = [];
    public bool Dragging { get; set; }   // don't rebuild Waiting under a drag in progress

    public QueueRow Current { get; private set; } = new("", 1, "", "", "", "", "", QueueStage.Waiting);
    public bool HasCurrent => cur != null;
    public bool Idle => cur == null;
    public bool Empty => cur == null && Waiting.Count == 0;
    public bool HasFinished => Finished.Count > 0;
    public bool CanClear => Waiting.Count > 0 || Finished.Count > 0;
    public bool CanStart => !App.Core.QueueRunning && Waiting.Count > 0;
    public bool CanRaiseLimit => ScsKiller.CanRaiseCacheLimit(App.Core.Vendor.Caps, driver?.Limit);
    public string Summary { get; private set; } = "";
    public string? PlanChecks { get; private set; }   // ScsKiller.PlanCheckLine
    public bool HasPlanChecks => PlanChecks != null;

    public string CurrentDetail => cur is null ? "" : Fmt.Stage(cur) + (Fmt.ProgressText(cur) is { } p ? " · " + p : "");
    public bool Indeterminate => Fmt.Indeterminate(cur);
    public double Percent => Fmt.Percent(cur);
    public string PauseText => cur?.Stage == QueueStage.Paused ? "继续" : "暂停";
    public bool CacheWarn { get; private set; }
    public string CacheWarnText { get; private set; } = "";

    public double Threads { get => Used; set { if ((int)value != Used) S = S with { Threads = (int)value }; } }
    int Used => ScsKiller.UsedThreads(S.Threads);
    public string ThreadsText => $"{Used} of {Environment.ProcessorCount}";
    public double MaxThreads => Environment.ProcessorCount;
    public int Priority { get => (int)S.Priority; set { if (value >= 0 && value != (int)S.Priority) S = S with { Priority = (WarmPriority)value }; } }
    public string CpuSummary => $"CPU 占用：{Used}/{Environment.ProcessorCount} 线程，{(S.Priority == WarmPriority.BelowNormal ? "低于正常" : "空闲")}优先级";

    public void Refresh()
    {
        var core = App.Core;
        var all = core.Queue;
        var queue = all.Where(q => !q.PlanCheck).ToList();
        PlanChecks = ScsKiller.PlanCheckLine(all);
        var games = core.Games.ToDictionary(g => g.Game.Id);

        TimeSpan? Left(QueueItem q) => Fmt.Left(q, games.GetValueOrDefault(q.GameId));
        QueueRow Row(QueueItem q, int number, bool first = false, bool last = false)
        {
            var g = games.GetValueOrDefault(q.GameId);
            var time = q.Stage == QueueStage.Done ? Format.Duration(g?.LastWarmTime) : Left(q) is { } t ? Format.Duration(t) + (q.Progress?.PerSecond > 0 ? " 剩余" : "") : Format.Dash;
            return new(q.GameId, number, g?.Game.Name ?? q.GameId, g?.Game.ExePath ?? "", NoteOf(q, g), time, g is null ? Format.Dash : Fmt.Cache(g), q.Stage, first, last);
        }

        (cur, var waiting) = Fmt.RunOrder(queue);
        if (cur != null) Current = Row(cur, 1);
        int first = cur != null ? 2 : 1;
        if (!Dragging) Sync(Waiting, waiting.Select((q, i) => Row(q, first + i, i == 0, i == waiting.Count - 1)));
        Sync(Finished, queue.Where(q => !Fmt.Active(q)).Select(q => Row(q, 0)));

        var pending = queue.Where(Fmt.Active).ToList();
        var total = TimeSpan.FromTicks(pending.Sum(q => Left(q)?.Ticks ?? 0));
        bool unknown = pending.Any(q => Left(q) is null && !ScsKiller.Stalled(q));
        Summary = pending.Count == 0 ? "队列为空"
            : $"{pending.Count} 个游戏 · "
              + (total > TimeSpan.Zero ? $"总计约 {Format.Duration(total)}{(unknown ? "，另有待生成的计划" : "")}" : "生成计划后可知耗时")
              + (cur == null && !core.QueueRunning ? " · 尚未开始" : "");

        planned = pending.Sum(q => games.GetValueOrDefault(q.GameId)?.EstimatedCacheBytes ?? 0);
        var queued = pending.Select(q => games.GetValueOrDefault(q.GameId)).OfType<GameState>().ToList();
        growth = queued.Select(g => (g.Game.Name, ScsKiller.CacheGrowth(g))).ToList();
        sizes = queued.Select(g => (ScsKiller.CacheGrowth(g), g.EstimatedCacheBytes ?? 0)).ToList();
        ShowCache();
        Changed();
        cache.Request();
    }

    void ShowCache()
    {
        if (driver is not { } d) return;   // not read yet
        CacheWarnText = (d.Capped is { } used
            ? ScsKiller.LargeCompileWarning(sizes, App.Core.Vendor.Vendor) ?? AmdAppCache.QueueWarning(used, growth)   // one game over the cap is always over what's free too
            : ScsKiller.QueueCacheWarning(sizes, d.Usage.BytesOnDisk, planned, d.Limit?.Bytes, App.Core.Vendor.Vendor)) ?? "";
        CacheWarn = CacheWarnText.Length > 0;
    }

    static void Sync(ObservableCollection<QueueRow> target, IEnumerable<QueueRow> rows)
    {
        var list = rows.ToList();
        if (list.SequenceEqual(target)) return;
        target.Clear();
        foreach (var r in list) target.Add(r);
    }

    static string NoteOf(QueueItem q, GameState? g) => q.Stage switch
    {
        QueueStage.Failed => "失败：" + q.Error,
        QueueStage.Stopped when q.Progress is { } p => $"停在 {p.Done:N0}/{p.Total:N0}：再次加入即可继续" + (q.Note is { } n ? " · " + n : ""),
        QueueStage.Stopped => "Stopped",
        QueueStage.Done => q.Note is { } n ? "完成 · " + n : "Done",
        _ when q.Note != null => q.Note,
        _ => g?.Plan is { } plan ? $"{plan.Recorded + plan.Generated + plan.MiddlewareItems:N0} 条管线{(plan.D3D11Shaders > 0 ? $" + {plan.D3D11Shaders:N0} 条 DirectX 11 着色器" : "")}" : "会先生成计划",
    };

    public void PauseOrResume() => Fmt.PauseOrResume(cur);
}

public sealed class SettingsVm : Bindable
{
    public static readonly (string Label, CacheLimit Limit)[] Sizes =
    [
        ("16 GB（驱动默认值）", new(16L << 30, true)), ("32 GB", new(32L << 30, false)),
        ("100 GB", new(100L << 30, false)), ("Unlimited", new(null, false)),
    ];
    public static readonly string[] SizeLabels = Sizes.Select(s => s.Label).ToArray();

    Settings S { get => App.Core.Settings; set { App.Core.Settings = value; Refresh(); } }
    IGpuVendorBackend V => App.Core.Vendor;
    // The driver cache's limit and usage, the drive's free space and NVIDIA's Auto Shader Compilation, read off the UI thread; null until the first read.
    (CacheLimit? Limit, CacheUsage Usage, string Free, AutoShaderState? Auto)? read;
    readonly BackgroundRead<(CacheLimit?, CacheUsage, string, AutoShaderState?)> cache;
    long? amdUsed;   // AMD: the DirectX 12 cache, which its fixed limit counts

    public SettingsVm()
    {
        cache = new(() =>
        {
            var usage = V.GetCacheUsage();
            string free;
            drive = DriveOf(usage);
            amdUsed = (V as AmdBackend)?.AppCache.DxcBytes();
            try { free = Format.Bytes(new DriveInfo(drive).AvailableFreeSpace); } catch (Exception) { free = Format.Dash; }
            return (V.GetCacheLimit(), usage, free, V.GetAutoShaderCompilation());
        }, r =>
        {
            // Preselect the current custom limit, else 100 GB (the mockup's suggestion over the 16 GB default).
            if (read == null) SizeIndex = Array.FindIndex(Sizes, s => !s.Limit.IsDriverDefault && s.Limit == r.Item1) is >= 0 and var i ? i : 2;
            // The checkbox and level show the driver's state after every read (an apply's re-read included); Medium when off.
            AutoOn = r.Item4 is { Level: not AutoShaderCompilation.Off };
            AutoLevelIndex = r.Item4 is { Level: not AutoShaderCompilation.Off and var l } ? (int)l - 1 : 1;
            read = r;
            Changed();
        });
        cache.Request();
    }

    public string CacheTitle => $"{Fmt.Vendor(V.Vendor)} 着色器缓存大小";
    public bool IsNvidia => V.Vendor == GpuVendor.Nvidia;

    public static readonly string[] AutoLevels = ["Low", "Medium", "High"];   // AutoShaderCompilation.Low..High, as the NVIDIA App names them
    public bool? AutoOn { get; set; }
    public int AutoLevelIndex { get; set; } = 1;
    public AutoShaderCompilation AutoChoice => AutoOn == true ? (AutoShaderCompilation)(Math.Clamp(AutoLevelIndex, 0, 2) + 1) : AutoShaderCompilation.Off;
    public string AutoShaderText => read == null ? Format.Dash : read.Value.Auto switch
    {
        null => "无法读取此设置",
        { Level: AutoShaderCompilation.Off } => "当前：关闭",
        { TaskReady: true, Level: var l } => $"当前：开，{l}",
        _ => "设置已开启，但 NVIDIA 计划任务尚未建立：点击“应用”修复",
    };
    public string? AutoMessage { get; set; }
    public Microsoft.UI.Xaml.Controls.InfoBarSeverity AutoSeverity { get; set; }
    public bool HasAutoMessage => AutoMessage != null;
    public string CacheIntro => (IsNvidia ? "这是全局驱动设置，对应 NVIDIA App › 图形 › 着色器缓存大小。" : "这是全局驱动设置。")
        + (KeptOn ? "缓存满时驱动会删除较旧的条目，已保留的游戏会被放回。" : "缓存满时驱动会删除较旧的条目，相关游戏可能再次卡顿。");
    public bool Configurable => V.Caps.CacheSizeConfigurable;
    public bool NotConfigurable => !Configurable;
    public string NotConfigurableText => V.Vendor == GpuVendor.Amd
        ? $"AMD 驱动的 DirectX 12 着色器缓存固定为 {Format.Bytes(AmdAppCache.DxcCacheCap)}，无法调整大小。"
          + "超出后，游戏启动时驱动会移除最久未使用游戏的缓存" + (KeptOn ? "。已保留的游戏会被放回。" : "，SCSKiller 会把这些游戏显示为需要重新编译。")
        : V.Vendor == GpuVendor.Intel
        ? $"Intel 驱动为每个游戏最多保留 {Format.Bytes(IntelBackend.PerGameCap)} 的着色器，无法调整大小。"
          + "超出后驱动会丢弃该游戏较旧的着色器，因此 SCSKiller 优先编译最可能用到的部分。"
        : $"SCSKiller 暂时无法修改 {Fmt.Vendor(V.Vendor)} 驱动的缓存大小，只会显示用量。"
          + "如果游戏缓存被清理，请在显卡厂商控制面板中提高上限。";

    public string CurrentLimit => read == null ? Format.Dash : read.Value.Limit switch
    {
        null => "Unknown",
        { Bytes: null } => "Unlimited",
        { Bytes: 0 } => "Disabled",
        { Bytes: { } b, IsDriverDefault: var d } => Format.Bytes(b) + (d ? "（驱动默认值）" : ""),
    };
    public string Used => read is { Usage: var u } ? (u.UpperBound ? "≤ " : "") + Format.Bytes(u.BytesOnDisk) : Format.Dash;
    public bool HasAmdUse => amdUsed != null;
    public double AmdPercent => amdUsed is { } b ? Math.Min(100, 100.0 * b / AmdAppCache.DxcCacheCap) : 0;
    public string AmdUse => amdUsed is { } b ? $"DirectX 12 缓存：{Format.Bytes(b)}/{Format.Bytes(AmdAppCache.DxcCacheCap)}"
        + (b >= AmdAppCache.DxcCacheCap * 9 / 10 ? "。接近满：游戏启动时驱动会移除最久未使用游戏的缓存。" : "") : "";
    public bool IsAmd => V.Vendor == GpuVendor.Amd;
    bool KeptOn => IsAmd && S.KeepCachesAside;
    public bool KeepCachesAside { get => S.KeepCachesAside; set { if (value != S.KeepCachesAside) S = S with { KeepCachesAside = value }; } }
    public static IReadOnlyList<GameState> KeptGames => App.Core.Games.Where(g => g.Kept != null).OrderByDescending(g => g.Kept!.Frees).ThenBy(g => g.Game.Name).ToList();
    public bool HasKept => S.KeepCachesAside && KeptGames.Count > 0;
    public string KeptSummary => KeptGames is var k && k.Count > 0
        ? $"{k.Count} 个游戏，另存 {Format.Bytes(k.Sum(g => g.Kept!.Bytes))} · 可释放 {Format.Bytes(k.Sum(g => g.Kept!.Frees))}" : "";
    // through a link (the cache moved to another drive with a junction): the drive its files are on
    static string DriveOf(CacheUsage? u) => Path.GetPathRoot(u?.Path is { Length: > 0 } p ? GameFiles.FinalPath(p) ?? p : null) is { Length: > 0 } r ? r
        : Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))!;
    string? drive;   // read with the usage: resolving a link opens the folder
    public string FreeLabel
    {
        get
        {
            var d = drive ?? DriveOf(null);
            var root = d.TrimEnd('\\', ':');   // "C:\" -> "C"
            return root.Length == 1 ? $"释放 {root} 盘空间" : $"释放 {d.TrimEnd('\\')} 上的空间";
        }
    }
    public string Free => read?.Free ?? Format.Dash;

    public int SizeIndex { get; set; } = 2;
    public string? ApplyMessage { get; set; }
    public Microsoft.UI.Xaml.Controls.InfoBarSeverity ApplySeverity { get; set; }
    public bool HasApplyMessage => ApplyMessage != null;
    public void Refresh()
    {
        Changed();
        cache.Request();
    }

    public int DriverMode
    {
        get => (int)S.OnDriverUpdate;
        set
        {
            if (value < 0 || value == DriverMode) return;
            S = S with { OnDriverUpdate = (DriverUpdateMode)value };
            try { App.Core.ApplyDriverUpdateMode(); }  // user action: registers or removes the sign-in check
            catch (Exception e) { ApplyMessage = "无法更新登录时检查任务：" + e.Message; ApplySeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error; Changed(); }
        }
    }
    public bool? PauseWhileGaming { get => S.PauseWhileGaming; set { if (value is { } v && v != S.PauseWhileGaming) S = S with { PauseWhileGaming = v }; } }
    public double BackgroundThreads { get => UsedBackground; set { if ((int)value != UsedBackground) S = S with { BackgroundThreads = (int)value }; } }
    int UsedBackground => ScsKiller.UsedThreads(S.BackgroundThreads);
    public string BackgroundThreadsText => $"{UsedBackground} of {Environment.ProcessorCount}";
    public double MaxThreads => Environment.ProcessorCount;
    public bool HasLastRebuilt => LastRebuilt != "";
    public string LastRebuilt => App.Core.Games.Where(g => g.WarmedAt != null && g.WarmedDriverVersion != null).MaxBy(g => g.WarmedAt)?.WarmedDriverVersion is { } v ? $"上次为驱动 {v} 重新编译" : "";

    public bool? MaximumPlans { get => S.MaximumPlans; set { if (value is { } v && v != S.MaximumPlans) S = S with { MaximumPlans = v }; } }
    public bool? CompileRayTracing { get => S.CompileRayTracing; set { if (value is { } v && v != S.CompileRayTracing) S = S with { CompileRayTracing = v }; } }
    public bool? ShareRecordings { get => S.ShareRecordings; set { if (value is { } v && v != S.ShareRecordings) S = S with { ShareRecordings = v }; } }
    public bool? ShareDiagnostics { get => S.ShareDiagnostics; set { if (value is { } v && v != S.ShareDiagnostics) S = S with { ShareDiagnostics = v }; } }
    public bool? ActiveCheck { get => Core.App.ActiveCheck.Allowed(S); set { if (value is { } v && v != Core.App.ActiveCheck.Allowed(S)) S = Core.App.ActiveCheck.Answered(S, v); } }
    public void GamesChanged() => Changed();   // no driver-cache re-read (Refresh)
    public bool RecordAllGames { get => S.RecordAllGames; set { if (value != S.RecordAllGames) S = S with { RecordAllGames = value }; } }   // the core reconciles off this thread
    public bool? RecordAntiCheatGames { get => S.RecordAntiCheatGames; set { if (value is { } v && v != S.RecordAntiCheatGames) S = S with { RecordAntiCheatGames = v }; } }   // off: the core clears each game's switch off this thread
    /// <summary>"Recording in 23 games · 5 skipped: anti-cheat"; not-DX12 and unsupported games aren't counted.</summary>
    public string RecordingCount
    {
        get
        {
            var games = App.Core.Games;
            var skipped = games.Where(g => g.RecorderSkip is ScsKiller.SkipAntiCheat or ScsKiller.SkipShaderMod or ScsKiller.SkipForeignDll or ScsKiller.SkipModNotChainable
                    or ScsKiller.SkipVulkanMod or ScsKiller.SkipNeedsAdmin or ScsKiller.SkipPackageD3D12 or ScsKiller.SkipStreamline)
                .GroupBy(g => g.RecorderSkip).OrderByDescending(x => x.Count()).Select(x => $" · 已跳过 {x.Count()}：{x.Key}");
            int n = games.Count(g => g.RecorderInstalled);
            return $"在 {n} 个游戏中录制" + string.Concat(skipped);
        }
    }

    public static readonly int[] RecordingLimits = ScsKiller.RecordingLimits;
    public static readonly string[] RecordingLimitLabels = RecordingLimits.Select(ScsKiller.LimitText).ToArray();
    public int RecordingLimitIndex
    {
        get => Array.IndexOf(RecordingLimits, S.RecordingLimitMB);   // -1: a value set elsewhere, shown blank
        set { if (value >= 0 && RecordingLimits[value] != S.RecordingLimitMB) S = S with { RecordingLimitMB = RecordingLimits[value] }; }   // the core reconciles off this thread
    }
    /// <summary>Each game with a recording on disk, largest first: its size, and whether it reached the limit.</summary>
    public IReadOnlyList<DetailRow> Recordings => App.Core.Games.Where(g => g.RecordingBytes > 0).OrderByDescending(g => g.RecordingBytes)
        .Select(g => new DetailRow(g.Game.Name, Format.Bytes(g.RecordingBytes), g.RecordingPaused ? ScsKiller.PausedNote(S) : null)).ToList();
    public bool HasRecordings => App.Core.Games.Any(g => g.RecordingBytes > 0);
    public string RecordingSpace => App.Core.Games.Where(g => g.RecordingBytes > 0).ToList() is { Count: > 0 } r
        ? $"录制数据在 {r.Count} 个游戏中占用 {Format.Bytes(r.Sum(g => g.RecordingBytes))}" : "磁盘上没有录制数据";
    public bool? StartWithWindows
    {
        get => S.StartWithWindows;
        set
        {
            if (value is not { } v || v == S.StartWithWindows) return;
            S = S with { StartWithWindows = v };
            App.ApplyStartWithWindows(byUser: true);
        }
    }
    public bool? BeforeAfter { get => S.BeforeAfter; set { if (value is { } v && v != S.BeforeAfter) S = S with { BeforeAfter = v }; } }
    public int NewShadersChoice
    {
        get => (int)NewShaders.Global(S);
        set
        {
            if (value < 0 || value == NewShadersChoice) return;
            S = S with { OnNewShaders = (NewShadersMode)value };
            App.NewShadersChanged();
        }
    }
    public bool? ScanAtStart { get => S.ScanAtStart; set { if (value is { } v && v != S.ScanAtStart) S = S with { ScanAtStart = v }; } }
    public bool? HddMode { get => S.HddMode; set { if (value is { } v && v != S.HddMode) S = S with { HddMode = v }; } }
    public bool? CloseQuits { get => S.CloseQuits; set { if (value is { } v && v != S.CloseQuits) S = S with { CloseQuits = v }; } }
    public bool HasIntelGpu { get; } = App.Core.HasIntelGpu;
    public bool? ExperimentalIntel { get => S.ExperimentalIntel; set { if (value is { } v && v != S.ExperimentalIntel) S = S with { ExperimentalIntel = v }; } }
    // The GPU picker, shown only with more than one GPU SCSKiller compiles for; the first entry is the default rule.
    public IReadOnlyList<GpuChoice> GpuChoices { get; } = App.Core.GpuChoices;
    public bool HasGpuChoices => GpuChoices.Count > 1;
    public IReadOnlyList<string> GpuLabels => ["自动：显存最大的显卡", .. GpuChoices.Select(c => c.Name)];
    public int GpuIndex
    {
        get => S.Gpu is not { } c ? 0 : GpuChoices.ToList() is var all && all.IndexOf(c) is >= 0 and var at ? at + 1
            : all.FindIndex(g => (g.Vendor, g.DeviceId, g.Name) == (c.Vendor, c.DeviceId, c.Name)) + 1;   // as GpuBackends.Chosen: 0 when gone
        set
        {
            if (value < 0 || value == GpuIndex) return;
            S = S with { Gpu = value == 0 ? null : GpuChoices[value - 1] };
        }
    }

    public static readonly string[] Themes = ["使用 Windows 设置", "Light", "Dark"];   // AppTheme.System..Dark
    public int ThemeIndex
    {
        get => (int)(S.Theme ?? AppTheme.System);
        set
        {
            if (value < 0 || value == ThemeIndex) return;
            S = S with { Theme = value == 0 ? null : (AppTheme)value };
            App.Main.ApplyTheme(S.Theme);
        }
    }


    public double Threads { get => UsedThreads; set { if ((int)value != UsedThreads) S = S with { Threads = (int)value }; } }
    int UsedThreads => ScsKiller.UsedThreads(S.Threads);
    public string ThreadsText => $"{UsedThreads} of {Environment.ProcessorCount}";
    /// <summary>0 = Auto; 1 means 2 (the smallest budget offered).</summary>
    public double MaxCompileMemory { get => ShownMemory; set { var v = (int)value == 1 ? 2 : (int)value; if ((int)value != ShownMemory && v != S.MaxCompileMemoryGB) S = S with { MaxCompileMemoryGB = v }; } }
    int ShownMemory => Math.Min(S.MaxCompileMemoryGB, (int)MaxCompileMemoryMax);
    public string MaxCompileMemoryNote => $"自动会根据本机内存选择最佳上限（本机 {ScsKiller.AutoCompileMemoryGB()} GB，内存 {ScsKiller.RamGB()} GB）。调低可在编译时留出更多内存，调高可编译更快。";
    public double MaxCompileMemoryMax => ScsKiller.MaxCompileMemoryGB(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
    public string MaxCompileMemoryText => S.MaxCompileMemoryGB > 0 ? $"{S.MaxCompileMemoryGB} GB" : $"自动（{ScsKiller.AutoCompileMemoryGB()} GB）";
    public string PriorityNote => (S.Priority == WarmPriority.BelowNormal ? "优先级低于正常。" : "空闲优先级。") + "线程越少，编译越慢，但对电脑使用的影响越小。";
}

/// <summary>Settings' Account card: a view over <see cref="App.Account"/>, which changes on worker threads.</summary>
public sealed class AccountVm : Bindable
{
    static Account A => App.Account;
    readonly Coalesced changed;
    public AccountVm() => changed = new(App.Main.DispatcherQueue, Changed);

    /// <summary>While the page is shown. Signed in with nothing read yet this session: reads the status.</summary>
    public void Watch(bool on)
    {
        A.Changed -= changed.Request;
        Updater.Changed -= changed.Request;
        if (!on) return;
        A.Changed += changed.Request;
        Updater.Changed += changed.Request;
        Changed();
        if (A.SignedIn && A.Status == null && !A.Busy) _ = A.RefreshAsync();
    }

    public bool SignedIn => A.SignedIn;
    public bool SignedOut => !A.SignedIn;
    public bool SigningIn => A.SigningIn;
    public bool Busy => A.Busy;
    public bool Idle => !A.Busy;
    public string? Problem => A.Problem;
    public bool HasProblem => A.Problem != null;
    public bool OffersSharing => A.OffersSharing(App.Core.Settings);
    public void Refresh() => Changed();   // the settings changed (OffersSharing)

    // "下载共享着色器哈希": enabled only with "db" (follows sign-in, sign-out, refresh and a lapse through
    // Changed); the setting keeps its value. One quiet line under it: why it's off, or its last failure.
    public bool HasDb => A.HasDb;
    // Unticked without "db" whatever the setting says; the binding writes that false back, which must not reach the setting.
    public bool? UseCommunityDb
    {
        get => A.HasDb && App.Core.Settings.UseCommunityDb;
        set
        {
            if (!A.HasDb || value is not { } v || v == App.Core.Settings.UseCommunityDb) return;
            App.Core.Settings = App.Core.Settings with { UseCommunityDb = v };
            Changed();
        }
    }
    public string? DbNote => !A.SignedIn ? "覆盖的游戏清单对所有人开放；共享的哈希需要 Patreon 订阅。"
        : A.Status is not { } st ? null   // not read yet: the status line above says "正在检查…"
        : !A.HasDb ? "当前 Patreon 会员权限不包含社区数据库；已下载的内容仍可使用。"
        : (App.Core as ScsKiller)?.Community?.Problem;
    public bool HasDbNote => DbNote != null;

    public string Benefits => A.Status switch
    {
        null => A.Busy ? "正在检查会员状态…" : "尚未检查会员状态。",
        { Benefits: { } b } => b,   // the flag names live in Core (AccountStatus) so they're testable
        _ => "当前没有会员权益。Patreon 会员生效后，请单击“刷新状态”。",
    };

    // Update channel: a picker only when the token grants more than stable.
    static readonly Dictionary<string, string> ChannelNames = new()
    {
        [UpdateChannels.Stable] = "Stable", [UpdateChannels.Beta] = "测试版（上游 Patreon 会员）",
        [UpdateChannels.Alpha] = "预览版（上游 Patreon 赞助者）", [UpdateChannels.Internal] = "Internal",
    };
    IReadOnlyList<string> Offered => UpdateChannels.Offered(A.Status?.Ent);
    string Chosen => UpdateChannels.Effective(App.Core.Settings.UpdateChannel, AppVersion.Current.Channel, A.Status?.Ent);
    List<string> labels = [];
    public List<string> ChannelLabels => labels.SequenceEqual(Offered.Select(c => ChannelNames[c])) ? labels : labels = Offered.Select(c => ChannelNames[c]).ToList();   // same list: the ComboBox keeps its selection
    public bool ShowsChannels => A.SignedIn && Offered.Count > 1;
    public bool CanPickChannel => !Updater.Restarting;
    public bool CanSignOut => Idle && !Updater.Restarting;   // signing out can change the channel a restart applies
    public int ChannelIndex
    {
        get => Math.Max(0, Offered.ToList().IndexOf(Chosen));
        set
        {
            if (value < 0 || value >= Offered.Count || Offered[value] == Chosen || Updater.Restarting) return;   // -1: the list was replaced
            App.Core.Settings = App.Core.Settings with { UpdateChannel = Offered[value] };
            _ = Updater.CheckAsync();
            Changed();
        }
    }
    /// <summary>"立即返回稳定版": on an installed pre-release build, whatever the account.</summary>
    public bool OffersBackToStable => Updater.Installed && AppVersion.Current.Channel != UpdateChannels.Stable;
    public bool ShowsUpdates => ShowsChannels || OffersBackToStable;
    public string ChannelNote => Updater.Checking ? "正在检查更新…"
        : Updater.Ready is { } v ? AutoInstall.ReadyNote(v, Updater.InstallsAutomatically)
        : (Updater.InstallsAutomatically ? "更新会在后台下载，并在下次启动或退出时安装；编译或游戏运行期间不会安装。"
            : "Updates download in the background, and Restart to update installs them.") + "退出预发布渠道后，当前版本会保留到稳定版版本号超过它。";
    public string? UpdateProblem => Updater.Problem;
    public bool HasUpdateProblem => Updater.Problem != null;
}

/// <summary>A cache folder on the Cache page.</summary>
public sealed record CacheFolderRow(string Label, string Path, string Size, string? Note)
{
    public bool HasNote => Note != null;
}

/// <summary>A cache item; Folder: where to open it, null when its files are in a folder listed above.</summary>
public sealed record CacheItemRow(string Name, string Size, string? Folder, string? Note, CacheItem Item, CacheGroupKind Kind, bool Enabled)
{
    public bool HasFolder => Folder != null;
    public bool HasNote => Note != null;
}

public sealed record CacheGroupRow(string Title, string Size, string Caption, string? Note, IReadOnlyList<CacheItemRow> Items, bool Expanded,
    CacheGroup Group, bool Enabled)
{
    public bool HasNote => Note != null;
}

/// <summary>The Cache page: a read of <see cref="IScsKiller.CacheInventory"/> off the UI thread, and its deletes.</summary>
public sealed class CacheVm : Bindable
{
    CacheInventory? read;
    HashSet<string> games = [];   // the library the last read attributed by
    readonly BackgroundRead<CacheInventory> cache;

    public CacheVm()
    {
        cache = new(() => App.Core.CacheInventory(), r => { read = r; Changed(); });
        Refresh();
    }

    public void Refresh()
    {
        games = App.Core.Games.Select(g => g.Game.Id).ToHashSet();
        cache.Request();
    }

    /// <summary>A scan that lists other games (the first one after start among them) moves caches between groups.</summary>
    public void GamesChanged()
    {
        if (!games.SetEquals(App.Core.Games.Select(g => g.Game.Id))) Refresh();
    }

    public CacheInventory? Inventory => read;
    public string Total => read == null ? "正在读取缓存…" : $"着色器缓存共 {Format.Bytes(read.Groups.Sum(g => g.Bytes))}";
    public bool NoFolders => read is { Folders.Count: 0 };

    public bool Deleting { get; private set; }
    public bool CanDelete => read != null && !Deleting;
    public bool CanClearEverything => CanDelete && read!.Groups.Count > 0;
    public string? Result { get; private set; }
    public bool HasResult => Result != null;
    public Microsoft.UI.Xaml.Controls.InfoBarSeverity ResultSeverity { get; private set; }

    /// <summary>Deletes off the UI thread, then reads the caches again: the page shows what is left, not a subtraction.</summary>
    public async Task DeleteAsync(IReadOnlyList<CacheItem> items)
    {
        (Deleting, Result) = (true, null);
        Changed();
        try
        {
            var r = await Task.Run(() => App.Core.DeleteCaches(items));
            Result = $"已删除 {r.Files} 个文件，共 {Format.Bytes(r.Bytes)}。"
                + (r.Skipped.Count > 0 ? $" 已跳过 {r.Skipped.Count} 个：\n" + string.Join("\n", r.Skipped) : "");
            ResultSeverity = r.Skipped.Count > 0 ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success;
        }
        catch (Exception e)
        {
            (Result, ResultSeverity) = ($"未删除任何内容：{e.Message}", Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
        }
        Deleting = false;
        Refresh();
        Changed();
    }

    long OldDriver => read?.Bytes(CacheGroupKind.OldDriver) ?? 0;
    public bool HasOldDriver => OldDriver > 0;
    public string OldDriverText => $"{Format.Bytes(OldDriver)} 缓存文件早于当前驱动的安装时间（{read?.DriverInstalled?.ToLocalTime():d}），此后驱动再未使用过。";
    public string FreeText => $"释放 {Format.Bytes(OldDriver)}";
    public IReadOnlyList<CacheItem> OldDriverItems => read?.Groups.Where(g => g.Kind == CacheGroupKind.OldDriver).SelectMany(g => g.Items).ToList() ?? [];

    public IReadOnlyList<CacheFolderRow> Folders => read?.Folders.Select(f => new CacheFolderRow(f.Label, f.Path,
        f.Cap is { } cap ? $"{Format.Bytes(f.Bytes)} of {Format.Bytes(cap)}" : Format.Bytes(f.Bytes),
        f.TargetMissing ? "此目录是一个链接，但目标已丢失"
        : f.Cap is { } full && f.Bytes >= full * 9 / 10 ? "接近满：游戏启动时驱动会移除最久未使用游戏的缓存"
        : f.Final != null ? $"通过链接存放于 {f.Final}"
        : !f.Exists ? "尚未创建" : null)).ToList() ?? [];

    public IReadOnlyList<CacheGroupRow> Groups => read?.Groups.Select(g =>
    {
        var (title, caption) = Texts(g.Kind, read.DriverInstalled);
        var items = g.Items.Select(i => new CacheItemRow(ItemName(g.Kind, i), Format.Bytes(i.Bytes), Outside(i.Files), i.Note, i, g.Kind, CanDelete)).ToList();
        return new CacheGroupRow(title, Format.Bytes(g.Bytes), caption, g.Note, items, g.Kind != CacheGroupKind.Unattributed, g, CanDelete);
    }).ToList() ?? [];

    static string ItemName(CacheGroupKind kind, CacheItem i) => kind == CacheGroupKind.Unattributed && i.Name.Length == 8 && i.Name.All(char.IsAsciiHexDigit) ? $"未知程序 {i.Name}" : i.Name;

    public static (string Title, string Caption) Texts(CacheGroupKind kind, DateTime? installed) => kind switch
    {
        CacheGroupKind.Installed => ("已安装的游戏", "游戏库中的游戏，按 SCSKiller 学到的缓存键列出。"),
        CacheGroupKind.Uninstalled => ("已卸载的游戏", "SCSKiller 编译过或见过运行、但已不在游戏库中的游戏，按 exe 名列出。"),
        CacheGroupKind.OldDriver => ("旧驱动", $"写入时间早于当前驱动的安装时间（{installed?.ToLocalTime():d}），此后驱动再未使用过。"),
        CacheGroupKind.OldBuild => ("较早的 SCSKiller 编译", "Left by earlier compiles. The games don't read them."),
        CacheGroupKind.OtherVendor => ("其他显卡厂商", "Caches for another brand of GPU than the one SCSKiller compiles for."),
        _ => ("其他程序与游戏", "Desktop apps, and games SCSKiller hasn't compiled or seen running. A game moves to its group once SCSKiller sees it running."),
    };

    /// <summary>The confirm's text for deleting <paramref name="items"/> of one group: what happens after.</summary>
    public static string Consequence(CacheGroupKind kind, IReadOnlyList<CacheItem> items) => kind switch
    {
        CacheGroupKind.Installed or CacheGroupKind.Uninstalled =>
            $"这些游戏下次编译时会重新编译着色器，在此之前会卡顿：{string.Join("、", items.Select(i => i.Name))}。",
        CacheGroupKind.OldDriver => "当前驱动安装后未使用过这些文件，无需重新编译。",
        CacheGroupKind.OldBuild => "游戏不会读取这些文件，无需重新编译。",
        CacheGroupKind.OtherVendor => "在该显卡上运行的程序会在运行时重新构建缓存。",
        _ => "这些程序与游戏会在运行时重新构建缓存，重建完成前可能卡顿。",
    } + string.Concat(items.Select(i => i.Note).OfType<string>().Select(n => " " + n)) + " 正在使用的文件会被跳过。";

    /// <summary>The folder holding all these files, when it isn't one of the cache folders listed above.</summary>
    string? Outside(IReadOnlyList<CacheFile> files)
    {
        if (files.Count == 0 || Path.GetDirectoryName(files[0].Path) is not { } dir) return null;
        while (!files.All(f => GameFiles.Inside(dir, f.Path)) && Path.GetDirectoryName(dir) is { } up) dir = up;
        return read!.Folders.Any(f => string.Equals(GameFiles.DirKey(f.Path), GameFiles.DirKey(dir), StringComparison.OrdinalIgnoreCase)) ? null : dir;
    }
}
