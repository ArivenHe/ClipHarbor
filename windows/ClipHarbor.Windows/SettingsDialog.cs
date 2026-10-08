using ClipHarbor.Core;
using ClipHarbor.Windows.Interop;
using ClipHarbor.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Diagnostics;
using System.Text.Json;
using Windows.Storage.Pickers;
using Windows.System;

namespace ClipHarbor.Windows;

internal sealed class SettingsDialog : ContentDialog
{
    private readonly HistoryStore _store;
    private readonly DesktopBridge _desktop;
    private readonly MainWindow _window;
    private readonly CloudSyncService _sync;
    private readonly AppSettings _draft;
    private readonly InfoBar _feedback = new() { IsOpen = false, Severity = InfoBarSeverity.Error };
    private readonly ToggleSwitch _startup, _autoPaste, _plainText, _text, _images, _files, _favoriteExempt, _screenshots, _learning;
    private readonly NumberBox _limit, _imageLimit, _threshold;
    private readonly TextBox _excluded, _folder;
    private readonly Button _hotkey, _phraseHotkey;
    private readonly NumberBox _retentionValue;
    private readonly ComboBox _retentionUnit;
    private readonly Dictionary<ClipKind, (ToggleSwitch Enabled, NumberBox Value, ComboBox Unit)> _typeRules = [];
    private bool _recording, _recordingPhrase;

    public SettingsDialog(HistoryStore store, string screenshotStatus, DesktopBridge desktop, ClipboardService clipboard, MainWindow window, CloudSyncService sync)
    {
        _store = store; _desktop = desktop; _window = window; _sync = sync;
        _draft = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(store.Settings))!;
        Title = "拾贴设置"; PrimaryButtonText = "保存"; CloseButtonText = "取消"; DefaultButton = ContentDialogButton.Primary;
        Resources["ContentDialogMaxWidth"] = 720d;
        var pages = new Dictionary<string, StackPanel>();
        pages.Add("跨设备同步", SyncPage());
        var general = Page(); pages.Add("通用", general);
        _startup = Switch("登录 Windows 时启动", StartupService.Enabled);
        _autoPaste = Switch("快捷面板回车后自动粘贴", _draft.AutoPaste);
        _plainText = Switch("文本默认以纯文本复制", _draft.PlainText);
        Add(general, Heading("使用方式"), _startup, _autoPaste, _plainText,
            Description("双击记录始终尝试粘贴到原应用。普通历史窗口的回车默认只复制。关闭窗口后继续在托盘记录。"),
            Description("Windows 无需 macOS 辅助功能授权。系统可能阻止向管理员权限窗口模拟粘贴，届时仍可手动粘贴。"), Heading("全局唤起键"));
        _hotkey = new() { Content = HotkeyLabel(_draft.HotkeyModifiers, _draft.HotkeyKey), HorizontalAlignment = HorizontalAlignment.Left };
        _hotkey.Click += (_, _) => { _recordingPhrase = false; _recording = true; _hotkey.Content = "按下组合键，Esc 取消"; _hotkey.Focus(FocusState.Programmatic); };
        var resetHotkey = new Button { Content = "恢复 Ctrl + Alt + V", HorizontalAlignment = HorizontalAlignment.Left };
        resetHotkey.Click += (_, _) => { _draft.HotkeyModifiers = 3; _draft.HotkeyKey = 0x56; _recording = false; UpdateHotkeyLabel(); };
        Add(general, _hotkey, resetHotkey, Description("这只影响全局唤起。Tab、方向键、回车、空格和 Esc 的窗口操作始终可用。"));

        _phraseHotkey = new() { Content = HotkeyLabel(_draft.PhraseHotkeyModifiers, _draft.PhraseHotkeyKey), HorizontalAlignment = HorizontalAlignment.Left };
        _phraseHotkey.Click += (_, _) => { _recordingPhrase = true; _recording = true; _phraseHotkey.Content = "按下组合键，Esc 取消"; _phraseHotkey.Focus(FocusState.Programmatic); };
        var clearPhraseHotkey = new Button { Content = "取消短语快捷键", HorizontalAlignment = HorizontalAlignment.Left };
        clearPhraseHotkey.Click += (_, _) => { _draft.PhraseHotkeyModifiers = _draft.PhraseHotkeyKey = 0; _recording = false; UpdateHotkeyLabel(); };
        Add(general, Heading("直接打开短语面板"), _phraseHotkey, clearPhraseHotkey);

