using System.Globalization;

namespace SCSKiller.Core.App;

/// <summary>The before and after recording's pure parts: whether a run counts, the comparison, the share text.</summary>
public static partial class BeforeAfter
{
    /// <summary>Play a run needs to count; the countdown asks for <see cref="BeforeAfterRecord.Minutes"/>.</summary>
    public static readonly TimeSpan MinPlay = TimeSpan.FromMinutes(3);

    /// <summary>With fewer shader stutters than this in the before run, the game was already smooth.</summary>
    public const int SmoothStutters = 3;

    // Every user-facing line of the report and the share text
    const string NoFrames = "{0}那次运行没有帧时间记录";
    const string NoCreates = "{0}那次运行没有管线日志";
    const string TooShort = "{0}那次运行只玩了 {1}，至少要玩 {2}。";
    const string LibraryLoads = "编译后那次运行另有 {0} 条管线是从游戏自己的缓存加载的，不计入 SCSKiller";
    const string OtherSettings = "两次运行的分辨率或显示格式不同：编译前 {0}，编译后 {1}";
    const string OverlayOnce = "只有一次运行开了游戏内的倒计时";
    const string Crashed = "编译前那次运行游戏没有正常退出";
    const string MaybeWarm = "AMD 的 DirectX 11 驱动缓存无法检查，编译前那次运行未必是完全冷的";
    const string MaybeHelped = "分不清游戏的 DirectX 11 驱动缓存和别的程序的，编译后那次可能用上了编译前留下的缓存";
    const string FrameGenBoth = "两次运行的帧生成都是{0}。";
    const string FrameGenOne = "只有{0}那次运行开了帧生成。";
    const string Share = "{0} 的着色器编译卡顿：SCSKiller 预编译之后，{3}的游玩中从 {1} 次降到 {2} 次。";
    const string ShareReady = " 就绪的管线占 {0}%，原来是 {1}%。";
    const string ShareSmooth = "{0} 在这台电脑上本来就不卡：SCSKiller 把就绪管线从 {1}% 提到 {2}%。";
    const string ShareSmoothNoShare = "{0} 在这台电脑上本来就不卡：用了 SCSKiller 之后，{3}的游玩中着色器编译卡顿从 {1} 次降到 {2} 次。";
    const string ShareDx11 = "{0}（DirectX 11）：SCSKiller 预编译前后，{3}的游玩中 50 毫秒以上的卡顿从 {1} 次变为 {2} 次。";
    public const string Dx11Label = "DirectX 11：SCSKiller 分不出哪些卡顿是着色器编译造成的";
    const string ShareLink = " scskiller.com";
    const int ShareMax = 280, LinkWeight = 23;   // X's limit; X counts any link as 23

    /// <summary>The game page's button with no pair: hidden only with the setting off, greyed with the reason as its note
    /// while <see cref="ScsKiller.BeforeAfterBlocked"/> says why a pair can't start.</summary>
    public static (bool Shown, bool Enabled, string Note) StartButton(bool on, string? blocked) => (on, on && blocked == null, blocked ?? "");

    /// <summary>Why the run doesn't count, one line for the button's note; null = it counts.</summary>
    public static string? RunProblem(SessionSnapshot? run, bool before)
    {
        var which = before ? "编译前" : "编译后";
        if (run == null || run.Frames == 0) return F(NoFrames, which);
        if (run.Creates == null) return F(NoCreates, which);
        if (run.PlaySeconds < MinPlay.TotalSeconds)
            return F(TooShort, which, run.PlaySeconds < 60 ? "不到一分钟" : $"{(int)(run.PlaySeconds / 60)} 分钟",
                $"{(int)MinPlay.TotalMinutes} 分钟");
        return null;
    }

    public static RunStats Stats(SessionSnapshot s)
    {
        var shader = s.Hitches.Where(h => h.Cause == HitchCause.Shader).Select(h => h.Ms).ToList();
        var play = s.Hitches.Where(h => h.Cause is HitchCause.Shader or HitchCause.Other).Select(h => h.Ms).ToList();
        return new RunStats(s.PlaySeconds, shader.Count, shader.Sum() / 1000, shader.DefaultIfEmpty(0).Max(),
            s.Creates?.ReadyShare, s.Creates?.PlayCompiles ?? 0, s.Low1PctFps, s.AvgFps, s.Hitches.Count(h => h.Cause == HitchCause.Other),
            play.Count, play.Sum() / 1000, play.DefaultIfEmpty(0).Max());
    }

