namespace SCSKiller.Core.App;

/// <summary>Text the app and the CLI show.</summary>
public static class Format
{
    public const string Dash = "—";

    /// <summary>"3.2 GB" from 1 GiB up, else whole MB; a dash for null.</summary>
    public static string Bytes(long? b) => b switch
    {
        null => Dash,
        >= 1L << 30 => $"{b / (double)(1L << 30):0.#} GB",
        _ => $"{b / (double)(1 << 20):0} MB",
    };

    /// <summary><see cref="ScsKiller.Duration"/>; a dash for null.</summary>
    public static string Duration(TimeSpan? t) => t is { } v ? ScsKiller.Duration(v) : Dash;

    public static string Ms(double ms) => ms >= 1000 ? $"{ms / 1000:0.0} s" : $"{ms:0} ms";

    // FrameLog's 1% low is 0 only when play is empty: the startup never found a quiet window
    public static bool NoPlay(double low1PctFps) => low1PctFps <= 0;
    const string NoPlayText = "一直在加载或编译着色器";

    /// <summary>"5 min: 3 stutters from shader compiles (worst 1.3 s), 2 other hitches, 1% low 35 fps. Loading: 4 slow
    /// frames compiling shaders (worst 9.4 s), 12 others." (compiling shaders: startup only)</summary>
    public static string Frames(FrameReport f, IReadOnlyList<Hitch> shader)
    {
        int other = f.Hitches.Count(h => h.Cause == HitchCause.Other), loading = f.Hitches.Count(h => h.Cause == HitchCause.Loading);
        var compiling = f.Hitches.Where(h => h.Cause == HitchCause.LoadingShaders).ToList();
        return (NoPlay(f.Low1PctFps) ? $"{Duration(f.Duration)}, {NoPlayText}." : $"{Duration(f.Duration)}: " + (shader.Count == 0 ? "无着色器编译卡顿"
                : $"{shader.Count:N0} 次卡顿来自着色器编译（最长 {Ms(shader.Max(h => h.Ms))}）")
            + (other == 0 ? "，没有其他卡顿" : $"，其他卡顿 {other:N0} 次")
            + $"，最低 1% 帧率 {f.Low1PctFps:0} fps。")
            + (compiling.Count + loading == 0 ? "" : "加载阶段：" + string.Join("，", new[]
            {
                compiling.Count == 0 ? null : $"{compiling.Count:N0} 个慢帧在编译着色器（最长 {Ms(compiling.Max(h => h.Ms))}）",
                loading == 0 ? null : compiling.Count == 0 ? $"{loading:N0} 个慢帧" : $"其他 {loading:N0} 个",
            }.OfType<string>()) + ".");
    }

    /// <summary>An earlier launch in the frame history: "1% low 35 fps · 3 stutters".</summary>
    public static string Session(FrameSession e) => NoPlay(e.Low1PctFps) ? NoPlayText
        : $"1% 低帧 {e.Low1PctFps:0} fps · " + (e.Stutters == 0 ? "no stutters" : $"{e.Stutters:N0} stutter{(e.Stutters == 1 ? "" : "s")}");

    /// <summary>"Carved DXBC", "Unity 2022.3.10"; just the family (or <paramref name="family"/>) when the reader found no
    /// version: Carved and FromSoft say "-", Unity "?".</summary>
    public static string Engine(EngineInfo e, string? family = null) =>
        e.Version is "" or "-" or "?" ? family ?? e.Family : $"{family ?? e.Family} {e.Version}";

    /// <summary>The item is being compiled: any stage before it finishes, paused included.</summary>
    public static bool Running(QueueItem q) => q.Stage is QueueStage.Indexing or QueueStage.Planning or QueueStage.Materializing
        or QueueStage.Warming or QueueStage.Paused;

    /// <summary>The notification area's status: a line, and the compile's share done (null = no progress to show).
    /// <paramref name="running"/>: the queue's running item (<see cref="Running"/>), <paramref name="name"/> its game's.</summary>
    public static (string Line, double? Done) TrayStatus(QueueItem? running, string? name, bool quitting)
    {
        double? done = running is { Progress: { Total: > 0 } p } ? Math.Clamp((double)p.Done / p.Total, 0, 1) : null;
        return quitting ? ("正在结束：显卡驱动正在保存着色器缓存", null)
            : running == null ? ("Idle", null)
            : running.PlanCheck ? ("正在检查游戏是否有新内容可编译", null)
            : running.Stage == QueueStage.Paused ? ($"已暂停：{name}", done)
            : running.Stage == QueueStage.Warming && done is { } d ? ($"正在编译 {name}，{100 * d:0}%", d)
            : ($"正在准备 {name}", null);
    }

