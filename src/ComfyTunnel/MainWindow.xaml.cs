using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace ComfyTunnel.Desktop;

public partial class MainWindow : Window
{
    private readonly Storage storage = new(Storage.DefaultDirectory);
    private readonly SafeLog log = new(Storage.DefaultDirectory);
    private readonly TunnelController controller;
    private readonly Forms.NotifyIcon tray;
    private readonly DispatcherTimer parseTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly Queue<string> history = new();
    private bool ready, exiting, stopping, notified, storageBlocked;
    private string? url, lastDetail;
    private string lastParsed = "";
    private readonly List<SavedConnection> connections = new();
    private string? selectedId;
    private bool refreshing, applying;
    private int uiGeneration;
    private bool editAfterSelection;
    private TunnelState displayState = TunnelState.Idle;
    private int? actualLocalPort;
    private sealed class FormValidationException : Exception { }
    private Dictionary<string, Control> Fields => new()
    { ["Host"] = HostBox, ["SshPort"] = SshPortBox, ["User"] = UserBox, ["KeyPath"] = KeyBox,
      ["TargetHost"] = TargetHostBox, ["TargetPort"] = TargetPortBox, ["LocalPort"] = LocalPortBox };

    public MainWindow()
    {
        InitializeComponent();
        Height = Math.Min(740, SystemParameters.WorkArea.Height - 40);
        MinHeight = Math.Min(MinHeight, Height);
        controller = new(() => new SshTransport(storage, ConfirmIdentity));
        controller.Changed += s => { int generation = uiGeneration; Dispatcher.BeginInvoke(() => { if (generation == uiGeneration) ShowStatus(s); }); };
        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Shield, Text = "ComfyTunnel · 未连接", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示窗口", null, (_, _) => Dispatcher.Invoke(ShowWindow));
        menu.Items.Add("连接 / 断开", null, (_, _) => Dispatcher.Invoke(() => ToggleConnection(this, new RoutedEventArgs())));
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(() => ExitApp()));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindow);
        parseTimer.Tick += (_, _) => { parseTimer.Stop(); TryParse(false); };
        try
        {
            var catalog = storage.LoadConnections(); connections.AddRange(catalog.Connections);
            if (connections.Count == 0) connections.Add(new());
            selectedId = connections.Any(c => c.Id == catalog.SelectedId) ? catalog.SelectedId : connections[0].Id;
            RefreshProfiles(selectedId); ApplySelected();
        }
        catch (Exception)
        {
            storageBlocked = true;
            Loaded += (_, _) => ShowError("无法读取配置或解密凭据。为保护现有文件，已暂停保存和连接。请备份并修复配置后重启。目录：" + storage.DirectoryPath);
        }
        NetworkChange.NetworkAvailabilityChanged += NetworkAvailable;
        SystemEvents.PowerModeChanged += PowerChanged;
        ready = true;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control && EditorPage.IsVisible && SaveButton.IsEnabled)
            { SaveProfile(this, new RoutedEventArgs()); e.Handled = true; }
        };
    }

    private void ApplyProfile(Profile p)
    {
        applying = true;
        HostBox.Text = p.Host; SshPortBox.Text = p.SshPort.ToString(); UserBox.Text = p.User;
        KeyMode.IsChecked = p.UseKey; PasswordMode.IsChecked = !p.UseKey;
        KeyBox.Text = p.KeyPath; SecretBox.Password = p.Secret; RememberBox.IsChecked = p.RememberSecret;
        TargetHostBox.Text = p.TargetHost; TargetPortBox.Text = p.TargetPort.ToString();
        LocalPortBox.Text = p.LocalPort.ToString(); AutoPortBox.IsChecked = p.AutomaticPort;
        EnableComfyBox.IsChecked = p.EnableComfy;
        ComfyModeChanged(this, new RoutedEventArgs());
        AuthChanged(this, new RoutedEventArgs());
        applying = false;
    }

    private Profile ReadProfile(bool validate = true)
    {
        if (storageBlocked) throw new InvalidOperationException("配置读取失败，请先备份并修复配置文件，然后重启程序。");
        foreach (var field in Fields.Values) Field.SetError(field, "");
        if (!TryParse(false)) throw new FormValidationException();
        var errors = ProfileFields.Validate(HostBox.Text.Trim(), SshPortBox.Text, UserBox.Text.Trim(),
            TargetHostBox.Text.Trim(), TargetPortBox.Text, LocalPortBox.Text, KeyMode.IsChecked == true, KeyBox.Text.Trim(), validate, EnableComfyBox.IsChecked == true);
        foreach (var error in errors) Field.SetError(Fields[error.Key], error.Value);
        if (errors.Count > 0) throw new FormValidationException();
        static int Port(TextBox box) => int.Parse(box.Text);
        static int OptionalPort(TextBox box, int fallback) => int.TryParse(box.Text, out int port) && port is > 0 and < 65536 ? port : fallback;
        var p = new Profile
        {
            Host = HostBox.Text.Trim(), SshPort = Port(SshPortBox), User = UserBox.Text.Trim(),
            UseKey = KeyMode.IsChecked == true, KeyPath = KeyBox.Text.Trim(), Secret = SecretBox.Password,
            RememberSecret = RememberBox.IsChecked == true, TargetHost = TargetHostBox.Text.Trim(),
            EnableComfy = EnableComfyBox.IsChecked == true,
            TargetPort = OptionalPort(TargetPortBox, 8188), LocalPort = OptionalPort(LocalPortBox, 18188), AutomaticPort = AutoPortBox.IsChecked == true
        };
        if (validate) p.Validate(); return p;
    }

    private void RefreshProfiles(string? id)
    {
        refreshing = true;
        ProfileList.ItemsSource = null; ProfileList.ItemsSource = connections;
        ProfileList.SelectedItem = connections.FirstOrDefault(c => c.Id == id);
        CountLabel.Text = $"{connections.Count} 个连接 · 加密存储";
        refreshing = false;
    }
    private void ApplySelected()
    {
        var entry = connections.First(c => c.Id == selectedId);
        parseTimer.Stop(); CommandBox.Clear(); lastParsed = "";
        NameBox.Text = entry.Name; ConnectionTitle.Text = entry.Name;
        ParseHint.Text = "粘贴 SSH 命令自动填写；不会执行命令。";
        ParseHint.Foreground = (Brush)FindResource("Muted");
        ImportPanel.IsExpanded = false; ComfyPanel.IsExpanded = false;
        ApplyProfile(entry.Profile);
        foreach (var field in Fields.Values) Field.SetError(field, "");
        Field.SetError(CommandBox, ""); Field.SetError(SecretBox, "");
        displayState = TunnelState.Idle;
        actualLocalPort = null;
        UpdateHomeSummary();
    }
    private void UpdateHomeSummary()
    {
        var entry = connections.FirstOrDefault(c => c.Id == selectedId);
        bool configured = !string.IsNullOrWhiteSpace(entry?.Profile.Host) && !string.IsNullOrWhiteSpace(entry?.Profile.User);
        ServerCard.Tag = configured ? "Configured" : "Empty";
        ServerCard.MinHeight = configured ? 88 : 176;
        System.Windows.Automation.AutomationProperties.SetName(ServerCard, configured ? "选择服务器" : "添加你的第一台服务器");
        EmptyServerPrompt.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
        ServerIdentity.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
        ConnectionActions.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
        ConnectionTitle.Text = configured ? entry!.Name : "添加服务器";
        EditButton.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
        ServerSummary.Text = configured ? $"{entry!.Profile.Host} · SSH {entry.Profile.SshPort}" : "配置一次，以后一键连接";
        ServerChevron.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
        UpdateForwardingSummary();
        if (!controller.IsRunning && !stopping) RenderConnectionState(displayState, configured);
    }
    private void RenderConnectionState(TunnelState state, bool configured)
    {
        var view = ConnectionPresentation.From(state, configured);
        ConnectButton.Content = view.Action; StateLabel.Text = view.Label;
        StateLabel.Foreground = (Brush)FindResource(view.Tone);
        DetailLabel.Foreground = (Brush)FindResource(state == TunnelState.Failed ? "Error" : "Muted");
        ConnectButton.Tag = view.Busy ? (SystemParameters.ClientAreaAnimation ? "Busy" : "Waiting") : state == TunnelState.Connected ? "Connected" : "Idle";
        ConnectButton.Foreground = (Brush)FindResource(state == TunnelState.Connected ? "Muted" : "Ink");
        ConnectButton.BorderBrush = (Brush)FindResource(state == TunnelState.Connected ? "Edge" : "AccentSoft");
    }
    private void ComfyModeChanged(object sender, RoutedEventArgs e)
    {
        if (ComfyFields == null || ModeHint == null) return;
        bool enabled = EnableComfyBox.IsChecked == true;
        ComfyFields.IsEnabled = enabled;
        UpdateForwardingSummary();
        if (!enabled)
            foreach (var field in new[] { TargetHostBox, TargetPortBox, LocalPortBox }) Field.SetError(field, "");
    }
    private void ForwardingFieldChanged(object sender, TextChangedEventArgs e)
    { if (ready && !applying) UpdateForwardingSummary(); }
    private void UpdateForwardingSummary()
    {
        if (ModeHint == null || TargetPortBox == null || LocalPortBox == null || EnableComfyBox == null) return;
        bool enabled = EnableComfyBox.IsChecked == true;
        if (enabled && (!int.TryParse(TargetPortBox.Text, out int target) || target is < 1 or > 65535 ||
                        !int.TryParse(LocalPortBox.Text, out int local) || local is < 1 or > 65535))
        { ModeHint.Text = "请检查端口设置"; return; }
        ModeHint.Text = ForwardingSummary.Format(enabled, TargetHostBox.Text.Trim(),
            int.TryParse(TargetPortBox.Text, out int targetPort) ? targetPort : 8188,
            int.TryParse(LocalPortBox.Text, out int localPort) ? localPort : 18188, actualLocalPort, displayState);
    }
    private void ShowPage(FrameworkElement page)
    {
        bool changed = page.Visibility != Visibility.Visible;
        foreach (var item in new[] { HomePage, ServersPage, EditorPage, LogsPage })
            item.Visibility = item == page ? Visibility.Visible : Visibility.Collapsed;
        if (changed && SystemParameters.ClientAreaAnimation)
            page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)) { FillBehavior = FillBehavior.Stop });
    }
    private void GoHome(object sender, RoutedEventArgs e) => ShowPage(HomePage);
    private void OpenServers(object sender, RoutedEventArgs e)
    {
        if (connections.Count == 1 && (string.IsNullOrWhiteSpace(connections[0].Profile.Host) || string.IsNullOrWhiteSpace(connections[0].Profile.User))) OpenEditor(sender, e);
        else ShowPage(ServersPage);
    }
    private void OpenLogs(object sender, RoutedEventArgs e) => ShowPage(LogsPage);
    private void OpenEditor(object sender, RoutedEventArgs e)
    {
        if (controller.IsRunning || stopping || storageBlocked) return;
        ShowPage(EditorPage); NameBox.Focus();
    }
    private void CloseEditor(object sender, RoutedEventArgs e)
    {
        try { CaptureDraft(false); RefreshProfiles(selectedId); UpdateHomeSummary(); ShowPage(HomePage); }
        catch (Exception ex) { ShowInputError(ex); }
    }
    private void UseSelectedServer(object sender, System.Windows.Input.MouseButtonEventArgs e)
    { if (!stopping) ShowPage(HomePage); }
    private void CaptureDraft(bool validate)
    {
        if (storageBlocked) throw new InvalidOperationException("无法解密现有连接资料，已阻止覆盖。请修复后重启。");
        if (selectedId == null) return;
        Profile p = ReadProfile(validate);
        int index = connections.FindIndex(c => c.Id == selectedId);
        connections[index] = connections[index] with { Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "未命名连接" : NameBox.Text.Trim(), Profile = p };
    }
    private void PersistCatalog()
    {
        if (storageBlocked) throw new InvalidOperationException("无法解密现有连接资料，已阻止覆盖。");
        storage.SaveConnections(new() { SelectedId = selectedId, Connections = connections.ToList() });
    }
    private async void SelectConnection(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || refreshing || stopping || ProfileList.SelectedItem is not SavedConnection target || target.Id == selectedId) return;
        try
        {
            CaptureDraft(false);
            stopping = true; ProfileNavigation.IsEnabled = false; ConnectButton.IsEnabled = false;
            await controller.StopAsync(); uiGeneration++;
            selectedId = target.Id; RefreshProfiles(selectedId); ApplySelected();
            ShowStatus(new(TunnelState.Idle, "已切换服务器，点击连接建立隧道。"));
            ShowPage(editAfterSelection ? EditorPage : HomePage); editAfterSelection = false;
        }
        catch (Exception ex) { RefreshProfiles(selectedId); ShowInputError(ex); }
        finally { stopping = false; ProfileNavigation.IsEnabled = true; ConnectButton.IsEnabled = true; ConnectButton.Content = "连接"; Editor.IsEnabled = EditButton.IsEnabled = SaveButton.IsEnabled = EnableComfyBox.IsEnabled = !controller.IsRunning; UpdateHomeSummary(); }
    }
    private void NewConnection(object sender, RoutedEventArgs e)
    {
        if (stopping || storageBlocked) return;
        try
        {
            CaptureDraft(false);
            var entry = new SavedConnection { Name = "新的连接 " + (connections.Count + 1) }; connections.Add(entry);
            RefreshProfiles(selectedId); editAfterSelection = true; ProfileList.SelectedItem = entry;
        }
        catch (Exception ex) { ShowInputError(ex); }
    }
    private async void DeleteConnection(object sender, RoutedEventArgs e)
    {
        if (stopping || storageBlocked || selectedId == null) return;
        if (MessageBox.Show(this, "移除当前保存的连接？活动隧道将断开。服务器上的数据不会改变。", "移除连接", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        stopping = true; ProfileNavigation.IsEnabled = false; ConnectButton.IsEnabled = false;
        try
        {
            await controller.StopAsync(); uiGeneration++;
            var remaining = connections.Where(c => c.Id != selectedId).ToList();
            storage.SaveConnections(new() { SelectedId = remaining.FirstOrDefault()?.Id, Connections = remaining });
            connections.Clear(); connections.AddRange(remaining); if (connections.Count == 0) connections.Add(new());
            selectedId = connections[0].Id; RefreshProfiles(selectedId); ApplySelected(); ShowStatus(new(TunnelState.Idle, "连接已移除。"));
            ShowPage(HomePage);
        }
        catch (Exception ex) { ShowInputError(ex); }
        finally { stopping = false; ProfileNavigation.IsEnabled = true; ConnectButton.IsEnabled = true; ConnectButton.Content = "连接"; Editor.IsEnabled = EditButton.IsEnabled = SaveButton.IsEnabled = EnableComfyBox.IsEnabled = !controller.IsRunning; UpdateHomeSummary(); }
    }
    private void MinimizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void HideWindow(object sender, RoutedEventArgs e) => Close();

    private void CommandChanged(object sender, TextChangedEventArgs e)
    { if (ready) { parseTimer.Stop(); parseTimer.Start(); } }
    private void ParseCommand(object sender, RoutedEventArgs e) => TryParse(true);
    private bool TryParse(bool force)
    {
        string text = CommandBox.Text.Trim();
        if (text.Length == 0 || (!force && text == lastParsed)) return true;
        try
        {
            var parsed = SshCommandParser.Parse(text);
            HostBox.Text = parsed.Host; SshPortBox.Text = parsed.Port.ToString(); UserBox.Text = parsed.User;
            if (parsed.KeyPath != null) { KeyMode.IsChecked = true; KeyBox.Text = parsed.KeyPath; }
            lastParsed = text;
            Field.SetError(CommandBox, ""); ParseHint.Text = "已导入连接参数，可在上方修改。"; ParseHint.Foreground = (Brush)FindResource("Muted");
            return true;
        }
        catch (FormatException ex) { Field.SetError(CommandBox, ex.Message); ParseHint.Text = "请修正命令，或清空后手动填写。"; return false; }
    }

    private void AuthChanged(object sender, RoutedEventArgs e)
    {
        if (KeyRow == null) return;
        bool key = KeyMode.IsChecked == true;
        KeyRow.Visibility = key ? Visibility.Visible : Visibility.Collapsed;
        SecretLabel.Text = key ? "私钥口令（未加密私钥可留空）" : "SSH 密码";
        Field.SetPlaceholder(SecretBox, key ? "未加密的私钥可留空" : "输入 SSH 密码");
        if (ready && !applying) SecretBox.Clear();
    }
    private void BrowseKey(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择 SSH 私钥", Filter = "所有文件|*.*" };
        if (dialog.ShowDialog(this) == true) KeyBox.Text = dialog.FileName;
    }
    private void SaveProfile(object sender, RoutedEventArgs e)
    {
        try { CaptureDraft(true); PersistCatalog(); RefreshProfiles(selectedId); displayState = TunnelState.Idle; UpdateHomeSummary(); ShowPage(HomePage); DetailLabel.Text = ""; }
        catch (Exception ex) { ShowInputError(ex); }
    }
    private async void ToggleConnection(object sender, RoutedEventArgs e)
    {
        if (stopping || exiting) return;
        if (controller.IsRunning)
        {
            stopping = true; ConnectButton.IsEnabled = false; ConnectButton.Content = "正在断开…";
            try { await controller.StopAsync(); }
            finally { stopping = false; ConnectButton.IsEnabled = true; ConnectButton.Content = "连接"; Editor.IsEnabled = EditButton.IsEnabled = SaveButton.IsEnabled = EnableComfyBox.IsEnabled = true; UpdateHomeSummary(); }
            return;
        }
        try
        {
            if (string.IsNullOrWhiteSpace(HostBox.Text) || string.IsNullOrWhiteSpace(UserBox.Text))
            {
                OpenEditor(this, new RoutedEventArgs());
                if (!string.IsNullOrWhiteSpace(HostBox.Text) && !string.IsNullOrWhiteSpace(UserBox.Text)) SecretBox.Focus();
                return;
            }
            if (PasswordMode.IsChecked == true && string.IsNullOrEmpty(SecretBox.Password))
            { Field.SetError(SecretBox, "本次连接需要输入 SSH 密码。"); throw new FormValidationException(); }
            CaptureDraft(true); PersistCatalog(); RefreshProfiles(selectedId);
            Profile p = connections.First(c => c.Id == selectedId).Profile;
            ConnectionTitle.Text = connections.First(c => c.Id == selectedId).Name;
            UpdateHomeSummary();
            Editor.IsEnabled = false; ConnectButton.Content = "取消连接";
            controller.Start(p);
        }
        catch (Exception ex) { Editor.IsEnabled = true; ConnectButton.Content = "连接"; ShowPage(EditorPage); ShowInputError(ex); }
    }

    private void ShowStatus(TunnelStatus status)
    {
        if (exiting) return;
        displayState = status.State;
        if (status.State == TunnelState.Connected) actualLocalPort = status.LocalPort;
        else if (status.State is TunnelState.Idle or TunnelState.Failed) actualLocalPort = null;
        UpdateForwardingSummary();
        RenderConnectionState(status.State, !string.IsNullOrWhiteSpace(HostBox.Text));
        DetailLabel.Text = status.Detail;
        url = status.State == TunnelState.Connected ? status.Url : null;
        AddressPanel.Visibility = url != null ? Visibility.Visible : Visibility.Collapsed;
        UrlBox.Text = url ?? "尚未建立本地入口";
        CopyButton.IsEnabled = OpenButton.IsEnabled = url != null;
        bool active = status.State is TunnelState.Connected or TunnelState.Connecting or TunnelState.Retrying;
        Editor.IsEnabled = !active && !stopping;
        EditButton.IsEnabled = SaveButton.IsEnabled = EnableComfyBox.IsEnabled = !active && !stopping;
        if (stopping) ConnectButton.Content = "正在断开…";
        tray.Text = "ComfyTunnel · " + StateLabel.Text;
        if (lastDetail != status.Detail)
        {
            lastDetail = status.Detail; log.Write(status.Detail);
            history.Enqueue($"{DateTime.Now:HH:mm:ss}  {status.Detail}");
            while (history.Count > 150) history.Dequeue();
            LogBox.Text = string.Join(Environment.NewLine, history); LogBox.ScrollToEnd();
        }
    }

    private Task<bool> ConfirmIdentity(HostIdentity identity, CancellationToken token)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.BeginInvoke(() =>
        {
            if (token.IsCancellationRequested || exiting) { answer.TrySetCanceled(token); return; }
            ShowWindow();
            var dialog = new Window { Title = "确认 SSH 服务器身份", Owner = this, Width = 520, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, FontFamily = FontFamily };
            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(new TextBlock { Text = "首次连接，请与服务器提供方核对指纹", FontSize = 17, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = $"{identity.Host}:{identity.Port}\n{identity.Algorithm}", Margin = new Thickness(0,16,0,8), TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBox { Text = identity.Fingerprint, IsReadOnly = true, TextWrapping = TextWrapping.Wrap });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,18,0,0) };
            var cancel = new Button { Content = "取消", Margin = new Thickness(0,0,10,0) };
            var accept = new Button { Content = "核对一致，信任并连接" };
            cancel.Click += (_, _) => dialog.Close();
            accept.Click += (_, _) => { answer.TrySetResult(true); dialog.Close(); };
            buttons.Children.Add(cancel); buttons.Children.Add(accept); panel.Children.Add(buttons); dialog.Content = panel;
            using var registration = token.Register(() => { answer.TrySetCanceled(token); Dispatcher.BeginInvoke(() => dialog.Close()); });
            dialog.ShowDialog(); answer.TrySetResult(false);
        });
        return answer.Task;
    }

    private void CopyAddress(object sender, RoutedEventArgs e)
    {
        if (url == null) return;
        try { Clipboard.SetText(url); } catch { ShowError("剪贴板正被占用，请稍后再试。"); }
    }
    private void OpenAddress(object sender, RoutedEventArgs e)
    {
        if (url == null) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { ShowError("无法启动默认浏览器，请复制地址手动打开。"); }
    }
    private void NetworkAvailable(object? sender, NetworkAvailabilityEventArgs e) { if (e.IsAvailable) controller.Wake(); }
    private void PowerChanged(object sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) controller.Wake(); }
    private void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void ShowLicenses(object sender, RoutedEventArgs e)
    {
        var assembly = Assembly.GetExecutingAssembly(); var text = new StringBuilder();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) continue;
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            text.AppendLine(name).AppendLine(reader.ReadToEnd()).AppendLine();
        }
        new Window { Owner = this, Title = "ComfyTunnel 0.6.0 · 第三方许可", Width = 700, Height = 500, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBox { Text = text.ToString(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(15) } }.ShowDialog();
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (exiting) return;
        e.Cancel = true; Hide();
        if (!notified) { notified = true; tray.ShowBalloonTip(4000, "ComfyTunnel 仍在运行", "双击托盘图标打开窗口，右键选择“退出”结束程序。", Forms.ToolTipIcon.Info); }
    }
    private async void ExitApp()
    {
        if (exiting) return;
        exiting = true; parseTimer.Stop(); Hide();
        NetworkChange.NetworkAvailabilityChanged -= NetworkAvailable; SystemEvents.PowerModeChanged -= PowerChanged;
        await controller.StopAsync(); tray.Visible = false; tray.Dispose(); Application.Current.Shutdown();
    }
    private void ShowError(string text) => MessageBox.Show(this, text, "ComfyTunnel", MessageBoxButton.OK, MessageBoxImage.Warning);
    private void ShowInputError(Exception ex)
    {
        if (ex is FormValidationException)
        {
            ShowPage(EditorPage);
            var invalid = new Control[] { CommandBox, HostBox, SshPortBox, UserBox, KeyBox, SecretBox, TargetHostBox, TargetPortBox, LocalPortBox }.FirstOrDefault(Field.GetHasError);
            if (invalid == CommandBox) ImportPanel.IsExpanded = true;
            if (invalid == TargetHostBox || invalid == TargetPortBox || invalid == LocalPortBox) ComfyPanel.IsExpanded = true;
            Dispatcher.BeginInvoke(() => { invalid?.Focus(); invalid?.BringIntoView(); });
            return;
        }
        ShowError(ex is FormatException or ArgumentException or InvalidOperationException ? ex.Message : "无法保存配置，请检查本地应用数据目录的写入权限。");
    }
}
