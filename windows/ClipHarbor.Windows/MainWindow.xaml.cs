using ClipHarbor.Core;
using ClipHarbor.Windows.Interop;
using ClipHarbor.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Windows.Storage;
using Windows.System;

namespace ClipHarbor.Windows;

public sealed partial class MainWindow : Window
{
    public ObservableCollection<ClipRecord> VisibleItems { get; } = [];
    private readonly HistoryStore _store = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipHarbor"));
    private readonly DesktopBridge _desktop;
    private readonly Task _initialization;
    private ClipboardService? _clipboard;
    private CloudSyncService? _sync;
    private ScreenshotWatcher? _screenshots;
    private readonly DispatcherTimer _cleanupTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _noteTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private CancellationTokenSource? _previewCancel;
    private string _filter = "all";
    private bool _ready, _updating, _quick, _dialogOpen, _quitting;
    private ClipRecord? Selected => RecordList.SelectedItem as ClipRecord;
    private string? PreviewPath => PreviewFilePicker.SelectedItem as string ?? PreviewPaths(Selected).FirstOrDefault();

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true; SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new(1120, 720));
        AppWindow.IsShownInSwitchers = false;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _desktop = new DesktopBridge(hwnd);
        _desktop.Command += command => DispatcherQueue.TryEnqueue(async () => await HandleCommandAsync(command));
        AppWindow.Closing += (_, args) => { if (!_quitting) { args.Cancel = true; HideHistory(); } };
        Navigation.SelectedItem = Navigation.MenuItems[0];
        _store.Changed += Refresh;
        _cleanupTimer.Tick += async (_, _) => await GuardAsync(async () => { _store.Prune(); await _store.NotifyAsync(); });
        _noteTimer.Tick += async (_, _) => { _noteTimer.Stop(); if (Selected is { } item) _sync?.MetadataChanged(item); await GuardAsync(_store.SaveAsync); };
        Root.Loaded += (_, _) => { if (!_dialogOpen) SearchBox.Focus(FocusState.Programmatic); };
        Navigation.IsEnabled = false;
        _initialization = InitializeServicesAsync();
    }
    private async Task InitializeServicesAsync()
    {
        await GuardAsync(async () =>
        {
            await _store.LoadAsync();
            _clipboard = new(_store, DispatcherQueue);
            _clipboard.Error += ShowError;
            _sync = new(_store, _clipboard, DispatcherQueue);
            _ = _sync.Initialize();
            _screenshots = new(_store, _clipboard, DispatcherQueue);
            if (!_desktop.SetHotkey(_store.Settings)) ShowError("全局唤起键被其他应用占用，请在设置中更改。托盘仍可打开历史。");
            _ready = true; Navigation.IsEnabled = true; Refresh(); _cleanupTimer.Start();
            if (_store.LoadWarning is { } warning) ShowError(warning);
        });
    }
    public void ShowHistory(bool quick)
    {
        _desktop.RememberTarget(NativeMethods.GetForegroundWindow());
        _quick = quick; AppWindow.Show(); Activate();
        UseButton.Content = quick && _store.Settings.AutoPaste ? "粘贴" : "复制";
        DispatcherQueue.TryEnqueue(() => SearchBox.Focus(FocusState.Programmatic));
    }
    public void HideHistory() { AppWindow.Hide(); }
    private async Task HandleCommandAsync(string command)
    {
        await _initialization;
        switch (command)
        {
            case "quick": if (AppWindow.IsVisible && NativeMethods.IsOwnWindow(NativeMethods.GetForegroundWindow())) HideHistory(); else ShowHistory(true); break;
            case "history": ShowHistory(false); break;
            case "pause": TogglePause(); break;
            case "settings": ShowHistory(false); await ShowSettingsAsync(); break;
            case "quit": await GuardAsync(async () => { _quitting = true; _cleanupTimer.Stop(); _noteTimer.Stop(); _screenshots?.Dispose(); if (_sync is not null) await _sync.DisposeAsync(); _clipboard?.Dispose(); if (_ready) await _store.SaveAsync(); _desktop.Dispose(); Close(); Application.Current.Exit(); }); break;
        }
    }
    private void Refresh()
    {
        if (!_ready || _updating) return;
        var selectedId = Selected?.Id;
        var query = SearchBox.Text;
        var category = (FileFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        IEnumerable<ClipRecord> items = _filter == "frequent" ? _store.Frequent() : _store.Items;
        var records = items.Where(i => (i.SyncSpace is null || i.SyncSpace == _store.ActiveSyncSpace) && (_filter switch
        {
            "all" or "frequent" => true, "favorites" => i.Favorite, "screenshots" => i.ScreenshotPath is not null, _ => i.DisplayKind.ToString() == _filter
        }) && (_filter != "Files" || category == "all" || i.FilePaths.Any(p => FileTypes.Classify(p).ToString() == category)) && i.Matches(query)).ToList();
        _updating = true;
        VisibleItems.Clear(); foreach (var item in records) VisibleItems.Add(item);
        RecordList.SelectedItem = records.FirstOrDefault(i => i.Id == selectedId) ?? records.FirstOrDefault();
        _updating = false;
        EmptyState.Visibility = records.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = string.IsNullOrWhiteSpace(query) ? "等待你的下一次复制" : "没有匹配内容";
        FileFilter.Visibility = _filter == "Files" ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = _store.Paused ? $"记录已暂停 · {records.Count} 条记录" : $"{records.Count} 条记录";
        PauseButton.IsChecked = _store.Paused;
        _desktop.Paused = _store.Paused;
        UpdateDetails();
    }
    private IReadOnlyList<string> PreviewPaths(ClipRecord? item) => item is null ? [] : item.ImageName is { } name ? [_store.ImagePath(name)] : item.FilePaths;
    private void UpdateDetails()
    {
        var item = Selected;
        Details.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        PreviewButton.IsEnabled = item is not null;
        FavoriteButton.IsEnabled = MoreButton.IsEnabled = UseButton.IsEnabled = item is not null;
        _previewCancel?.Cancel(); _previewCancel?.Dispose(); _previewCancel = new();
        PreviewHost.Content = null;
        if (item is null) return;
        _updating = true;
        DetailTitle.Text = item.KindTitle == "文本" || item.KindTitle == "链接" ? item.KindTitle : item.Title;
        FilePathsText.Text = string.Join(Environment.NewLine, item.FilePaths);
        FilePathsText.Visibility = item.FilePaths.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoteBox.Text = item.Note;
        DateText.Text = item.CapturedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        StatisticsText.Text = _store.Settings.LearningEnabled ? $"记录 {item.CaptureCount} 次 · 使用 {item.UseCount} 次" : "";
        FavoriteButton.Content = item.Favorite ? "取消收藏" : "收藏";
        var paths = PreviewPaths(item);
        PreviewFilePicker.ItemsSource = paths;
        PreviewFilePicker.SelectedIndex = paths.Count > 0 ? 0 : -1;
        PreviewFilePicker.Visibility = paths.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        OpenFileButton.Visibility = paths.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _updating = false;
        _ = RenderPreviewAsync(item, _previewCancel.Token);
    }
    private async Task RenderPreviewAsync(ClipRecord item, CancellationToken token)
    {
        try
        {
            UIElement content = PreviewPath is { } path ? await PreviewService.CreateAsync(path, token) : new TextBlock { Text = item.Text ?? "", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            if (!token.IsCancellationRequested && Selected?.Id == item.Id) PreviewHost.Content = content;
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!token.IsCancellationRequested) PreviewHost.Content = new TextBlock { Text = "无法预览：" + e.Message + "\n可尝试打开原文件。", TextWrapping = TextWrapping.Wrap }; }
    }
    private async Task UseSelectedAsync(bool? paste = null, bool plain = false)
    {
        if (Selected is not { } item || _clipboard is null) return;
        var target = _desktop.PasteTarget;
        var shouldPaste = paste ?? (_quick && _store.Settings.AutoPaste);
        await GuardAsync(async () =>
        {
            await _clipboard.CopyAsync(item, plain);
            if (shouldPaste) { HideHistory(); await NativeMethods.PasteAsync(target); }
            else if (_quick) { HideHistory(); if (NativeMethods.IsPasteTarget(target)) NativeMethods.SetForegroundWindow(target); }
        });
    }
    private async Task GuardAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception error) { ShowError(error.Message); }
    }
    private void ShowError(string message)
    {
        if (!DispatcherQueue.HasThreadAccess) { DispatcherQueue.TryEnqueue(() => ShowError(message)); return; }
        Feedback.Message = message; Feedback.IsOpen = true;
        if (!AppWindow.IsVisible) ShowHistory(false);
    }
    private void FocusRecords()
    {
        if (VisibleItems.Count == 0) { SearchBox.Focus(FocusState.Keyboard); return; }
        if (RecordList.SelectedIndex < 0) RecordList.SelectedIndex = 0;
        RecordList.ScrollIntoView(RecordList.SelectedItem);
        if (RecordList.ContainerFromItem(RecordList.SelectedItem) is ListViewItem row) row.Focus(FocusState.Keyboard);
        else RecordList.Focus(FocusState.Keyboard);
    }
    private void TogglePause() { _store.Paused = !_store.Paused; Refresh(); }
    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected) return;
        _filter = (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "all";
        Refresh();
    }
    private async void Navigation_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked) await ShowSettingsAsync();
    }
    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => Refresh();
    private async void SearchBox_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Down) { FocusRecords(); args.Handled = true; }
        else if (args.Key == VirtualKey.Up) { if (Navigation.SelectedItem is NavigationViewItem item) item.Focus(FocusState.Keyboard); args.Handled = true; }
        else if (args.Key == VirtualKey.Enter) { args.Handled = true; await UseSelectedAsync(); }
    }
    private void FileFilter_SelectionChanged(object sender, SelectionChangedEventArgs args) => Refresh();
    private void RecordList_SelectionChanged(object sender, SelectionChangedEventArgs args) { if (!_updating) UpdateDetails(); }
    private async void RecordList_PreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (NativeMethods.GetAsyncKeyState(0x11) < 0 || NativeMethods.GetAsyncKeyState(0x12) < 0) return;
        if (args.Key is VirtualKey.Up or VirtualKey.Down)
        {
            if (VisibleItems.Count > 0) { RecordList.SelectedIndex = Math.Clamp(RecordList.SelectedIndex + (args.Key == VirtualKey.Up ? -1 : 1), 0, VisibleItems.Count - 1); FocusRecords(); }
            args.Handled = true;
        }
        else if (args.Key == VirtualKey.Enter) { args.Handled = true; await UseSelectedAsync(); }
        else if (args.Key == VirtualKey.Space) { args.Handled = true; await ShowPreviewAsync(); }
        else if (args.Key == VirtualKey.Right && Selected is not null) { NoteBox.Focus(FocusState.Keyboard); args.Handled = true; }
        else if (args.Key == VirtualKey.Left) { if (Navigation.SelectedItem is NavigationViewItem item) item.Focus(FocusState.Keyboard); args.Handled = true; }
    }
    private async void RecordList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        DependencyObject? element = args.OriginalSource as DependencyObject;
        while (element is not null && element != RecordList)
        {
            if (element is FrameworkElement { DataContext: ClipRecord item }) { RecordList.SelectedItem = item; args.Handled = true; await UseSelectedAsync(true); return; }
            element = VisualTreeHelper.GetParent(element);
        }
    }
    private void Root_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Escape || _dialogOpen) return;
        if (NoteBox.FocusState != FocusState.Unfocused) FocusRecords();
        else if (!string.IsNullOrEmpty(SearchBox.Text)) { SearchBox.Text = ""; SearchBox.Focus(FocusState.Keyboard); }
        else HideHistory();
        args.Handled = true;
    }
    private void NoteBox_KeyDown(object sender, KeyRoutedEventArgs args) { if (args.Key == VirtualKey.Enter) { FocusRecords(); args.Handled = true; } }
    private void NoteBox_TextChanged(object sender, TextChangedEventArgs args)
    {
        if (_updating || Selected is not { } item) return;
        item.Note = NoteBox.Text; _sync?.MetadataChanged(item); _noteTimer.Stop(); _noteTimer.Start();
    }
    private void PreviewFilePicker_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updating || Selected is not { } item) return;
        _previewCancel?.Cancel(); _previewCancel?.Dispose(); _previewCancel = new();
        _ = RenderPreviewAsync(item, _previewCancel.Token);
    }
    private async void Use_Click(object sender, RoutedEventArgs args) => await UseSelectedAsync();
    private async void CopyPlain_Click(object sender, RoutedEventArgs args) => await UseSelectedAsync(false, true);
    private async void Paste_Click(object sender, RoutedEventArgs args) => await UseSelectedAsync(true);
    private void Pause_Click(object sender, RoutedEventArgs args) => TogglePause();
    private async void Favorite_Click(object sender, RoutedEventArgs args) { if (Selected is { } item) await GuardAsync(async () => { item.Favorite = !item.Favorite; _sync?.MetadataChanged(item); await _store.NotifyAsync(); }); }
    private async void ExcludeLearning_Click(object sender, RoutedEventArgs args) { if (Selected is { } item) await GuardAsync(async () => { item.ExcludedFromLearning = !item.ExcludedFromLearning; await _store.NotifyAsync(); }); }
    private async void Delete_Click(object sender, RoutedEventArgs args) { if (Selected is { } item && await ConfirmAsync("删除这条记录？", "不会删除原文件。")) await GuardAsync(() => _store.DeleteAsync(item)); }
    private async void Clear_Click(object sender, RoutedEventArgs args) { if (await ConfirmAsync("清空普通历史？", "保留收藏，不会删除原文件。")) await GuardAsync(() => _store.ClearAsync(true)); }
    private async void OpenFile_Click(object sender, RoutedEventArgs args) => await GuardAsync(async () =>
    {
        if (PreviewPath is not { } path) return;
        if (Directory.Exists(path)) await Launcher.LaunchFolderAsync(await StorageFolder.GetFolderFromPathAsync(path));
        else await Launcher.LaunchFileAsync(await StorageFile.GetFileFromPathAsync(path));
    });
    private void Reveal_Click(object sender, RoutedEventArgs args)
    {
        var path = Selected?.FilePaths.FirstOrDefault() ?? Selected?.ScreenshotPath;
        if (path is null) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = "/select,\"" + path + "\"", UseShellExecute = true }); }
        catch (Exception e) { ShowError(e.Message); }
    }
    private async void Preview_Click(object sender, RoutedEventArgs args) => await ShowPreviewAsync();
    private async Task ShowPreviewAsync()
    {
        if (_dialogOpen || Selected is not { } item) return;
        await GuardAsync(async () =>
        {
            _dialogOpen = true;
            try
            {
                var content = PreviewPath is { } path ? await PreviewService.CreateAsync(path, CancellationToken.None) : new TextBlock { Text = item.Text ?? "", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                var dialog = new ContentDialog { Title = item.Title, Content = new ScrollViewer { Content = content, MaxHeight = 540 }, CloseButtonText = "关闭", XamlRoot = Root.XamlRoot };
                await dialog.ShowAsync();
            }
            finally { _dialogOpen = false; FocusRecords(); }
        });
    }
    private async Task<bool> ConfirmAsync(string title, string detail)
    {
        if (_dialogOpen) return false;
        _dialogOpen = true;
        try { return await new ContentDialog { Title = title, Content = detail, PrimaryButtonText = "确认删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, XamlRoot = Root.XamlRoot }.ShowAsync() == ContentDialogResult.Primary; }
        finally { _dialogOpen = false; }
    }
    private async Task ShowSettingsAsync()
    {
        await _initialization;
        if (!_ready || _dialogOpen) return;
        _dialogOpen = true;
        try
        {
            var dialog = new SettingsDialog(_store, _screenshots?.Status ?? "", _desktop, _clipboard!, this, _sync!) { XamlRoot = Root.XamlRoot };
            await dialog.ShowAsync();
            _screenshots?.Dispose(); _screenshots = new(_store, _clipboard!, DispatcherQueue);
            UseButton.Content = _quick && _store.Settings.AutoPaste ? "粘贴" : "复制";
            Refresh();
        }
        catch (Exception e) { ShowError(e.Message); }
        finally
        {
            _dialogOpen = false;
            Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(item => item.Tag as string == _filter);
            SearchBox.Focus(FocusState.Programmatic);
        }
    }
}