    /// <summary>"FSR4：已知 80 条管线，会随游戏一起编译", "DLSS：由 NVIDIA 驱动自行编译",
    /// "XeSS：已检测到，录制识别后可加入编译".</summary>
    public static string Middleware(MiddlewareTag t) => t.Label + (t.Pipelines > 0 ? $"：已知 {t.Pipelines:N0} 条管线，会随游戏一起编译"
        : t.Label == "DLSS" ? "：由 NVIDIA 驱动自行编译" : "：已检测到，录制识别后可加入编译");

    /// <summary>The Library's notice that this GPU can't compile, or on Intel that its support is experimental
    /// (<paramref name="intel"/>: the Intel backend is in use); null on NVIDIA and AMD, or once closed for this GPU
    /// (<paramref name="dismissedFor"/>: <see cref="Settings.GpuNoticeDismissed"/>).</summary>
    public static string? GpuNotice(GpuInfo gpu, string? dismissedFor, bool intel = false) => gpu.Vendor is GpuVendor.Nvidia or GpuVendor.Amd || dismissedFor == gpu.Name ? null
        : gpu.Vendor switch
        {
            GpuVendor.Intel when intel => "Intel 支持为实验性功能。DirectX 12 游戏需要先录制一次。",
            GpuVendor.Intel => "Intel 支持为实验性功能且已关闭。请在设置中开启。",
            GpuVendor.Qualcomm => "SCSKiller 暂不支持在 Qualcomm 显卡上编译：尚未完成其驱动着色器缓存行为的测量。",
            _ => "SCSKiller 暂不支持在此显卡上编译：目前只了解 NVIDIA 与 AMD 驱动的着色器缓存机制。",
        };

