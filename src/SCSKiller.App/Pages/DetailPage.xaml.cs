using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using SCSKiller.Core;
using SCSKiller.Core.App;

namespace SCSKiller.App.Pages;

public sealed partial class DetailPage : Page
{
    public DetailVm Vm { get; private set; } = null!;

    public DetailPage()
    {
        InitializeComponent();
        var refresh = new Coalesced(DispatcherQueue, () => Vm.Refresh());
        var progress = new Coalesced(DispatcherQueue, () => Vm.RefreshProgress());
        void OnChanged(GameState s) { if (s.Game.Id == Vm?.Row.Id) refresh.Request(); }   // a scan raises one per game
        // a warm raises a tick every progress report: only this game's move the page, and only its progress
        void OnQueue(QueueItem q)
        {
            if (q.Stage != QueueStage.Warming) refresh.Request();
            else if (q.GameId == Vm?.Row.Id) progress.Request();
        }
        // Refresh too: a change raised while the page wasn't loaded (e.g. the game exited) would show only at the next one
        Loaded += (_, _) => { App.Core.GameChanged += OnChanged; App.Core.QueueChanged += OnQueue; Icons.Failed += refresh.Request; App.Account.Changed += refresh.Request; Vm.Refresh(); Check(); };
        Unloaded += (_, _) => { App.Core.GameChanged -= OnChanged; App.Core.QueueChanged -= OnQueue; Icons.Failed -= refresh.Request; App.Account.Changed -= refresh.Request; };
    }

    // a game the scan hasn't read yet is read now, its state comes back as GameChanged
    void Check()
    {
        if (App.Core is not ScsKiller k) return;
        var (id, name) = (Vm.Row.Id, Vm.Name);
        _ = Task.Run(() =>
        {
            try { k.CheckGame(id); }
            catch (Exception e) { k.Log?.Report($"检查 {name} 失败：{e.Message}"); }
        });
    }

    public ContentDialog PartlyWhyDialog() => new()
    {
        XamlRoot = XamlRoot, Title = $"为什么 {Vm.Name} 只完成了部分编译？", CloseButtonText = "关闭",
        Content = new TextBlock { Text = DetailVm.PartlyCompiledWhy, TextWrapping = TextWrapping.Wrap },
    };

    async void OnPartlyWhy(object _, RoutedEventArgs __) => await App.ShowAsync(PartlyWhyDialog());

    async void OnSetAesKey(object _, RoutedEventArgs __)
    {
        var vm = Vm;
        if (await AesKeyDialog.ShowAsync(XamlRoot, vm.Row.Id, $"为 {vm.Name} 设置 AES 密钥")) vm.RefreshGame();
    }

    static readonly (CompatChoice Choice, string Text)[] ReportChoices =
    [
        (CompatChoice.Gone, "着色器编译卡顿已消除。"),
        (CompatChoice.Better, "着色器编译卡顿有所改善，但仍有少量卡顿。"),
        (CompatChoice.NoHelp, "游戏已编译，但对卡顿没有帮助。"),
        (CompatChoice.Worse, "编译后游戏运行反而更差。"),
        (CompatChoice.NotCompiled, "游戏无法编译。"),
        (CompatChoice.NoStutterBefore, "此游戏在编译前也没有着色器编译卡顿。"),
    ];