        var recording = Page(); pages.Add("记录与存储", recording);
        _text = Switch("记录文本与链接", _draft.RecordText); _images = Switch("记录图片与图片文件引用", _draft.RecordImages); _files = Switch("记录其他文件引用", _draft.RecordFiles);
        _limit = Number("普通历史数量上限", _draft.HistoryLimit, 100, 10000, 100);
        _imageLimit = Number("单张缓存图片上限（MB）", _draft.ImageLimitMB, 1, 100);
        _favoriteExempt = Switch("收藏免于时间和数量清理", _draft.FavoritesExempt);
        (_retentionValue, _retentionUnit) = RetentionControls(_draft.Retention);
        Add(recording, Heading("记录类型"), _text, _images, _files,
            Description("本地记录保存文件引用；开启文件同步后，会上传文件副本供另一台设备粘贴。PNG、JPEG、HEIC 等图片文件归入图片。"),
            Heading("存储与保留"), _limit, _imageLimit, _favoriteExempt, _retentionValue, _retentionUnit,
            Description("保存较短的保留期限可能清理已有记录。永久只免于时间清理，普通记录仍受数量上限限制；暂停期间也会每 30 秒清理。"), Heading("按类型保留"));
        foreach (var kind in Enum.GetValues<ClipKind>())
        {
            var rule = _draft.TypeRetention.GetValueOrDefault(kind) ?? new();
            var enabled = Switch((new ClipRecord { Kind = kind }).KindTitle + "单独设置", rule.Override);
            var (value, unit) = RetentionControls(rule);
            value.IsEnabled = unit.IsEnabled = enabled.IsOn;
            enabled.Toggled += (_, _) => value.IsEnabled = unit.IsEnabled = enabled.IsOn;
            _typeRules[kind] = (enabled, value, unit);
            Add(recording, enabled, value, unit);
        }
        var dataFolder = Action("打开本地数据目录", () => { Process.Start(new ProcessStartInfo(store.DirectoryPath) { UseShellExecute = true }); return Task.CompletedTask; });
        Add(recording, dataFolder);

