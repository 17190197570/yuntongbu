using CloudDrive.Storage;
using System.Diagnostics;

namespace CloudDrive.Desktop;

public sealed partial class MainForm
{
    private readonly NotifyIcon tray = new() { Text = "CloudDrive Lite", Icon = SystemIcons.Application };
    private readonly Label productOverview = new() { Dock = DockStyle.Fill, Font = BodyFont, Text = "正在读取后台状态…" };
    private readonly ListBox productActivity = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true };
    private EventWaitHandle? showEvent;
    private bool polling;
    private readonly NumericUpDown startupDelay = new() { Minimum = 0, Maximum = 600, Value = 15, Width = 110 };
    private readonly TextBox productMessage = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private void BuildProductHome(TabPage page)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = Padding.Empty };
        foreach (int height in new[] { 124, 60, 170, 64 }) layout.RowStyles.Add(new(SizeType.Absolute, height));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        var metrics = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
        string[] captions = ["NAS 连接", "文件夹映射", "已索引文件", "本地缓存占用"];
        for (int i = 0; i < 4; i++)
        {
            metrics.ColumnStyles.Add(new(SizeType.Percent, 25));
            var card = new ConnectionCard { Dock = DockStyle.Fill, Margin = new(i == 0 ? 0 : 6, 0, i == 3 ? 0 : 6, 12), Padding = new(20, 14, 16, 12) };
            card.Controls.Add(homeValues[i]); card.Controls.Add(new Label { Text = captions[i], Dock = DockStyle.Top, Height = 26, ForeColor = Muted }); metrics.Controls.Add(card, i, 0);
        }
        var shortcuts = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new(0, 4, 0, 0) };
        shortcuts.Controls.Add(MainAction("添加 NAS 连接", (_, _) => NavigateWorkspace(1)));
        shortcuts.Controls.Add(Button("管理文件夹映射", (_, _) => NavigateWorkspace(2)));
        shortcuts.Controls.Add(Button("查看运行健康", (_, _) => NavigateWorkspace(8)));
        shortcuts.Controls.Add(Button("安全退出程序", async (_, _) => await ExitProduct()));
        layout.Controls.Add(metrics, 0, 0); layout.Controls.Add(shortcuts, 0, 1);
        layout.Controls.Add(FormCard("运行概况", productOverview), 0, 2);
        var limitation = new Label { Dock = DockStyle.Fill, Padding = new(14, 8, 10, 4), BackColor = Color.FromArgb(255, 251, 230), ForeColor = Color.FromArgb(130, 85, 10), Text = "开发版 · 已有文件以只读模式使用；完整双向同步与真实 NAS 文件操作尚未验收。\n关闭窗口会隐藏到托盘。结束运行请使用“安全退出程序”。" };
        layout.Controls.Add(limitation, 0, 3); layout.Controls.Add(FormCard("最近活动", productActivity), 0, 4); page.Controls.Add(layout);
        UpdateHomeProfiles();
    }
    private void BuildProductSettings(TabPage page)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1 };
        layout.RowStyles.Add(new(SizeType.Absolute, 132)); layout.RowStyles.Add(new(SizeType.Absolute, 132)); layout.RowStyles.Add(new(SizeType.Absolute, 176)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.Controls.Add(FormCard("客户端与数据", new Label { Text = "CloudDrive Lite " + ProductEnvironment.Version + " · 当前用户部署\n用户数据：" + ProductEnvironment.DataRoot, Dock = DockStyle.Fill, AutoEllipsis = true }), 0, 0);
        var startup = new FlowLayoutPanel { Dock = DockStyle.Fill };
        startup.Controls.Add(login);
        startup.Controls.Add(new Label { Text = "登录延迟（秒）", AutoSize = true }); startup.Controls.Add(startupDelay);
        if (!smoke) startupDelay.Value = ProductSettings.Load().StartupDelaySeconds;
        startup.Controls.Add(Button("保存登录延迟", (_, _) => { if (!smoke) ProductSettings.Change(s => s with { StartupDelaySeconds = (int)startupDelay.Value }); }));
        layout.Controls.Add(FormCard("启动与登录", startup), 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true };
        actions.Controls.Add(Button("打开日志目录", (_, _) => OpenLogs()));
        actions.Controls.Add(Button("检查更新", async (_, _) => await CheckProductUpdate()));
        AddOperationsButtons(actions);
        actions.Controls.Add(Button("安全退出程序", async (_, _) => await ExitProduct()));
        layout.Controls.Add(FormCard("维护工具", actions), 0, 2); layout.Controls.Add(FormCard("操作结果", productMessage), 0, 3); page.Controls.Add(layout);
    }
    private void InitializeTray(TabControl tabs)
    {
        var menu = new ContextMenuStrip();
        void Navigate(string page) { Show(); WindowState = FormWindowState.Normal; Activate(); foreach (TabPage tab in tabs.TabPages) if (tab.Text.StartsWith(page, StringComparison.Ordinal)) tabs.SelectedTab = tab; }
        menu.Items.Add("打开 CloudDrive Lite", null, (_, _) => Navigate("首页"));
        menu.Items.Add("查看同步状态", null, (_, _) => Navigate("同步中心"));
        menu.Items.Add("查看缓存状态", null, (_, _) => Navigate("缓存管理"));
        menu.Items.Add("查看归档任务", null, (_, _) => Navigate("归档管理"));
        menu.Items.Add("运行健康中心", null, (_, _) => Navigate("运行健康中心"));
        menu.Items.Add(new ToolStripSeparator());
        foreach (var item in new[] { ("暂停同步认领", "sync-pause"), ("恢复同步认领", "sync-resume"), ("暂停自动归档", "archive-pause"), ("恢复自动归档", "archive-resume"), ("重新连接所有 NAS", "reconnect-all") })
        { string command = item.Item2; menu.Items.Add(item.Item1, null, async (_, _) => await ProductCommand(new(command))); }
        menu.Items.Add("打开日志目录", null, (_, _) => OpenLogs());
        menu.Items.Add("检查更新", null, async (_, _) => await CheckProductUpdate());
        menu.Items.Add("退出程序", null, async (_, _) => await ExitProduct());
        tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Navigate("首页"); tray.Visible = !smoke;
        if (!smoke)
        {
            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CloudDriveLite.Show." + ProductEnvironment.UserKey);
            refresh.Tick += (_, _) => { if (showEvent.WaitOne(0)) Navigate("首页"); };
        }
    }
    private async Task ProductCommand(ManagementRequest request)
    {
        try
        {
            await ManagementClient.EnsureStartedAsync(lifetime.Token);
            var reply = await ManagementClient.SendAsync(request, lifetime.Token); productMessage.Text = diagnostic.Text = reply.Message;
            if (!reply.Success) throw new IOException(reply.Message);
        }
        catch (Exception e) { productMessage.Text = diagnostic.Text = e.Message; }
    }
    private async Task PollProductState()
    {
        if (polling || closing) return; polling = true;
        try
        {
            var reply = await ManagementClient.SendAsync(new("status"), lifetime.Token, timeoutSeconds: 2); var s = reply.Snapshot!;
            int ready = s.Providers.Count(p => p.Ready && p.HeartbeatAt is { } at && DateTimeOffset.UtcNow - at < TimeSpan.FromSeconds(45)); int online = s.Providers.Count(p => p.Ready && p.SmbOnline && p.HeartbeatAt is { } at && DateTimeOffset.UtcNow - at < TimeSpan.FromSeconds(45)); int total = connections.Items.Count;
            productOverview.Text = $"后台状态：{s.State}    ·    CFAPI 就绪：{ready} / {total}\n待同步：{s.Waiting}    ·    失败/冲突：{s.Failed}    ·    上传中：{s.ActiveUploads}    ·    读取中：{s.ActiveDownloads}\n归档执行：{s.ArchiveActive}    ·    数据库：{(s.DatabaseHealthy ? "正常" : "异常")}\n最近更新：{s.UpdatedAt.ToLocalTime():HH:mm:ss}";
            UpdateHomeMetrics(s.IndexedFiles, s.CacheBytes); runtimeBadge.Text = s.DatabaseHealthy ? "●  后台在线" : "●  数据库异常"; runtimeBadge.ForeColor = s.DatabaseHealthy ? Color.FromArgb(56, 122, 48) : Color.FromArgb(207, 19, 34);
            string state = !s.DatabaseHealthy ? "后台异常" : s.Failed > 0 ? "任务失败" : s.ArchiveActive > 0 ? "正在归档" : s.ActiveUploads + s.ActiveDownloads > 0 ? "正在传输" : total > online ? "离线/未连接" : "运行正常";
            tray.Icon = !s.DatabaseHealthy || s.Failed > 0 ? SystemIcons.Error : total > online ? SystemIcons.Warning : s.ArchiveActive > 0 ? SystemIcons.Shield : s.ActiveUploads + s.ActiveDownloads > 0 ? SystemIcons.Information : SystemIcons.Application;
            string tip = $"CloudDrive Lite · {state}\n连接{online}/{total} 待同步{s.Waiting} 读取{s.ActiveDownloads}"; tray.Text = tip.Length > 63 ? tip[..63] : tip;
            productActivity.BeginUpdate(); productActivity.Items.Clear();
            foreach (var p in s.Providers) productActivity.Items.Add($"连接 {p.ConnectionId} / Host {p.HostPid} / {p.State} / {p.ObservedAt.ToLocalTime():g}");
            foreach (var line in index.Logs().Take(30)) productActivity.Items.Add(line);
            productActivity.EndUpdate();
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException)
        { if (!closing) { productOverview.Text = "后台不可达，请检查日志或重新启动。\n既有本地文件、映射和缓存保留。"; runtimeBadge.Text = "●  后台未连接"; runtimeBadge.ForeColor = Muted; homeValues[2].Text = homeValues[3].Text = "—"; tray.Icon = SystemIcons.Warning; tray.Text = "CloudDrive Lite · 后台不可达"; } }
        finally { polling = false; }
    }
    private void OpenLogs()
    { Directory.CreateDirectory(ProductEnvironment.LogRoot); using var process = Process.Start(new ProcessStartInfo(ProductEnvironment.LogRoot) { UseShellExecute = true }); }
    private async Task ExitProduct()
    {
        if (operation is not null) { Show(); productMessage.Text = "界面仍有文件操作，请等待或取消当前批次，再安全退出。"; return; }
        try
        {
            var reply = await ManagementClient.SendAsync(new("shutdown"), lifetime.Token); if (!reply.Success) { productMessage.Text = reply.Message; Show(); return; }
            productMessage.Text = reply.Message;
            for (int attempt = 0; attempt < 60; attempt++)
            {
                await Task.Delay(500, lifetime.Token);
                try { await ManagementClient.SendAsync(new("status"), lifetime.Token, timeoutSeconds: 1); }
                catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException) { lifetime.Token.ThrowIfCancellationRequested(); closing = true; tray.Visible = false; tray.Dispose(); lifetime.Cancel(); refresh.Stop(); Close(); return; }
            }
            Show(); productMessage.Text = "后台仍在安全收尾，未强制中止。可以继续使用本窗口等待完成。";
        }
        catch (Exception e)
        {
            try { using var offline = ProductMaintenance.AcquireOffline(index); closing = true; tray.Visible = false; tray.Dispose(); refresh.Stop(); Close(); }
            catch (IOException) { Show(); productMessage.Text = e.Message + "；后台仍持有资源，请等待安全收尾。"; }
        }
    }
}