    /// <summary>A Library row's note: a few words for the status's reason (the whole one is in the row's tooltip and on the
    /// game's page); null when the status title says it. Read from the core's reasons (ScsKiller.Evaluate, Planner.Check,
    /// the engine readers); an unknown one shows its first clause.</summary>
    public static string? ShortNote(GameState s)
    {
        var r = s.StatusReason;
        bool Has(string part) => r.Contains(part, StringComparison.Ordinal);
        bool Starts(string part) => r.StartsWith(part, StringComparison.Ordinal);
        var partly = ScsKiller.IsPartlyWarmed(s);
        return s.Status switch
        {
            _ when s.CompileUnreached => "此游戏需要专用加载方式",
            GameStatus.Warmed when r == ScsKiller.OverCacheLimitReason => "超出着色器缓存容量",
            GameStatus.Warmed when r == ScsKiller.CacheFullReason => "着色器缓存已满",
            _ when ScsKiller.NotReached(r) => "尚未写入游戏缓存",
            GameStatus.Warmed when ScsKiller.IsPartlyCompiled(s) => Sentence(ScsKiller.PartlyCompiledNote(s)),
            GameStatus.Warmed when partly => $"驱动 {s.WarmedDriverVersion} · 首次启动仍编译了 {s.Careful!.LaunchCompiled * 100:0}%",
            GameStatus.Warmed when ScsKiller.RtAfterRecording(s) && s.NoStutter == null => s.RecorderInstalled ? "录制器已开启：请启用光追并游玩"
                : ScsKiller.RecordedEnough(s) ? "光追仍需要录制" : "光追还需要约 5 分钟录制",
            GameStatus.Warmed when ScsKiller.BothApis(s.Plan) => "DirectX 11 和 12" + (ScsKiller.IsPartial(s.Plan) ? " · 部分覆盖" : ""),
            GameStatus.Warmed => $"驱动 {s.WarmedDriverVersion}" + (ScsKiller.IsPartial(s.Plan) ? " · 部分覆盖" : ""),
            GameStatus.Stale => StaleNote(r),
            GameStatus.NeedsRecording when r == ScsKiller.OfflineSessionNote => r,
            GameStatus.NeedsRecording when s.NoStutter != null && !s.RecorderInstalled => "无需编译",
            GameStatus.NeedsRecording when s.RecordingPaused => "录制已达到容量上限",
            GameStatus.NeedsRecording when ScsKiller.NeverRecorded(s) => null,   // the title: ScsKiller.NothingRecordedTitle
            GameStatus.NeedsRecording when s.RecorderInstalled && !ScsKiller.RecordedEnough(s) => Has(Planning.Planner.Dx12Only) ? "录制器已开启：请用 DirectX 12 游玩" : "录制器已开启：请游玩约 5 分钟",
            GameStatus.NeedsRecording when Starts(ScsKiller.RtNeedsRecording) => "用于光线追踪效果" + (s.InCommunityDb == true ? " · " + InDb(r) : ""),
            GameStatus.NeedsRecording when s.InCommunityDb == true => Sentence(InDb(r)),
            GameStatus.NeedsRecording when Starts("the recording has no draws") => "请进入游戏世界并游玩",
            GameStatus.NeedsRecording when s.RecorderInstalled => Sentence(FirstClause(r)),
            GameStatus.NeedsRecording when s.RecorderEffective && Games.Collections.RecordedBy(s.Game.Id) != null => "录制器已开启：请游玩约 5 分钟",   // its collection's first game's
            GameStatus.NeedsRecording when s.RiskEligible => "在其页面录制一次会话",
            GameStatus.NeedsRecording when s.RecorderSkip != null => "录制器当前不可用",
            GameStatus.NeedsRecording => "开启录制并游玩",
            GameStatus.Ready when Has("ray-traced effects aren't compiled") => !s.AnyAntiCheat || s.RiskEligible ? "光追效果尚未编译" : $"光追被 {ScsKiller.AntiCheatName(s)} 阻止",
            GameStatus.Ready when Has(ScsKiller.RtUnseenNote) => "录制中未观察到光追",
            GameStatus.Ready when Has(ScsKiller.RtInlineNote) => "路径追踪需要录制",
            GameStatus.Ready when ScsKiller.IsPartial(s.Plan) => "部分覆盖",
            GameStatus.Ready when Has(Planning.Planner.UntestedNote) => "此引擎版本尚未测试",
            GameStatus.Ready when Has("for DirectX 12, ") => !s.AnyAntiCheat || s.RiskEligible ? "DirectX 11、DirectX 12 需要一次录制" : $"DirectX 11、DirectX 12 被 {ScsKiller.AntiCheatName(s)} 阻止",
            GameStatus.Ready when Has("also compiles every DirectX 11 shader") => "DirectX 11 和 12",
            GameStatus.Ready when Starts("compiles every DirectX 11 shader") => "DirectX 11",
            GameStatus.Ready => null,
            _ when r == Games.BootPrecompiles.Reason => "在主菜单阶段构建着色器",
            _ when s.ShaderModBlocks => $"{s.ShaderMod} 改变了其所有管线",
            _ when s.AnyAntiCheat && Starts("需要一次录制，") => $"被 {ScsKiller.AntiCheatName(s)} 阻止" + (s.InCommunityDb == true ? " · " + InDb(r) : ""),
            _ when Has(ScsKiller.ManualNoRecording) => "需要录制：请确认其目录",
            _ when s.Engine?.Encrypted == true => null,   // the title: "游戏文件已加密"
            _ when r is Codecs.NoOodleDll or Codecs.NoNativeOodle => r,
            _ when s.Engine?.Unsupported is { } u => u.StartsWith("no D3D shaders", StringComparison.Ordinal) ? "未找到 DirectX 着色器" : "引擎暂不支持",
            _ when r == ScsKiller.CheckingReason => null,   // the title: "正在检查…"
            _ when r == ScsKiller.NotCheckedReason => "打开以检查",
            _ when s.Engine == null => r == "engine not supported yet" ? "引擎暂不支持" : "无法读取游戏文件",
            _ when Starts("runs on ") => "运行于 " + FirstClause(r["runs on ".Length..]),
            _ when Starts("not supported on this GPU") => "此显卡暂不支持",
            _ => Sentence(FirstClause(r)),
        };
    }

    /// <summary>A shader mod that doesn't block the game, on its own line after its reason: "RenoDX changes this game's
    /// pipelines: ...". None for an unsupported game: its status and reason alone.</summary>
    public static string ModNote(GameState s) =>
        (s is { ShaderMod: not null, ShaderModBlocks: false } && s.Status != GameStatus.Unsupported ? "\n" + ScsKiller.ShaderModNote(s) : "")
        + (s.Dxvk && s.Status != GameStatus.Unsupported ? "\n" + (s.DxvkCompiled ? ScsKiller.DxvkCompiledNote : ScsKiller.DxvkNote) : "");

    /// <summary>Recorder in, not long enough yet: the DirectX 12 hint stays, a DirectX 11 run never loads the recorder.</summary>
    public static string RecorderOnNote(GameState s) => "录制器已开启：请游玩约 5 分钟"
        + (s.StatusReason.Contains(Planning.Planner.Dx12Only, StringComparison.Ordinal) ? "，" + Planning.Planner.Dx12Only : "");

