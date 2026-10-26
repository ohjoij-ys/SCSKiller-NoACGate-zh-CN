using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SCSKiller.Core;
using System.Globalization;
using SCSKiller.Core.App;
using SCSKiller.Core.Vendors;

namespace SCSKiller.App.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsVm Vm { get; } = new();
    public AccountVm Patreon { get; } = new();

    public SettingsPage()
    {
        InitializeComponent();
        var refresh = new Coalesced(DispatcherQueue, Vm.GamesChanged);   // reconcile changes the recorder count
        void OnChanged(GameState _) => refresh.Request();
        Loaded += (_, _) => { Patreon.Watch(true); App.Core.GameChanged += OnChanged; };
        Unloaded += (_, _) => { Patreon.Watch(false); App.Core.GameChanged -= OnChanged; };
        Vm.PropertyChanged += (_, _) => Patreon.Refresh();   // e.g. the share checkbox ticked: the prompt goes
    }

    // Account: failures show in the card (Account.Problem), so these never throw.
    void OnSignIn(object _, RoutedEventArgs __) => _ = App.Account.SignInAsync();
    void OnCancelSignIn(object _, RoutedEventArgs __) => App.Account.CancelSignIn();
    void OnRefreshAccount(object _, RoutedEventArgs __) => _ = App.Account.RefreshAsync();
    void OnSignOut(object _, RoutedEventArgs __) { if (!Updater.Restarting) _ = App.Account.SignOutAsync(); }

    // The latest stable, even though it's older than this pre-release (a downgrade, allowed this once).
    void OnBackToStable(object _, RoutedEventArgs __)
    {
        if (Updater.Restarting) return;   // the restart applies the update it waited for, or none
        App.Core.Settings = App.Core.Settings with { UpdateChannel = UpdateChannels.Stable };
        _ = Updater.CheckAsync(backToStable: true);
    }

    // The post-sign-in share prompt (Account.OffersSharing).
    void OnShare(object _, RoutedEventArgs __) => Answer(share: true);
    void OnNotNow(object _, RoutedEventArgs __) => Answer(share: false);
    void Answer(bool share)   // asked once, ever: either answer dismisses it
    {
        App.Core.Settings = App.Core.Settings with { SharePromptDismissed = true, ShareRecordings = share || App.Core.Settings.ShareRecordings };
        Vm.Refresh();   // the share checkbox; its PropertyChanged refreshes the prompt too
    }

    // Stands in for the website's recorder-anti-cheat page until it exists.
    public ContentDialog AntiCheatDetailsDialog() => new()
    {
        XamlRoot = XamlRoot, Title = "在有反作弊的游戏中录制", CloseButtonText = "关闭",
        Content = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "为一次会话，SCSKiller 会把它的录制器（一个 d3d12.dll）放到游戏 exe 旁边。\n\n"
                + "带反作弊的游戏可能把录制器视为作弊，在会话期间或之后封禁你的账号。目前没有已知安全的游戏。\n\n"
                + "游戏退出后 SCSKiller 会移除它放入的每个文件；若发生崩溃，则在你下次登录时移除。\n\n"
                + "在这些游戏中启用录制器风险自负，SCSKiller 不对封禁负责。",
        },
    };

    async void OnAntiCheatDetails(object _, RoutedEventArgs __) => await App.ShowAsync(AntiCheatDetailsDialog());

    /// <summary>Also --screenshots: the games whose AMD cache is kept aside, each with its size and a Remove.</summary>
    public ContentDialog ManageKeptDialog()
    {
        var rows = new StackPanel { Spacing = 10 };
        foreach (var g in SettingsVm.KeptGames)
        {
            var row = new Grid { ColumnSpacing = 12, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
            row.Children.Add(new StackPanel { Spacing = 2, Children = { new TextBlock { Text = g.Game.Name, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = Fmt.KeptSize(g.Kept!), Style = (Style)Application.Current.Resources["Secondary"] } } });
            var remove = new Button { Content = "移除", VerticalAlignment = VerticalAlignment.Center };
            remove.Click += (_, _) => { App.Core.DropKeptCache(g.Game.Id); rows.Children.Remove(row); Vm.Refresh(); };
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            rows.Children.Add(row);
        }
        return new()
        {
            XamlRoot = XamlRoot, Title = "已另存", CloseButtonText = "关闭",
            Content = new StackPanel { Spacing = 14, MinWidth = 420, Children =
            {
                new TextBlock { Text = "移除后可释放空间。游戏下次编译后会再次保留。", TextWrapping = TextWrapping.Wrap },
                new ScrollViewer { MaxHeight = 360, Content = rows },
            } },
        };
    }

    async void OnManageKept(object _, RoutedEventArgs __) => await App.ShowAsync(ManageKeptDialog());

    public void ScrollToEnd() => Scroller.ChangeView(null, Scroller.ScrollableHeight, null, true);
    /// <summary>--screenshots: the Patreon card at the top.</summary>
    public void ScrollToPatreon() => PatreonCard.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, VerticalOffset = -16, AnimationDesired = false });
    public void ScrollToNewShaders() => NewShadersHeader.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, VerticalOffset = 40, AnimationDesired = false });

    // SetCacheLimit is a global driver setting that needs admin: only after the user's explicit OK.
    async void OnApply(object _, RoutedEventArgs __)
    {
        var (label, limit) = SettingsVm.Sizes[SizeBox.SelectedIndex];
        if (!await App.ConfirmAsync(this, $"将着色器缓存上限设为 {label}？",
                $"这是 {Fmt.Vendor(App.Core.Vendor.Vendor)} 驱动的全局设置：对本机所有游戏生效，而不只是 SCSKiller 编译的游戏，" +
                "并会一直保持到你更改为止。Windows 会请求管理员权限。\n\n" +
                "调高不会删除任何内容。调低到低于当前使用量时，驱动会删除较旧的条目，那些游戏将再次卡顿。" +
                "你可以随时在这里恢复驱动默认值。",
                "更改上限")) return;

        ApplyButton.IsEnabled = false;
        var size = limit.IsDriverDefault ? "default" : limit.Bytes is { } b ? (b / (double)(1L << 30)).ToString(CultureInfo.InvariantCulture) : "unlimited";
        (Vm.ApplyMessage, Vm.ApplySeverity) = await AsAdminAsync(() => { App.Core.Vendor.SetCacheLimit(limit); return ""; }, ["cache", "set", size, .. RunningGpu]) switch
        {
            null => ("已取消。", InfoBarSeverity.Informational),
            { Ok: true } => ($"着色器缓存上限已设为 {label}。", InfoBarSeverity.Success),
            var r => ("未更改上限：" + r.Message, InfoBarSeverity.Error),
        };
        ApplyButton.IsEnabled = true;
        Vm.Refresh();
    }

    // the elevated CLI acts on the adapter this process runs on, not on a GPU picked since
    static string[] RunningGpu => [ScsKiller.AdapterArg, App.Core.Vendor.Gpu.AdapterLuid.ToString("X16")];

    // Admin-only actions: in this process when it is already elevated (or shows the fake), else the bundled CLI behind a
    // UAC prompt (Elevated.Run). Null = the user cancelled the prompt; an exception is a failure like the CLI's.
    static Task<Elevated.Result?> AsAdminAsync(Func<string> inProcess, string[] cliArgs) => Task.Run(() =>
    {
        try
        {
            if (App.Core is not Design.FakeScsKiller && !Elevated.IsAdmin) return Elevated.Run(cliArgs);
            return new Elevated.Result(true, inProcess());
        }
        catch (Exception ex) { return new Elevated.Result(false, ex.Message); }
    });

    // Auto Shader Compilation: a global driver setting + the driver's idle task, admin: only after the user's explicit OK.
    async void OnApplyAuto(object _, RoutedEventArgs __)
    {
        var level = Vm.AutoChoice;
        AutoApplyButton.IsEnabled = false;   // before the first await: one apply at a time
        try
        {
            // Already so (e.g. the NVIDIA App set it): no question, no UAC prompt, nothing written.
            AutoShaderState? now = null;
            try { now = await Task.Run(App.Core.Vendor.GetAutoShaderCompilation); }
            catch (Exception) { }   // unknown: the apply reads it again and reports its own failure
            if (now != null && now.Is(level))
            {
                (Vm.AutoMessage, Vm.AutoSeverity) = (NvidiaBackend.AutoShaderOutcome(level, changed: false), InfoBarSeverity.Success);
                return;
            }
            if (!await App.ConfirmAsync(this, level == AutoShaderCompilation.Off ? "关闭 NVIDIA 自动着色器编译？" : $"开启 NVIDIA 自动着色器编译（{level}）？",
                    "自动着色器编译是 NVIDIA® 的测试版功能。", level == AutoShaderCompilation.Off ? "关闭" : "开启")) return;

            // --for-user: the elevated CLI refuses when UAC elevated another administrator (NvOSC would register the task for them).
            string[] cli = ["nvidia-auto-shader", level.ToString().ToLowerInvariant(), Elevated.ForUserArg, Elevated.CurrentUserSid, .. RunningGpu];
            var result = await AsAdminAsync(() => App.Core.Vendor.SetAutoShaderCompilation(level), cli);
            if (result is { Ok: false } && App.Core is ScsKiller k) k.Log?.Report($"自动着色器编译 {level}：{result.Message}");
            (Vm.AutoMessage, Vm.AutoSeverity) = result switch
            {
                null => ("已取消。", InfoBarSeverity.Informational),
                { Ok: true } r => (r.Message is { Length: > 0 } m ? m : NvidiaBackend.AutoShaderOutcome(level, changed: true), InfoBarSeverity.Success),
                var r => ("无法应用：" + r.Message.Split('\n')[0], InfoBarSeverity.Error),   // NvOSC's full output goes to the log only
            };
        }
        finally
        {
            AutoApplyButton.IsEnabled = true;
            Vm.Refresh();   // re-reads the setting and the task
        }
    }
}