    /// <summary>The before run is cold (a run that started with old driver-cache files is played again), unless
    /// <paramref name="notCold"/>: its clear didn't know the key of a cache SCSKiller can't watch (AMD's DirectX 11).
    /// <paramref name="afterHelped"/>: that key was still unknown at the clear before the compile. A cold
    /// run's ready pipelines are the driver's own reuse within the launch (both vendors cache per shader stage, so a new
    /// pipeline of stages it already compiled is ready), not a cache it started with.</summary>
    public static Comparison Compare(SessionSnapshot before, SessionSnapshot after, bool notCold = false, bool afterHelped = false)
    {
        var (b, a) = (Stats(before), Stats(after));
        bool dx11 = before.D3D11 || after.D3D11;   // either run without create rows: no split claimed for the pair
        var notes = new List<string>();
        if ((after.Creates?.Library ?? 0) - (before.Creates?.Library ?? 0) is var more and > 0) notes.Add(F(LibraryLoads, more));
        if (before.SwapChain != null && after.SwapChain != null && before.SwapChain != after.SwapChain) notes.Add(F(OtherSettings, before.SwapChain, after.SwapChain));
        if (before.Overlay != after.Overlay) notes.Add(OverlayOnce);
        if (!before.CleanExit) notes.Add(Crashed);
        if (notCold) notes.Add(MaybeWarm);
        else if (afterHelped) notes.Add(MaybeHelped);
        if (before.FrameGen is { } fb && after.FrameGen is { } fa)   // a run whose module loaded but whose frames can't tell says nothing
            notes.Add(fb == fa ? F(FrameGenBoth, fb ? "开启" : "关闭") : F(FrameGenOne, fb ? "编译前" : "编译后"));
        return new Comparison(b, a, Math.Abs(b.PlaySeconds - a.PlaySeconds) > 0.1 * Math.Min(b.PlaySeconds, a.PlaySeconds),
            !dx11 && b.ShaderStutters < SmoothStutters, notes, dx11);
    }

    /// <summary>Each run's play in whole minutes, as the card's footer and the share text name them.</summary>
    public static (int Before, int After) PlayMinutes(Comparison c) =>
        ((int)Math.Round(c.Before.PlaySeconds / 60, MidpointRounding.AwayFromZero), (int)Math.Round(c.After.PlaySeconds / 60, MidpointRounding.AwayFromZero));

    /// <summary>Within X's 280 with the link (<see cref="PostLength"/>); a long game name is cut to fit. The counts are the card's.</summary>
    public static string ShareText(string game, Comparison c)
    {
        var (b, a) = PlayMinutes(c);
        var span = b == a ? $"{b} 分钟" : $"{b} 分钟和 {a} 分钟";
        var (before, after) = (c.Before.ReadyShare, c.After.ReadyShare);
        string Text(string name) =>
            (c.Dx11 ? F(ShareDx11, name, c.Before.Hitches, c.After.Hitches, span)
            : c.AlreadySmooth
                ? before != null && after != null ? F(ShareSmooth, name, Percent(before.Value), Percent(after.Value))
                    : F(ShareSmoothNoShare, name, c.Before.ShaderStutters, c.After.ShaderStutters, span)
                : F(Share, name, c.Before.ShaderStutters, c.After.ShaderStutters, span)
                    + (before != null && after != null ? F(ShareReady, Percent(after.Value), Percent(before.Value)) : ""))
            + ShareLink;
        var text = Text(game);
        for (int n = game.Length - 1; n >= 1 && PostLength(text) > ShareMax; n--) text = Text(game[..n].TrimEnd() + "…");
        return text;
    }

    /// <summary>The share text's length as X counts it (docs.x.com, counting characters): a link 23, a code point in Latin
    /// and general punctuation 1, any other 2 (arrows, the ellipsis, CJK, emoji). Anything shaped like a domain counts as a
    /// link, a game name's "GOG.com" too: X's own list of domains is longer, so this errs on the short side.</summary>
    public static int PostLength(string text) =>
        Domain().Replace(text, new string('x', LinkWeight)).EnumerateRunes()
            .Sum(r => r.Value is <= 4351 or >= 8192 and <= 8205 or >= 8208 and <= 8223 or >= 8242 and <= 8247 ? 1 : 2);

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(?:[A-Za-z0-9-]+\.)+[A-Za-z]{2,}\b")]
    private static partial System.Text.RegularExpressions.Regex Domain();

    /// <summary>A share in whole percent, 100 only when every pipeline was ready.</summary>
    static int Percent(double share) => share >= 1 ? 100 : Math.Min(99, (int)Math.Round(share * 100));

    static string F(string format, params object[] args) => string.Format(CultureInfo.InvariantCulture, format, args);

    public const string Link = "https://scskiller.com";
}