    /// <summary>Rate this compile's popup for this PC's earlier report of the build (<paramref name="sent"/>), or its thanks
    /// (<paramref name="thanks"/>); built apart so --screenshots renders each state without a click. Only its Send rating
    /// button sends. What a report carries shows until one was sent from this install (<see cref="SCSKiller.Core.Settings.CompatReportSent"/>).</summary>
    public ContentDialog ReportDialog(CompatSent? sent, bool thanks = false)
    {
        var (vm, id) = (Vm, Vm.Row.Id);
        var dialog = App.Confirm(this, $"为 {vm.Name} 的本次编译评分", null!, "发送评分");
        if (thanks) return Thanked(dialog);
        static TextBlock Text(string t) => new() { Text = t, TextWrapping = TextWrapping.Wrap };
        var choices = new RadioButtons();
        foreach (var (_, t) in ReportChoices) choices.Items.Add(new RadioButton { Content = Text(t) });
        CheckBox Box(string t) => new() { Content = Text(t) };
        var (frameGen, upscaler, mods) = (Box("开启了帧生成"), Box("开启了超分辨率（DLSS、FSR 或 XeSS）"),
            Box("安装了 mod（例如 ReShade、OptiScaler 或 RenoDX）"));
        var problem = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["WarnBrush"], Visibility = Visibility.Collapsed };
        var panel = new StackPanel { Spacing = 12, Children = { Text($"告诉其他玩家 SCSKiller 编译后 {vm.Name} 的运行情况。评分是匿名的。") } };
        if (sent != null) panel.Children.Add(new TextBlock { Text = "你已评价过此版本的游戏，再次评分将覆盖原评价。", TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(choices);
        panel.Children.Add(new StackPanel { Children = { frameGen, upscaler, mods } });
        if (!App.Core.Settings.CompatReportSent)
            panel.Children.Add(new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = "随评分一起发送：CPU 型号、显卡型号、驱动版本、Windows 版本，以及 SCSKiller 为此游戏编译的内容。不含其他信息。",
                        TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["Secondary"] },
                    new HyperlinkButton { Content = "共享内容", NavigateUri = new Uri(PrivacyReports), Padding = new(0) },
                },
            });
        panel.Children.Add(problem);
        dialog.Content = new ScrollViewer { Content = panel };
        dialog.IsPrimaryButtonEnabled = false;   // until a choice is picked
        choices.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = choices.SelectedIndex >= 0;
        dialog.PrimaryButtonClick += async (d, args) =>
        {
            args.Cancel = true;   // the popup stays for the answer
            var deferral = args.GetDeferral();
            d.IsPrimaryButtonEnabled = false;
            string? why;
            // read here: the send runs off the UI thread, where the controls can't be read
            var (choice, fg, up, md) = (ReportChoices[choices.SelectedIndex].Choice, frameGen.IsChecked == true, upscaler.IsChecked == true, mods.IsChecked == true);
            try { why = await Task.Run(() => App.Core.SendReportAsync(id, choice, fg, up, md)); }
            catch (Exception ex) { why = Describe(ex); }
            if (why == null)
            {
                Thanked(d);
                try { App.Core.Settings = App.Core.Settings with { CompatReportSent = true }; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // unsaved: the disclosure shows once more
            }
            else
            {
                (problem.Text, problem.Visibility) = (why, Visibility.Visible);
                d.IsPrimaryButtonEnabled = true;
            }
            deferral.Complete();
        };
        return dialog;

        static ContentDialog Thanked(ContentDialog d)
        {
            (d.Content, d.PrimaryButtonText, d.CloseButtonText) = (Text("谢谢，你的评分已发送。"), "", "关闭");
            return d;
        }
    }

    /// <summary>The privacy page's section on compatibility reports.</summary>
    public const string PrivacyReports = "https://scskiller.com/privacy#compatibility-reports";

    /// <summary>The privacy page's section on problem reports.</summary>
    public const string PrivacyProblems = "https://scskiller.com/privacy#problem-reports";

    /// <summary>"Report a problem": the user's words, then the whole report as it will be sent. Nothing is sent before Send.
    /// <paramref name="sentId"/>: the thanks with that report id (screenshots); <paramref name="message"/> fills the box.</summary>
    public ContentDialog ProblemDialog(string? sentId = null, string? message = null)
    {
        var (vm, id) = (Vm, Vm.Row.Id);
        var store = Fmt.StoreName(vm.Row.State.Game);
        var page = new (string, string)[]
        {
            ("Status", $"{vm.StatusTitle}. {vm.StatusReason}"),
            ("Coverage", vm.CoverageHeadline),
            (vm.TimeLabel, vm.HasTimeValue ? vm.TimeValue : ""),
            (vm.DiskLabel, vm.HasDiskValue ? vm.DiskValue : ""),
            (vm.StuttersLabel, vm.HasStutterTile ? vm.StuttersValue : ""),
            (vm.CompileTitle, vm.HasCompileCard ? vm.CompileText : ""),
            ("上次会话", vm.FramesVerdict),
        }.Concat(vm.Details.Select(d => (d.Label, d.Value))).ToList();
        var dialog = App.Confirm(this, $"报告 {vm.Name} 的问题", null!, "Send");
        if (sentId != null) return Thanked(dialog, sentId);
        static TextBlock Text(string t) => new() { Text = t, TextWrapping = TextWrapping.Wrap };
        var words = new TextBox { Header = "发生了什么？", Text = message ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, MaxLength = ProblemReport.MaxMessageChars };
        var withLog = new CheckBox { Content = Text("附上程序中关于此游戏的日志"), IsChecked = true };
        var report = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 240,
            FontFamily = new FontFamily("Consolas"), FontSize = 12,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(report, ScrollBarVisibility.Auto);
        var problemText = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["WarnBrush"], Visibility = Visibility.Collapsed };
        var built = "";   // what is sent: the TextBox turns line ends into \r
        void Build()
        {
            try { report.Text = built = App.Core.ProblemReport(id, store, page, withLog.IsChecked == true); }
            catch (Exception ex) { report.Text = built = ""; (problemText.Text, problemText.Visibility) = (Describe(ex), Visibility.Visible); }
            Enable();
        }
        void Enable() => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(words.Text) && built.Length > 0;
        dialog.Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    words, new StackPanel { Spacing = 8, Children = { Text("随消息一起发送"), report } }, withLog,
                    new StackPanel
                    {
                        Spacing = 2,
                        Children =
                        {
                            new TextBlock { Text = "发送至 SCSKiller 服务器，保留 90 天，仅用于修复该问题。", TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["Secondary"] },
                            new HyperlinkButton { Content = "发送内容", NavigateUri = new Uri(PrivacyProblems), Padding = new(0) },
                        },
                    },
                    problemText,
                },
            },
        };
        Build();
        withLog.Click += (_, _) => Build();
        words.TextChanged += (_, _) => Enable();
        dialog.PrimaryButtonClick += async (d, args) =>
        {
            args.Cancel = true;   // the popup stays for the answer
            var deferral = args.GetDeferral();
            d.IsPrimaryButtonEnabled = false;
            var (text, body) = (words.Text.Trim().ReplaceLineEndings("\n"), built);
            string? sent, why;
            try { (sent, why) = await Task.Run(() => App.Core.SendProblemAsync(text, body)); }
            catch (Exception ex) { (sent, why) = (null, Describe(ex)); }
            if (sent != null) Thanked(d, sent);
            else
            {
                (problemText.Text, problemText.Visibility) = (why ?? "未发送，请稍后再试。", Visibility.Visible);
                d.IsPrimaryButtonEnabled = true;
            }
            deferral.Complete();
        };
        return dialog;

        static ContentDialog Thanked(ContentDialog d, string reportId)
        {
            (d.PrimaryButtonText, d.CloseButtonText) = ("", "关闭");
            d.Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "谢谢，你的报告已发送。", TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = $"报告编号 {reportId}", IsTextSelectionEnabled = true, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                    new TextBlock { Text = "可在 Discord 或 GitHub 上提及该编号，便于我们找到你的报告。", TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["Secondary"] },
                },
            };
            return d;
        }
    }

    async void OnReportProblem(object _, RoutedEventArgs __) => await App.ShowAsync(ProblemDialog());

    async void OnSendReport(object _, RoutedEventArgs __)
    {
        var id = Vm.Row.Id;
        CompatSent? sent = null;
        try { sent = await Task.Run(() => App.Core.ReportSent(id)); }
        catch (Exception) { }   // not known: the popup says nothing about an earlier report
        await App.ShowAsync(ReportDialog(sent));
    }

    void OnDbTeaser(Microsoft.UI.Xaml.Documents.Hyperlink _, Microsoft.UI.Xaml.Documents.HyperlinkClickEventArgs __)
    {
        if (App.Account.SignedIn) App.Main.NavigateToPatreon();
        else App.SignInWithPatreon();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Vm = new DetailVm((string)e.Parameter);
        Vm.ReadCaches();
        Crumbs.ItemsSource = new[] { "游戏库", Vm.Name };
        Vm.PropertyChanged += (_, _) => DrawFrames();
        Bindings.Update();
        DrawFrames();
    }

    async void OnGameFolder(object _, RoutedEventArgs __)
    {
        var game = Vm.Row.State.Game;
        if (await GameFolderDialog.ShowAsync(XamlRoot, game, $"{game.Name}'s folder", "保存") is not { } folder) return;
        try { await Task.Run(() => App.Core.AddManualGame(game.ExePath, folder)); }   // checks the folder again, then the game
        catch (Exception ex)
        {
            await App.ShowAsync(new ContentDialog { XamlRoot = XamlRoot, Title = "无法设置游戏目录", Content = ex.Message, CloseButtonText = "确定" });
            return;
        }
        Vm.Refresh();
    }

    // Only the name shown: the game's id, which everything SCSKiller keeps of it is stored under, stays.
    async void OnRename(object _, RoutedEventArgs __)
    {
        var box = new TextBox { Text = Vm.Name, Header = "Name", Description = "只会改变 SCSKiller 显示的名称。", MaxLength = 200 };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = $"重命名 {Vm.Name}", Content = box, PrimaryButtonText = "重命名", CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Text.Trim().Length > 0;
        box.Loaded += (_, _) => box.SelectAll();
        if (await App.ShowAsync(dialog) != ContentDialogResult.Primary) return;
        try { Fmt.Rename(Vm.Row.Id, box.Text); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await App.ShowAsync(new ContentDialog { XamlRoot = XamlRoot, Title = "无法重命名游戏", Content = ex.Message, CloseButtonText = "确定" });
            return;
        }
        Vm.Refresh();
        Crumbs.ItemsSource = new[] { "游戏库", Vm.Name };
    }

    async void OnRemoveGame(object _, RoutedEventArgs __)
    {
        var (id, manual) = (Vm.Row.Id, Vm.IsManual);
        if (!await App.ConfirmAsync(this, $"从游戏库移除 {Vm.Name}？",
                manual ? "SCSKiller 会忘记此游戏并从其目录卸载录制器。游戏自带文件会保留，你随时可以重新添加。" : Vm.HideNote,
                "移除")) return;
        // refused while a compile of it runs, and a store game while it runs or its recorder stays in
        try { await Task.Run(() => { if (manual) App.Core.RemoveManualGame(id); else App.Core.HideGame(id); }); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await App.ShowAsync(new ContentDialog { XamlRoot = XamlRoot, Title = "无法移除游戏", Content = ex.Message, CloseButtonText = "确定" });
            return;
        }
        App.Main.Navigate(typeof(LibraryPage));
    }

    void DrawFrames() => Graph.Show(Vm.F is { } f ? FrameData.Of(f) : null);

    /// <summary>Screenshots: the bottom of the page; false when it all fits.</summary>
    public bool ScrollToEnd() => Scroller.ScrollableHeight > 0 && Scroller.ChangeView(null, Scroller.ScrollableHeight, null, true);

    /// <summary>Screenshots: the coverage card's Details and the session's slow frames open.</summary>
    public void ShowDetails() => DetailsExpander.IsExpanded = SlowFramesExpander.IsExpanded = EarlierExpander.IsExpanded = true;

    /// <summary>Screenshots: the compile card with its kept-aside lines; false without them.</summary>
    public bool ScrollToKept() => Vm.HasKept && Scroller.ChangeView(null, KeptPanel.TransformToVisual((UIElement)Scroller.Content).TransformPoint(default).Y - 260, null, true);

    /// <summary>Screenshots: the frame times card at the top; false without one.</summary>
    public bool ScrollToFrames() => Vm.HasFrames && Scroller.ChangeView(null, FramesCard.TransformToVisual((UIElement)Scroller.Content).TransformPoint(default).Y - 12, null, true);

    void OnCrumb(BreadcrumbBar _, BreadcrumbBarItemClickedEventArgs e)
    {
        if (e.Index == 0) App.Main.ShowLibrary();
    }

    async void OnCompile(object _, RoutedEventArgs __)
    {
        var (vm, id) = (Vm, Vm.Row.Id);
        if (vm.OffersCareful)
            try { await Task.Run(() => App.Core.SetCarefulCompile(id, true)); }
            catch (Exception ex) { vm.Error = ex.Message; vm.Refresh(); return; }
        App.Core.Compile(id);
        vm.Refresh();
    }

    async void OnMaxCompile(object _, RoutedEventArgs __)
    {
        var (vm, id) = (Vm, Vm.Row.Id);
        if (!App.Core.Settings.MaxCompileConfirmed)
        {
            if (!await App.ConfirmAsync(this, $"最大编译 {vm.Name}？", DetailVm.MaxCompileWhy, "最大编译")) return;
            App.Core.Settings = App.Core.Settings with { MaxCompileConfirmed = true };
        }
        App.Core.CompileMax(id);
        vm.Refresh();
    }

    // Toggled also fires when the binding sets IsOn: only a user's flip differs from the view model.
    void OnCarefulToggled(object _, RoutedEventArgs __) { if (CarefulSwitch.IsOn != Vm.CarefulOn) SetCareful(CarefulSwitch.IsOn); }

    void OnLeftOutToggled(object _, RoutedEventArgs __)
    {
        var on = LeftOutSwitch.IsOn;
        if (on != Vm.LeftOutOn) Set((vm, p) => vm.LeftOutPending = p, on, id => App.Core.SetGameLeftOutOfAddAll(id, on));
    }

    void SetCareful(bool on) => Set((vm, p) => vm.CarefulPending = p, on, id => App.Core.SetCarefulCompile(id, on));

    async void OnRayTracingChanged(object _, SelectionChangedEventArgs __)
    {
        var (vm, id, i) = (Vm, Vm.Row.Id, RayTracingBox.SelectedIndex);
        if (i < 0 || i == vm.RayTracingIndex) return;
        try
        {
            await Task.Run(() => App.Core.SetGameRayTracing(id, i switch { 1 => true, 2 => false, _ => null }));
            vm.Error = null;
        }
        catch (Exception ex) { vm.Error = Describe(ex); }
        vm.Refresh();
    }

    async void OnNewShadersChanged(object _, SelectionChangedEventArgs __)
    {
        var (vm, id, i) = (Vm, Vm.Row.Id, NewShadersBox.SelectedIndex);
        if (i < 0 || i == vm.NewShadersIndex) return;
        try
        {
            await Task.Run(() => App.Core.SetNewShadersMode(id, i == 0 ? null : (NewShadersMode)(i - 1)));
            vm.Error = null;
        }
        catch (Exception ex) { vm.Error = Describe(ex); }
        vm.Refresh();
    }

    // A binding's own update selects ApiIndex: only a different pick goes to the core.
    void OnApiPicked(object sender, SelectionChangedEventArgs _)
    {
        var i = ((ComboBox)sender).SelectedIndex;
        if (i < 0 || i == Vm.ApiIndex || !Vm.CanPickApi) return;
        DxApi? api = i == 1 ? DxApi.D3D11 : i == 2 ? DxApi.D3D12 : null;
        Set((vm, p) => vm.ApiPending = p, true, id => App.Core.SetApiOverride(id, api));
    }

    void OnGameMaximum(object _, RoutedEventArgs __)
    {
        var on = Vm.MaximumSuggested;
        Set((vm, p) => vm.MaximumPending = p, on, id => App.Core.SetGameMaximum(id, on));
    }

    /// <summary>Never blank: an exception without a message (some WinRT ones) shows its type, and its inner one's.</summary>
    static string Describe(Exception e) => !string.IsNullOrWhiteSpace(e.Message) ? e.Message
        : e.GetType().Name + (e.InnerException is { } inner ? ": " + Describe(inner) : $"（0x{e.HResult:X8}）");

    // The core's switches do file I/O: off the UI thread, the switch disabled meanwhile (pending = the state asked for).
    async void Set(Action<DetailVm, bool?> setPending, bool pending, Action<string> change)
    {
        var (vm, id) = (Vm, Vm.Row.Id);
        setPending(vm, pending);
        vm.Refresh();
        try
        {
            await Task.Run(() => change(id));
            vm.Error = null;
        }
        catch (Exception ex) { vm.Error = Describe(ex); }
        setPending(vm, null);
        vm.Refresh();   // the switch shows the real state, even after a failure
    }

    async void OnClearCache(object _, RoutedEventArgs __)
    {
        var (vm, id) = (Vm, Vm.Row.Id);
        var note = "重新预热前，游戏可能在游玩时再次编译着色器。"
            + (vm.Row.State.AntiCheat != AntiCheat.None ? " 检测到反作弊：这里只清理驱动缓存。" : "");   // VAC: as any game (ScsKiller.GameCaches)
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "正在统计缓存文件…\n\n" + note };
        var ownText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var alsoOwn = new CheckBox { Content = ownText, Visibility = Visibility.Collapsed };
        _ = Measure();
        if (await App.ShowAsync(App.Confirm(this, $"清理 {vm.Name} 的着色器缓存？", new StackPanel { Spacing = 12, Children = { text, alsoOwn } }, "清理缓存"))
            != ContentDialogResult.Primary) return;
        var withOwn = alsoOwn.IsChecked == true;
        try { vm.Error = await Task.Run(() => App.Core.ClearGameCache(id, withOwn)) ? null : "未清理任何文件：没有找到此游戏的着色器缓存。"; }
        catch (Exception ex) { vm.Error = ex.Message; }
        vm.ReadCaches();
        vm.Refresh();

        // the sizes fill in while the dialog is open: reading the Windows cache folders can take seconds; Clear reads them again
        async Task Measure()
        {
            try
            {
                var all = await Task.Run(() => App.Core.GameCaches(id, gamePrecache: true));
                static bool Own(CachePart p) => p.Name is Core.App.ScsKiller.PipelinePart or Core.App.ScsKiller.PrecachePart;
                var (parts, own) = (all.Where(p => !Own(p)).ToList(), all.Where(Own).ToList());
                text.Text = (parts.Count > 0 ? string.Join(" · ", parts.Select(p => $"{p.Name} {Format.Bytes(p.Bytes)}")) : "磁盘上当前没有缓存文件") + ".\n\n" + note;
                ownText.Text = $"同时删除游戏自己的着色器缓存（{Format.Bytes(own.Sum(p => p.Bytes))}）。下次启动时会重新生成，启动时间可能更长。";
                alsoOwn.Visibility = own.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex) { text.Text = Describe(ex) + "\n\n" + note; }
        }
    }

    async void OnBeforeAfter(object _, RoutedEventArgs __)
    {
        var (vm, id) = (Vm, Vm.Row.Id);
        var ba = vm.Row.State.BeforeAfter;
        string? error = ba?.Stage switch
        {
            null => await BeforeAfterUi.StartAsync(XamlRoot, id),
            _ when BeforeAfterUi.Voided(ba) => await BeforeAfterUi.StartAsync(XamlRoot, id, over: true),
            BeforeAfterStage.BeforeArmed when ba!.ClearPending => await BeforeAfterUi.ContinueAsync(id),
            BeforeAfterStage.BeforeArmed or BeforeAfterStage.AfterArmed => await BeforeAfterUi.LaunchAsync(id),
            BeforeAfterStage.AfterReady => await BeforeAfterUi.ContinueAsync(id),
            BeforeAfterStage.Done => await ReportWindow.OpenAsync(id) is null ? "报告已不存在：请重新开始。" : null,
            _ => null,
        };
        Shown(vm, error);
    }

    async void OnBaRecordAgain(object _, RoutedEventArgs __) => Shown(Vm, await BeforeAfterUi.RecordAfterAgainAsync(XamlRoot, Vm.Row.Id));
    async void OnBaStartOver(object _, RoutedEventArgs __) => Shown(Vm, await BeforeAfterUi.StartAsync(XamlRoot, Vm.Row.Id, over: true));
    async void OnBaCancel(object _, RoutedEventArgs __) => Shown(Vm, await BeforeAfterUi.CancelAsync(XamlRoot, Vm.Row.Id));
    async void OnBaHowItWorks(object _, RoutedEventArgs __) => await BeforeAfterUi.IntroAsync(XamlRoot, Vm.Name, BeforeAfterUi.Intro.HowItWorks);
    // Continue in Compiling clears and compiles again, first in the queue
    async void OnBaCompileAgain(object _, RoutedEventArgs __) => Shown(Vm, await BeforeAfterUi.ContinueAsync(Vm.Row.Id));

    static void Shown(DetailVm vm, string? error)
    {
        vm.Error = error;
        vm.Refresh();
    }

    async void OnClearRecording(object _, RoutedEventArgs __)
    {
        var (vm, id) = (Vm, Vm.Row.Id);
        if (!await App.ConfirmAsync(this, $"清理 {vm.Name} 的录制数据？",
                "已录到的管线会被移除；只有在开启录制后由游戏再次创建时才能重新获得，编译计划会退回到游戏文件可提供的内容。"
                + "编译计划会退回到游戏文件可提供的内容。\n\n录制器本身会保留，游戏自带文件不会被改动。",
                "清除录制数据")) return;
        vm.ClearingRecording = true;
        vm.Refresh();
        try { vm.Error = await Task.Run(() => App.Core.ClearRecording(id)) ? null : "未清理任何文件：没有找到此游戏的录制数据。"; }
        catch (Exception ex) { vm.Error = ex.Message; }
        vm.ClearingRecording = false;
        vm.Refresh();
    }

    void OnRecordToggled(object _, RoutedEventArgs __)
    {
        var on = RecordSwitch.IsOn;   // read here: the change runs off the UI thread, where the switch can't be read
        if (on == Vm.RecordOn) return;
        // an eligible anti-cheat game: the switch allows sessions (offline, or a confirmed session in a game with anti-cheat)
        if (Vm.ShowOffline) Set((vm, p) => vm.OfflinePending = p, on, id => App.Core.SetOfflineRecording(id, on));
        else SetRecord(on);
    }

    void OnRecordTip(object _, RoutedEventArgs __) => SetRecord(true);

    void OnUseDefault(object _, RoutedEventArgs __) => SetRecord(null);

    void SetRecord(bool? on) => Set((vm, p) => vm.RecordPending = p, on ?? App.Core.Settings.RecordAllGames,
        id => App.Core.SetRecorderOverride(id, on switch { true => RecorderOverride.On, false => RecorderOverride.Off, null => RecorderOverride.Default }));

    void OnAlongsideToggled(object _, RoutedEventArgs __) { if (AlongsideSwitch.IsOn != Vm.AlongsideOn) SetAlongside(AlongsideSwitch.IsOn); }

    void SetAlongside(bool on) => Set((vm, p) => vm.AlongsidePending = p, on, id => App.Core.SetRecordAlongsideMod(id, on));

    // NoACGate: the game's own permission, asked once (it is the user's risk), then the core installs and reconciles as usual
    async void OnAntiCheatRecordToggled(object _, RoutedEventArgs __)
    {
        var on = AntiCheatRecordSwitch.IsOn;
        if (on == Vm.AntiCheatRecordOn) return;
        if (on && !await App.ConfirmAsync(this, $"允许在 {Vm.Name} 中与反作弊并存录制？",
                new TextBlock { TextWrapping = TextWrapping.Wrap, Text = Vm.AntiCheatRecordNote }, "仍然录制"))
        {
            Vm.Refresh();   // the switch goes back to what the record says
            return;
        }
        Set((vm, p) => vm.AntiCheatRecordPending = p, on, id => App.Core.SetAntiCheatRecording(id, on));
    }

    // Every launch is confirmed here: the core starts a session only with confirmed: true
    async void OnRecordOffline(object _, RoutedEventArgs __)
    {
        var (vm, id) = (Vm, Vm.Row.Id);
        if (vm.Risk) { await RecordRisk(vm, id); return; }
        if (!await App.ConfirmAsync(this, $"以不加载 EasyAntiCheat 的离线模式启动 {vm.Name}？",
                new TextBlock { TextWrapping = TextWrapping.Wrap, Text = DetailVm.OfflineRisk + " 本次会话无法联网游玩。" },
                "启动离线录制会话")) return;
        await StartSession(vm, () => App.Core.StartOfflineSession(id, confirmed: true));
    }

    // A fresh check first, so the confirmation names the anti-cheat that is there now; the core refuses if it finds another.
    async Task RecordRisk(DetailVm vm, string id)
    {
        vm.OfflineStarting = true;   // before the check: the button stays off until the dialog is answered
        vm.Refresh();
        AntiCheat shown;
        try { shown = await App.Core.CheckAntiCheatAsync(id); }
        catch (Exception ex)
        {
            (vm.Error, vm.OfflineStarting) = (Describe(ex), false);
            vm.Refresh();
            return;
        }
        var fresh = vm.Row.State with { AntiCheat = shown };   // VAC stays the state's: the check only names a client anti-cheat
        var ok = await App.ConfirmAsync(this, $"以风险自负的方式录制 {vm.Name}？",
            new TextBlock { TextWrapping = TextWrapping.Wrap, Text = DetailVm.RiskText(fresh) + " 游戏退出后 SCSKiller 会移除其文件；若发生崩溃，则在你下次登录时移除。" },
            "开始录制会话");
        if (!ok)
        {
            vm.OfflineStarting = false;
            vm.Refresh();
            return;
        }
        await StartSession(vm, () => App.Core.StartRiskSession(id, confirmed: true, shown));
    }

    // The start runs off the UI thread; the task it returns ends when the game exited and its files are out.
    async Task StartSession(DetailVm vm, Func<Task> start)
    {
        vm.OfflineStarting = true;
        vm.Refresh();
        Task session;
        try
        {
            session = await Task.Factory.StartNew(start, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
            vm.Error = null;
        }
        catch (Exception ex) { (vm.Error, session) = (Describe(ex), Task.CompletedTask); }
        vm.OfflineStarting = false;
        vm.Refresh();
        try { await session; }
        catch (Exception ex) { vm.Error = Describe(ex); }
        vm.Refresh();
    }
}