        var screenshots = Page(); pages.Add("系统截图", screenshots);
        _screenshots = Switch("保存截图目录中新出现的图片", _draft.WatchScreenshots);
        _folder = new() { Header = "截图目录", PlaceholderText = "默认：图片 / Screenshots", Text = _draft.ScreenshotFolder };
        var choose = Action("选择目录…", async () =>
        {
            var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_window));
            var folder = await picker.PickSingleFolderAsync(); if (folder is not null) _folder.Text = folder.Path;
        });
        var resetFolder = Action("使用默认截图目录", () => { _folder.Text = ""; return Task.CompletedTask; });
        Add(screenshots, Heading("截图自动保存"), _screenshots, Description(screenshotStatus), _folder, choose, resetFolder,
            Description("Win + Shift + S 复制到剪贴板的截图由剪贴板监测接收；Win + Print Screen 或截图工具保存的文件由此目录监听接收。"),
            Description("首次启用不导入已有图片。图片缓存独立于原截图；删除原截图后仍可从拾贴复制。全局暂停会同时暂停截图监听。自选目录会监听其中所有新图片。"));

        var learning = Page(); pages.Add("常用内容", learning);
        _learning = Switch("在本机整理常用内容", _draft.LearningEnabled);
        _threshold = Number("进入常用列表的累计次数", _draft.LearningThreshold, 2, 20);
        var resetLearning = Action("重置学习次数", async () =>
        {
            foreach (var item in _store.Items) { item.CaptureCount = 1; item.UseCount = 0; item.LastUsedAt = null; }
            await _store.NotifyAsync(); ShowStatus("学习次数已重置，内容、收藏和备注已保留。");
        });
        Add(learning, Heading("本地学习"), _learning, _threshold,
            Description("使用次数在本机计算，不接入 AI 服务。开启同步后，选中的内容类型会上传到你配置的服务器。疑似密钥、密码和验证码会排除推荐；这不能识别所有敏感内容。"), resetLearning);

        var privacy = Page(); pages.Add("隐私与清理", privacy);
        _excluded = new() { Header = "排除应用", Text = _draft.ExcludedApps, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 130, PlaceholderText = "每行一个程序名，例如 KeePass.exe" };
        var cleanup = Action("按已保存的设置清理过期记录", async () => { _store.Prune(); await _store.NotifyAsync(); ShowStatus("已按保存的保留策略清理。"); });
        var confirmation = new CheckBox { Content = "确认删除全部历史、收藏及图片缓存" };
        var delete = Action("删除全部本地历史", async () => { await _store.ClearAsync(false); confirmation.IsChecked = false; ShowStatus("本地历史已清空，原文件未删除。"); });
        delete.IsEnabled = false;
        confirmation.Checked += (_, _) => delete.IsEnabled = true;
        confirmation.Unchecked += (_, _) => delete.IsEnabled = false;
        Add(privacy, Heading("排除与隐私"), _excluded,
            Description("本地数据在 LocalAppData / ClipHarbor，历史没有应用级加密。开启同步后会通过 HTTPS 上传到指定服务器；服务器管理员可读取内容。登录令牌存入 Windows 凭据管理器。声明的敏感剪贴板标记会跳过记录，但无法识别所有密码。"), Heading("清理"), cleanup, confirmation, delete);

        var about = Page(); pages.Add("关于", about);
        about.Children.Add(new Image { Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/ClipHarbor.png")), Width = 88, Height = 88, HorizontalAlignment = HorizontalAlignment.Left });
        Add(about, Heading("拾贴 · ClipHarbor"), Description($"Windows 版 · WinUI 3 · {typeof(App).Assembly.GetName().Version?.ToString(3)}\n复制即收纳，随时找回来。"),
            Action("GitHub 项目", async () => { await Launcher.LaunchUriAsync(new Uri("https://github.com/ArivenHe/ClipHarbor")); }),
            Description("图片、PDF、文本及系统支持的音视频可直接预览。Office 等其他格式可打开关联应用。HEIC 等格式可能需要 Windows 图像扩展。"),
            Description("便携目录移动后，登录启动的路径需要在这里重新保存。退出程序可使用系统托盘菜单。"));
        var navigation = new ComboBox { Header = "设置页面", ItemsSource = pages.Keys.ToList(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var scroll = new ScrollViewer { Content = pages.Values.First(), MaxHeight = 440, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, IsTabStop = false };
        navigation.SelectionChanged += (_, _) => { if (navigation.SelectedItem is string key) { scroll.Content = pages[key]; scroll.ChangeView(null, 0, null); } };
        var layout = new StackPanel { Spacing = 12, Width = 580 };
        Add(layout, navigation, _feedback, scroll);
        Content = layout;
        PreviewKeyDown += RecordHotkey;
        PrimaryButtonClick += Save_Click;
    }
    private StackPanel SyncPage()
    {
        var page = Page(); var config = _sync.Config;
        var url = new TextBox { Header = "服务器地址", PlaceholderText = "https://sync.example.com", Text = config.ServerUrl };
        var user = new TextBox { Header = "账号", Text = config.Username };
        var password = new PasswordBox { Header = "密码" };
        var device = new TextBox { Header = "设备名称", Text = config.DeviceName };
        var remember = Switch("保持登录", config.KeepSignedIn);
        var import = new ComboBox { Header = "首次登录时导入本机历史", ItemsSource = new[] { "只同步之后新复制的内容", "导入已有收藏", "导入已有全部历史" }, SelectedIndex = 0 };
        var state = Description(_sync.Status);
        void Update() { state.Text = _sync.Status; }
        _sync.Changed += Update; Closed += (_, _) => _sync.Changed -= Update;
        var enabled = Switch("自动同步", config.Enabled); var direct = Switch("跨设备直接粘贴", config.DirectPaste);
        var phrases = Switch("同步快捷短语", config.SyncPhrases);
        var text = Switch("同步文本与链接", config.SyncText); var images = Switch("同步图片", config.SyncImages); var files = Switch("同步文件", config.SyncFiles);
        Add(page, Heading("自建服务器"), url, user, password, device, remember, import,
            Action("测试连接", async () => { await CloudSyncService.Test(url.Text); ShowStatus("连接成功，服务器协议兼容。"); }),
            Action("登录并启用同步", async () => { await _sync.Login(url.Text, user.Text, password.Password, device.Text, remember.IsOn, import.SelectedIndex); password.Password = ""; enabled.IsOn = true; ShowStatus("已登录，正在同步。"); }),
            Heading("同步方式"), enabled, direct, text, images, files, phrases,
            Description("快捷短语：" + _sync.Phrases.Status),
            Action("打开快捷短语", () => { Hide(); _window.OpenPhrases(); return Task.CompletedTask; }),
            Description("另一台设备复制后，在此电脑直接 Ctrl + V。文件下载完成前保留当前剪贴板；单文件上限 100 MB，一次最多 100 个文件、500 MB。第一版支持普通文件。连接期间只接收新复制的内容，重连补历史。"),
            Action("应用同步选项", async () =>
            {
                var next = ClipHarbor.Sync.Protocol.Decode<ClipHarbor.Sync.SyncConfig>(ClipHarbor.Sync.Protocol.Encode(_sync.Config));
                next.Enabled = enabled.IsOn; next.DirectPaste = direct.IsOn; next.SyncText = text.IsOn; next.SyncImages = images.IsOn; next.SyncFiles = files.IsOn; next.SyncPhrases = phrases.IsOn; next.KeepSignedIn = remember.IsOn;
                await _sync.Configure(next); ShowStatus("同步选项已保存。");
            }), state,
            Action("立即补同步历史", async () => { await _sync.SyncNow(); ShowStatus("历史已补同步。"); }),
            Action("查看未同步内容与冲突草稿", () => { if (_sync.Conflicts() is { } path) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); return Task.CompletedTask; }),
            Action("退出登录", async () => { await _sync.Logout(); enabled.IsOn = false; }),
            Description("这些按钮立即生效。服务器地址、账号和设备名称在登录时保存；密码只用于登录，不写入设置文件。关闭此窗口不会撤销已经应用的同步设置。"));
        return page;
    }
    private static StackPanel Page() => new() { Spacing = 12 };
    private static TextBlock Heading(string title) => new() { Text = title, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0) };
    private static TextBlock Description(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
    private static ToggleSwitch Switch(string title, bool value) => new() { Header = title, IsOn = value, OnContent = "开启", OffContent = "关闭" };
    private static NumberBox Number(string title, int value, int min, int max, int step = 1) => new() { Header = title, Value = value, Minimum = min, Maximum = max, SmallChange = step, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
    private static void Add(StackPanel panel, params UIElement[] children) { foreach (var child in children) panel.Children.Add(child); }
    private static (NumberBox, ComboBox) RetentionControls(RetentionRule rule)
    {
        var value = Number("保留数量", rule.Value, 1, 100000);
        var unit = new ComboBox { Header = "保留单位", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var pair in new[] { ("分钟", "minutes"), ("小时", "hours"), ("天", "days"), ("周", "weeks"), ("永久", "forever") }) unit.Items.Add(new ComboBoxItem { Content = pair.Item1, Tag = pair.Item2 });
        unit.SelectedItem = unit.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == rule.Unit) ?? unit.Items[2];
        return (value, unit);
    }
    private Button Action(string title, Func<Task> callback)
    {
        var button = new Button { Content = title, HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += async (_, _) => { try { await callback(); } catch (Exception e) { ShowError(e.Message); } };
        return button;
    }
    private void ShowError(string message) { _feedback.Severity = InfoBarSeverity.Error; _feedback.Message = message; _feedback.IsOpen = true; }
    private void ShowStatus(string message) { _feedback.Severity = InfoBarSeverity.Success; _feedback.Message = message; _feedback.IsOpen = true; }
    private static int IntValue(NumberBox number, int fallback) => double.IsFinite(number.Value) ? (int)Math.Clamp(number.Value, number.Minimum, number.Maximum) : fallback;
    private static RetentionRule Rule(NumberBox value, ComboBox unit, bool enabled = true) => new() { Override = enabled, Value = IntValue(value, 30), Unit = (unit.SelectedItem as ComboBoxItem)?.Tag as string ?? "days" };
    private async void Save_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        args.Cancel = true;
        try
        {
            if (_recording) throw new InvalidOperationException("请先完成或取消快捷键录制。");
            _draft.RecordText = _text.IsOn; _draft.RecordImages = _images.IsOn; _draft.RecordFiles = _files.IsOn;
            _draft.AutoPaste = _autoPaste.IsOn; _draft.PlainText = _plainText.IsOn; _draft.FavoritesExempt = _favoriteExempt.IsOn;
            _draft.HistoryLimit = IntValue(_limit, 1000); _draft.ImageLimitMB = IntValue(_imageLimit, 20);
            _draft.LearningEnabled = _learning.IsOn; _draft.LearningThreshold = IntValue(_threshold, 2);
            _draft.WatchScreenshots = _screenshots.IsOn; _draft.ScreenshotFolder = _folder.Text.Trim(); _draft.ExcludedApps = _excluded.Text;
            _draft.Retention = Rule(_retentionValue, _retentionUnit);
            foreach (var (kind, editor) in _typeRules) _draft.TypeRetention[kind] = Rule(editor.Value, editor.Unit, editor.Enabled.IsOn);
            if (!_desktop.SetHotkey(_draft)) { _desktop.SetHotkey(_store.Settings); throw new InvalidOperationException("唤起键已被系统或其他应用占用，请更换组合键。"); }
            try { StartupService.SetEnabled(_startup.IsOn); }
            catch { _desktop.SetHotkey(_store.Settings); throw; }
            _draft.Sync = _store.Settings.Sync;
            await _store.UpdateSettingsAsync(_draft);
            args.Cancel = false;
        }
        catch (Exception e) { ShowError(e.Message); }
        finally { deferral.Complete(); }
    }
    private void RecordHotkey(object sender, KeyRoutedEventArgs args)
    {
        if (!_recording) return;
        args.Handled = true;
        if (args.Key == VirtualKey.Escape) { _recording = false; UpdateHotkeyLabel(); return; }
        if (args.Key is VirtualKey.Control or VirtualKey.Menu or VirtualKey.Shift or VirtualKey.LeftWindows or VirtualKey.RightWindows) return;
        uint modifiers = 0;
        if (NativeMethods.GetAsyncKeyState(0x12) < 0) modifiers |= 1;
        if (NativeMethods.GetAsyncKeyState(0x11) < 0) modifiers |= 2;
        if (NativeMethods.GetAsyncKeyState(0x10) < 0) modifiers |= 4;
        if (NativeMethods.GetAsyncKeyState(0x5B) < 0 || NativeMethods.GetAsyncKeyState(0x5C) < 0) modifiers |= 8;
        if ((modifiers & 11) == 0) { ShowError("全局唤起键需要包含 Ctrl、Alt 或 Win。"); return; }
        if (_recordingPhrase) { _draft.PhraseHotkeyModifiers = modifiers; _draft.PhraseHotkeyKey = (uint)args.Key; } else { _draft.HotkeyModifiers = modifiers; _draft.HotkeyKey = (uint)args.Key; } _recording = false; UpdateHotkeyLabel();
    }
    private void UpdateHotkeyLabel() { _hotkey.Content = HotkeyLabel(_draft.HotkeyModifiers, _draft.HotkeyKey); if (_phraseHotkey is not null) _phraseHotkey.Content = HotkeyLabel(_draft.PhraseHotkeyModifiers, _draft.PhraseHotkeyKey); }
    private static string HotkeyLabel(uint modifiers, uint key) => key == 0 ? "未分配 · 点击录制" : string.Join(" + ", new[] { (2u, "Ctrl"), (1u, "Alt"), (4u, "Shift"), (8u, "Win") }.Where(p => (modifiers & p.Item1) != 0).Select(p => p.Item2).Append(((VirtualKey)key).ToString()));
}