    static string StaleNote(string r) =>
        r.StartsWith("driver changed:", StringComparison.Ordinal) ? $"驱动 {r[(r.LastIndexOf(' ') + 1)..]} 清空了缓存"
        : r == ScsKiller.TrimmedPartReason ? "部分着色器缓存已丢失"
        : r == ScsKiller.TrimmedAllReason ? "其着色器缓存已被移除"
        : r.StartsWith("the compile didn't reach", StringComparison.Ordinal) ? "编译未进入游戏使用的缓存"
        : r.StartsWith("the game runs as", StringComparison.Ordinal) ? "exe 文件名的大小写变了"
        : r.StartsWith("the last compile filled the cache of", StringComparison.Ordinal) ? "为另一个 exe 编译"
        : r.StartsWith("game updated", StringComparison.Ordinal) ? "游戏已更新"
        : r.StartsWith("game shaders changed", StringComparison.Ordinal) ? "游戏着色器已改变"
        : r.StartsWith("SCSKiller can now compile", StringComparison.Ordinal) ? "SCSKiller 可编译更多内容"
        : r.StartsWith("Maximum mode", StringComparison.Ordinal) ? "最大模式：还有更多要编译"
        : Sentence(FirstClause(r));   // "已录制 N 条新管线"

    /// <summary>The whole reason in the app's language. The reasons <see cref="NewShaders"/> and <see cref="StaleNote"/> tell apart are
    /// matched by their English text, so they are translated here rather than where they are made. Only a reason that starts
    /// with one of them changes: one already written in Chinese, or a mix (a Chinese line after one of these), passes through
    /// its first line only.</summary>
    public static string ReasonText(string r)
    {
        if (r.Length == 0) return r;
        const string Driver = "driver changed: ", Updated = "game updated since the warm", Build = "game updated since the warm (build ";
        if (r.StartsWith(Driver, StringComparison.Ordinal)) return "驱动已更换：" + r[Driver.Length..].Replace(" -> ", " → ");
        if (r.StartsWith(Build, StringComparison.Ordinal)) return $"预编译后游戏已更新（版本 {r[Build.Length..^1].Replace(" -> ", " → ")}）";
        if (r.StartsWith(Updated, StringComparison.Ordinal)) return "预编译后游戏已更新";
        if (r.StartsWith("game shaders changed since the warm", StringComparison.Ordinal)) return "预编译后游戏着色器已改变";
        if (r.StartsWith("the compile didn't reach", StringComparison.Ordinal)) return "编译未进入游戏使用的缓存";
        if (r.StartsWith("the last compile filled the cache of ", StringComparison.Ordinal))
        {
            var rest = r["the last compile filled the cache of ".Length..];
            var at = rest.IndexOf(", but the game runs ", StringComparison.Ordinal);
            return at > 0 ? $"上次编译填充的是 {rest[..at]} 的缓存，而游戏运行的是 {rest[(at + ", but the game runs ".Length)..]}" : "上次编译填充的是另一个 exe 的缓存";
        }
        if (r.StartsWith("the game runs as ", StringComparison.Ordinal))
        {
            var rest = r["the game runs as ".Length..];
            var at = rest.IndexOf(", the warm filled the cache of ", StringComparison.Ordinal);
            return at > 0 ? $"游戏以 {rest[..at]} 运行，而预编译填充的是另一个 exe 的缓存（驱动按 exe 名的大小写区分缓存）" : "exe 文件名的大小写变了";
        }
        if (r.StartsWith("Maximum mode", StringComparison.Ordinal)) return "最大模式：所有着色器配对仍待编译";
        if (r.StartsWith("SCSKiller can now compile more", StringComparison.Ordinal)) return "SCSKiller 现在可以为此游戏编译更多内容";
        const string More = "SCSKiller can now compile ";
        if (r.StartsWith(More, StringComparison.Ordinal))
        {
            var rest = r[More.Length..];
            var at = rest.IndexOf(" more pipeline", StringComparison.Ordinal);
            return at > 0 && long.TryParse(rest[..at].Replace(",", ""), out var n) ? $"SCSKiller 现在可以为此游戏多编译 {n:N0} 条管线" : "SCSKiller 现在可以为此游戏编译更多内容";
        }
        return r;
    }

    static string FirstClause(string t) => t.Split([": ", "; ", " ("], 2, StringSplitOptions.None)[0];
    static string Sentence(string t) => t.Length > 0 ? char.ToUpperInvariant(t[0]) + t[1..] : t;
    static string InDb(string reason) => reason.Contains(ScsKiller.InDbSupportersNote, StringComparison.Ordinal) ? ScsKiller.InDbSupportersNote : ScsKiller.InDbNote;
}
