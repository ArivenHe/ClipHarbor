using ClipHarbor.Core;
using ClipHarbor.Sync;
using ClipHarbor.Windows.Interop;
using ClipHarbor.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Windows.System;
using System.Text;

namespace ClipHarbor.Windows;

internal sealed class QuickPhrasePage : UserControl
{
    private readonly CloudSyncService sync;
    private readonly Func<string, bool?, Task<bool>> use;
    private readonly Func<Task> login;
    private readonly Action close;
    private PresetCatalog catalog = new();
    private PhraseLocalState? local;
    private readonly TextBox search = new() { PlaceholderText = "搜索标题、别名或正文" };
    private readonly ComboBox section = new() { ItemsSource = new[] { "全部", "我的短语", "预置短语", "置顶", "未分组" }, SelectedIndex = 0 };
    private readonly ComboBox category = new() { MinWidth = 120 };
    private readonly CheckBox hidden = new() { Content = "显示已隐藏" };
    private readonly ListView list = new() { SelectionMode = ListViewSelectionMode.Single, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel details = new() { Spacing = 12 };
    private readonly InfoBar feedback = new() { IsOpen = false, Severity = InfoBarSeverity.Error };
    private readonly TextBlock state = Text("");
    private readonly TextBlock account = Text("");
    private readonly Button groups = new() { Content = "分组" }, drafts = new() { Content = "未保存草稿" }, conflicts = new() { Content = "冲突草稿" };
    private string? space;
    private bool refreshing;
    private ContentDialog? dialog;
    private PhraseEntity? pendingEditor;
    public bool DialogOpen => dialog is not null;
    public QuickPhrasePage(CloudSyncService sync, string directory, Func<string, bool?, Task<bool>> use, Func<Task> login, Action close)
    {
        this.sync = sync; this.use = use; this.login = login; this.close = close;
        try { catalog = PresetCatalog.Load(); } catch (Exception e) { Error("预置目录读取失败：" + e.Message); }
        try { local = new(directory); } catch (Exception e) { Error("本机统计或草稿读取失败，原文件已保留：" + e.Message); }
        var root = new Grid { RowSpacing = 12, Padding = new Thickness(20, 8, 20, 16) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) root.RowDefinitions.Add(new() { Height = height });
        var toolbar = new Grid { ColumnSpacing = 8 }; toolbar.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        toolbar.Children.Add(search); var add = Button("新建", () => Edit(new())); Grid.SetColumn(add, 1); toolbar.Children.Add(add); Grid.SetColumn(groups, 2); toolbar.Children.Add(groups); root.Children.Add(toolbar);
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        filters.Children.Add(section); filters.Children.Add(category); filters.Children.Add(hidden); filters.Children.Add(account); filters.Children.Add(Button("账号／同步", login));
        var messages = new StackPanel { Spacing = 8 }; messages.Children.Add(filters); messages.Children.Add(feedback); Grid.SetRow(messages, 1); root.Children.Add(messages);
        var middle = new Grid { ColumnSpacing = 20 }; middle.ColumnDefinitions.Add(new() { Width = new(1.25, GridUnitType.Star) }); middle.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        list.ItemTemplate = (DataTemplate)XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel Spacing='6' Padding='4,10'><TextBlock Text='{Binding Entity.Title}' FontWeight='SemiBold' TextTrimming='CharacterEllipsis'/><TextBlock Text='{Binding Preview}' MaxLines='1' TextTrimming='CharacterEllipsis'/><TextBlock Text='{Binding Metadata}' FontSize='12'/></StackPanel></DataTemplate>");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(list, "快捷短语列表");
        middle.Children.Add(list); var preview = new ScrollViewer { Content = details, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, IsTabStop = false }; Grid.SetColumn(preview, 1); middle.Children.Add(preview); Grid.SetRow(middle, 2); root.Children.Add(middle);
        var footer = new StackPanel { Spacing = 8 }; var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; actions.Children.Add(drafts); actions.Children.Add(conflicts); actions.Children.Add(Button("复制", () => Use(false))); actions.Children.Add(Button("粘贴", () => Use(true))); footer.Children.Add(actions); footer.Children.Add(state); Grid.SetRow(footer, 3); root.Children.Add(footer);
        Content = root;
        search.TextChanged += (_, _) => Refresh(); search.KeyDown += async (_, e) => { if (e.Key == VirtualKey.Down) { FocusList(); e.Handled = true; } else if (e.Key == VirtualKey.Enter) { await Use(); e.Handled = true; } };
        section.SelectionChanged += (_, _) => Refresh(); category.SelectionChanged += (_, _) => Refresh(); hidden.Checked += (_, _) => Refresh(); hidden.Unchecked += (_, _) => Refresh();
        list.RightTapped += (_, args) => { DependencyObject? node = args.OriginalSource as DependencyObject; while (node is not null && node != list) { if (node is FrameworkElement { DataContext: PhraseRow row }) { list.SelectedItem = row; break; } node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node); } };
        list.SelectionChanged += (_, _) => Details(); list.DoubleTapped += async (_, e) => { if (list.SelectedItem is PhraseRow) { e.Handled = true; await Use(true); } };
        list.KeyDown += async (_, e) => { if (e.Key == VirtualKey.Enter) { e.Handled = true; await Use(); } };
        KeyDown += (_, e) => { if (DialogOpen) return; if (e.Key == VirtualKey.F && NativeMethods.GetAsyncKeyState(0x11) < 0) { FocusSearch(); e.Handled = true; } else if (e.Key == VirtualKey.Escape) { if (search.Text.Length > 0) { search.Text = ""; FocusSearch(); } else close(); e.Handled = true; } };
        sync.PhrasesChanged += Refresh; Loaded += (_, _) => { Refresh(); FocusSearch(); };
        Refresh();
    }
    private static TextBlock Text(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private Button Button(string title, Func<Task> action)
    {
        var button = new Button { Content = title, HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += async (_, _) => { try { await action(); } catch (Exception e) { Error(e.Message); } }; return button;
    }
    private void Error(string value) { feedback.Message = value; feedback.IsOpen = true; }
    public void FocusSearch() => search.Focus(FocusState.Programmatic);
    private void FocusList() { if (list.Items.Count == 0) return; if (list.SelectedIndex < 0) list.SelectedIndex = 0; list.ScrollIntoView(list.SelectedItem); if (list.ContainerFromItem(list.SelectedItem) is ListViewItem item) item.Focus(FocusState.Keyboard); else list.Focus(FocusState.Keyboard); }
    private List<PhraseEntity> Groups => sync.Phrases.Entities.Where(e => e.Kind == "group").OrderBy(e => e.Title).ToList();
    private void Refresh()
    {
        if (refreshing) return; refreshing = true;
        try
        {
            if (space != sync.PhraseSpace) { var previousSpace = space; space = sync.PhraseSpace; dialog?.Hide(); search.Text = ""; category.SelectedIndex = 0; hidden.IsChecked = false; list.SelectedItem = null; if (previousSpace is not null) pendingEditor = null; }
            var selectedId = (list.SelectedItem as PhraseRow)?.Id;
            var filter = category.SelectedItem as string ?? "全部分类";
            var categories = new[] { "全部分类" }.Concat(catalog.Phrases.Select(p => p.CategoryName).Concat(Groups.Select(g => g.Title)).Distinct().Order()).ToList();
            category.ItemsSource = categories; category.SelectedItem = categories.Contains(filter) ? filter : "全部分类";
            var sectionKey = new[] { "all", "mine", "preset", "pinned", "ungrouped" }[Math.Max(0, section.SelectedIndex)]; hidden.Visibility = sectionKey == "preset" ? Visibility.Visible : Visibility.Collapsed;
            var rows = QuickPhraseBrowser.Rows(catalog, sync.Phrases, sectionKey, filter == "全部分类" ? "all" : filter, search.Text, hidden.IsChecked == true, local?.Usage(space));
            list.ItemsSource = rows; list.SelectedItem = rows.FirstOrDefault(r => r.Id == selectedId);
            state.Text = $"{rows.Count} 条短语 · {sync.Phrases.Status}" + (rows.Count == 0 ? "\n没有匹配的短语，请调整关键词。" : ""); account.Text = space is null ? "未登录" : sync.Config.Username; groups.IsEnabled = sync.CanEditPhrases;
            var groupMenu = new MenuFlyout(); void MenuAction(MenuFlyout menu, string label, Func<Task> action) { var item = new MenuFlyoutItem { Text = label }; item.Click += async (_, _) => { try { await action(); } catch (Exception e) { Error(e.Message); } }; menu.Items.Add(item); }
            MenuAction(groupMenu, "新建个人分组", () => Edit(new() { Kind = "group" }));
            foreach (var group in Groups) { var submenu = new MenuFlyoutSubItem { Text = group.Title }; var rename = new MenuFlyoutItem { Text = "重命名" }; rename.Click += async (_, _) => await Edit(group); submenu.Items.Add(rename); var delete = new MenuFlyoutItem { Text = "删除分组" }; delete.Click += async (_, _) => await Delete(group); submenu.Items.Add(delete); groupMenu.Items.Add(submenu); } groups.Flyout = groupMenu;
            var draftMenu = new MenuFlyout(); if (space is not null && local is not null) foreach (var draft in local.Drafts(space)) MenuAction(draftMenu, draft.Title.Length == 0 ? "未命名短语" : draft.Title, () => Edit(draft)); drafts.Flyout = draftMenu; drafts.IsEnabled = draftMenu.Items.Count > 0;
            var conflictMenu = new MenuFlyout(); foreach (var failure in sync.Phrases.Failures) MenuAction(conflictMenu, failure.Operation.Entity.Title, () => Resolve(failure)); conflicts.Flyout = conflictMenu; conflicts.Content = $"冲突草稿 ({sync.Phrases.Failures.Count})"; conflicts.IsEnabled = conflictMenu.Items.Count > 0;
            Details();
        }
        catch (Exception e) { Error("读取短语失败，原数据已保留：" + e.Message); }
        finally { refreshing = false; }
    }
    private void Details()
    {
        details.Children.Clear(); list.ContextFlyout = null; if (list.SelectedItem is not PhraseRow row) return;
        details.Children.Add(new TextBlock { Text = row.Entity.Title, FontSize = 22, TextWrapping = TextWrapping.Wrap }); details.Children.Add(Text(row.Metadata)); details.Children.Add(Text(row.Entity.Body)); details.Children.Add(Text($"使用 {local?.Usage(space).GetValueOrDefault(row.Id)?.Count ?? 0} 次"));
        var edit = Button(row.Personal ? "编辑" : "自定义", () => Edit(row.Personal ? row.Entity : Customize(row.Preset!))); details.Children.Add(edit);
        var pin = Button(row.Entity.Pinned ? "取消置顶" : "置顶", async () => { var entity = row.Personal ? row.Entity.Copy() : Preference(row.Entity.Id); entity.Pinned = !entity.Pinned; await Save(entity); }); pin.IsEnabled = sync.CanEditPhrases; details.Children.Add(pin);
        if (row.Personal)
        {
            details.Children.Add(Button("复制一份", () => { var copy = row.Entity.Copy(); copy.Id = Guid.NewGuid().ToString(); copy.Revision = "0"; copy.Alias = null; copy.OriginPresetId = null; copy.OriginPresetVersion = null; copy.Title = string.Concat(copy.Title.EnumerateRunes().Take(77)) + " 副本"; return Edit(copy); }));
            if (row.Entity.OriginPresetId is { } source)
            {
                var preset = catalog.Phrases.FirstOrDefault(p => p.PresetId == source);
                if (preset is null) details.Children.Add(Text("原预置已下架"));
                else
                {
                    details.Children.Add(new Expander { Header = row.Entity.OriginPresetVersion < preset.PresetVersion ? "预置有更新，查看原文" : "查看预置原文", Content = Text(preset.Body), HorizontalAlignment = HorizontalAlignment.Stretch });
                    details.Children.Add(Button("恢复预置正文", async () => { var expected = space; if (await Confirm("恢复预置正文？", "替换正文并保留个人标题、别名、分组与置顶。")) { var entity = row.Entity.Copy(); entity.Body = preset.Body; entity.OriginPresetVersion = preset.PresetVersion; await Save(entity, expected); } }));
                }
            }
            details.Children.Add(Button("删除个人短语", () => Delete(row.Entity)));
        }
        else
        {
            var hide = Button(row.Entity.Hidden ? "恢复显示" : "隐藏这条预置短语", async () => { var entity = Preference(row.Entity.Id); entity.Hidden = !entity.Hidden; await Save(entity); }); hide.IsEnabled = sync.CanEditPhrases; details.Children.Add(hide);
            if (sync.Phrases.Entities.FirstOrDefault(e => e.Kind == "phrase" && e.OriginPresetId == row.Entity.Id) is { } personal) details.Children.Add(Button("编辑已有个人版本", () => Edit(personal)));
        }
        var context = new MenuFlyout(); var copyItem = new MenuFlyoutItem { Text = "复制" }; copyItem.Click += async (_, _) => await Use(false); context.Items.Add(copyItem); var editItem = new MenuFlyoutItem { Text = row.Personal ? "编辑" : "自定义" }; editItem.Click += async (_, _) => await Edit(row.Personal ? row.Entity : Customize(row.Preset!)); context.Items.Add(editItem); list.ContextFlyout = context;
    }
    private PhraseEntity Preference(string id) => sync.Phrases.Entities.FirstOrDefault(e => e.Kind == "preference" && e.Id == id)?.Copy() ?? new() { Kind = "preference", Id = id };
    private PhraseEntity Customize(PresetPhrase preset) => sync.Phrases.Entities.FirstOrDefault(e => e.Kind == "phrase" && e.OriginPresetId == preset.PresetId)?.Copy() ?? new() { Title = preset.Title, Body = preset.Body, Alias = preset.Alias, OriginPresetId = preset.PresetId, OriginPresetVersion = preset.PresetVersion, GroupId = Groups.FirstOrDefault(g => string.Equals(g.Title, preset.CategoryName, StringComparison.OrdinalIgnoreCase))?.Id };
    private async Task Use(bool? paste = null) { if (list.SelectedItem is not PhraseRow row) return; var expected = space; try { if (await use(row.Entity.Body, paste)) { local?.Used(expected, row); Refresh(); } } catch (Exception e) { Error(e.Message); } }
    private Task Save(PhraseEntity entity, string? expected = null, PhraseEntity? newGroup = null, bool delete = false) => sync.SavePhrase(entity, expected ?? space ?? throw new InvalidDataException("请先登录账号。"), newGroup, delete);
    private async Task Delete(PhraseEntity entity)
    {
        var expected = space; var message = entity.Kind == "group" ? $"{sync.Phrases.Entities.Count(e => e.GroupId == entity.Id)} 条个人短语移到未分组。" : "会从此账号其他设备删除。" + (entity.OriginPresetId is null ? "" : "对应预置未隐藏时会重新展示。");
        try { if (await Confirm("确认删除？", message)) await Save(entity, expected, delete: true); } catch (Exception e) { Error(e.Message); }
    }
    private async Task<bool> Confirm(string title, string body)
    {
        if (DialogOpen) return false; dialog = new() { Title = title, Content = body, PrimaryButtonText = "确认", CloseButtonText = "取消", XamlRoot = XamlRoot, DefaultButton = ContentDialogButton.Close };
        try { return await dialog.ShowAsync() == ContentDialogResult.Primary; } finally { dialog = null; }
    }
    public async Task NewFromHistory(string text)
    {
        var title = text.Split('\n', '\r').FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? "";
        await Edit(new() { Title = string.Concat(title.EnumerateRunes().Take(80)), Body = text });
    }
    private async Task Edit(PhraseEntity source)
    {
        if (DialogOpen) return;
        if (!sync.CanEditPhrases)
        {
            if (space is not null) { Error(sync.Phrases.Status); return; }
            pendingEditor = source; await login(); if (sync.CanEditPhrases && pendingEditor is { } pending) { pendingEditor = null; await Edit(pending); } return;
        }
        var expected = space!; var draft = source.Copy(); var original = Protocol.Encode(source); var finished = false; var discarding = false;
        var title = new TextBox { Header = draft.Kind == "group" ? "分组名称" : "标题", Text = draft.Title };
        var body = new TextBox { Header = "正文", Text = draft.Body, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 160, MaxHeight = 260 };
        var alias = new TextBox { Header = "检索别名（可选）", Text = draft.Alias ?? "" };
        var group = new ComboBox { Header = "分组", HorizontalAlignment = HorizontalAlignment.Stretch }; group.Items.Add(new ComboBoxItem { Content = "未分组", Tag = "" }); foreach (var g in Groups) group.Items.Add(new ComboBoxItem { Content = g.Title, Tag = g.Id }); group.SelectedItem = group.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == draft.GroupId) ?? group.Items[0];
        var newGroup = new TextBox { Header = "新建分组（可选）", Text = local?.GroupName(expected, draft.Id) ?? "" }; var pinned = new CheckBox { Content = "置顶", IsChecked = draft.Pinned }; var error = new InfoBar { IsOpen = false, Severity = InfoBarSeverity.Error }; var discard = new CheckBox { Content = "放弃未保存改动", Visibility = Visibility.Collapsed };
        var panel = new StackPanel { Spacing = 12, Width = 460 }; panel.Children.Add(Text("保存到账号：" + sync.Config.Username)); panel.Children.Add(title); if (draft.Kind == "phrase") { panel.Children.Add(body); panel.Children.Add(alias); panel.Children.Add(group); panel.Children.Add(newGroup); panel.Children.Add(pinned); if (draft.OriginPresetId is not null) panel.Children.Add(Text("来自预置，保存后仅修改你的个人版本。")); } panel.Children.Add(error); panel.Children.Add(discard);
        var editor = new ContentDialog { Title = draft.Kind == "group" ? "个人分组" : "个人短语", Content = new ScrollViewer { Content = panel, MaxHeight = 560 }, PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.None, XamlRoot = XamlRoot }; dialog = editor;
        void Capture() { draft.Title = title.Text; draft.Body = body.Text; draft.Alias = alias.Text; draft.GroupId = (group.SelectedItem as ComboBoxItem)?.Tag as string; if (draft.GroupId == "") draft.GroupId = null; draft.Pinned = pinned.IsChecked == true; }
        bool Dirty() { Capture(); return Protocol.Encode(draft) != original || newGroup.Text.Trim().Length > 0; }
        void Retain() { try { if (Dirty()) local?.Retain(expected, draft, newGroup.Text); } catch (Exception e) { error.Message = "保存草稿失败：" + e.Message; error.IsOpen = true; } }
        title.TextChanged += (_, _) => Retain(); body.TextChanged += (_, _) => Retain(); alias.TextChanged += (_, _) => Retain(); newGroup.TextChanged += (_, _) => Retain(); group.SelectionChanged += (_, _) => Retain(); pinned.Checked += (_, _) => Retain(); pinned.Unchecked += (_, _) => Retain();
        async Task Commit(ContentDialogButtonClickEventArgs args)
        {
            var deferral = args.GetDeferral(); args.Cancel = true; editor.IsPrimaryButtonEnabled = editor.IsSecondaryButtonEnabled = false;
            try
            {
                Capture(); draft.Validate(); if (space != expected) throw new InvalidDataException("账号已改变，输入保留在原账号草稿中。");
                if (draft.Kind == "phrase" && draft.Alias is not null && sync.Phrases.Entities.Any(e => e.Kind == "phrase" && e.Id != draft.Id && e.Alias == draft.Alias)) throw new InvalidDataException("已有同名别名，请修改或清空。");
                if (draft.Kind == "group" && Groups.Any(g => g.Id != draft.Id && string.Equals(g.Title, draft.Title, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("已有同名分组。");
                PhraseEntity? newEntity = null; var name = newGroup.Text.Trim(); if (name.Length == 0 && draft.Revision == "0" && draft.OriginPresetId is { } sourceId && draft.GroupId is null) name = catalog.Phrases.FirstOrDefault(p => p.PresetId == sourceId)?.CategoryName ?? "";
                if (draft.Kind == "phrase" && name.Length > 0) { var found = Groups.FirstOrDefault(g => string.Equals(g.Title, name, StringComparison.OrdinalIgnoreCase)); if (found is not null) draft.GroupId = found.Id; else { newEntity = new() { Kind = "group", Title = name }; newEntity.Validate(); draft.GroupId = newEntity.Id; } }
                await Save(draft, expected, newEntity); local?.Clear(expected, draft.Id); finished = true; args.Cancel = false;
            }
            catch (Exception e) { error.Message = e.Message; error.IsOpen = true; Retain(); }
            finally { editor.IsPrimaryButtonEnabled = true; deferral.Complete(); }
        }
        editor.PrimaryButtonClick += async (_, args) => await Commit(args);
        editor.Closing += (_, args) => { if (space != expected) return; if (!finished && Dirty()) { if (discard.IsChecked == true) { discarding = true; local?.Clear(expected, draft.Id); } else { args.Cancel = true; discard.Visibility = Visibility.Visible; error.Message = "仍有未保存改动，请继续编辑或勾选放弃。"; error.IsOpen = true; } } };
        editor.KeyDown += (_, e) => { if (e.Key == VirtualKey.S && NativeMethods.GetAsyncKeyState(0x11) < 0 && editor.IsPrimaryButtonEnabled) { e.Handled = true; InvokePrimary(editor); } };
        try { await editor.ShowAsync(); } finally { if (!finished && !discarding) Retain(); dialog = null; }
    }
    private static Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider? FindPrimary(DependencyObject element)
    {
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(element); i++) { var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(element, i); if (child is Button { Name: "PrimaryButton" } button) { var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(button); return peer?.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke) as Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider; } var found = FindPrimary(child); if (found is not null) return found; } return null;
    }
    private static void InvokePrimary(ContentDialog editor) => FindPrimary(editor)?.Invoke();
    private async Task Resolve(PhraseFailure failure)
    {
        if (DialogOpen || space is null) return; var expected = space;
        var options = new ComboBox { Header = "处理方式", SelectedIndex = 0 }; options.Items.Add(new ComboBoxItem { Content = "保留服务器版本，放弃我的草稿", Tag = "server" });
        if (failure.Server is { DeletedAt: null } server && server.Id == failure.Operation.Entity.Id) options.Items.Add(new ComboBoxItem { Content = "用我的版本替换服务器", Tag = "mine" });
        if (failure.Operation.Entity.Kind == "phrase") options.Items.Add(new ComboBoxItem { Content = "另存为个人短语", Tag = "copy" });
        var content = new StackPanel { Spacing = 12, Width = 460 }; content.Children.Add(Text(failure.Reason)); content.Children.Add(Text("我的版本：\n" + failure.Operation.Entity.Body)); if (failure.Server is { } remote) content.Children.Add(Text("服务器版本：\n" + remote.Body)); content.Children.Add(options);
        dialog = new() { Title = "处理冲突草稿", Content = new ScrollViewer { Content = content, MaxHeight = 460 }, PrimaryButtonText = "确认处理", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot };
        try { if (await dialog.ShowAsync() == ContentDialogResult.Primary) await sync.ResolvePhrase(failure.Id, (options.SelectedItem as ComboBoxItem)!.Tag as string ?? "server", expected); } finally { dialog = null; }
    }
}
