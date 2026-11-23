using CloudDrive.Storage;
using Microsoft.Win32;
using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Runtime.InteropServices;

namespace CloudDrive.Desktop;

public sealed partial class MainForm : Form
{
    private readonly IndexStore index;
    private readonly CredentialVault vault = new();
    private readonly AgentRuntime runtime;
    private readonly ListBox connections = new() { Dock = DockStyle.Fill, DisplayMember = "Name" };
    private readonly Dictionary<string, TextBox> inputs = [];
    private readonly NumericUpDown port = new() { Minimum = 1, Maximum = 65535, Value = 445, Width = 130 };
    private readonly CheckBox auto = new() { Text = "启动时自动连接", AutoSize = true };
    private readonly CheckBox login = new() { Text = "Windows 登录时启动 Agent（当前用户）", AutoSize = true };
    private readonly TextBox diagnostic = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly DataGridView status = new() { Dock = DockStyle.Fill, ReadOnly = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, AllowUserToAddRows = false, RowHeadersVisible = false };
    private readonly ListBox logs = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true };
    private readonly DataGridView syncTasks = new() { Dock = DockStyle.Fill, ReadOnly = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, AllowUserToAddRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };
    private readonly CheckBox newUploads = new() { Text = "启用新增文件上传试验（仅测试 NAS，尚未验收）", AutoSize = true };
    private readonly NumericUpDown uploadConcurrency = new() { Minimum = 1, Maximum = 8, Value = 3, Width = 55 };
    private readonly NumericUpDown debounce = new() { Minimum = 200, Maximum = 60000, Value = 2000, Increment = 100, Width = 85 };
    private readonly Label syncScope = new() { AutoSize = true, Text = "当前映射：请在文件夹映射中选择映射" };
    private readonly CancellationTokenSource lifetime = new();
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 3000 };
    private CancellationTokenSource? operation;
    private Guid editing = Guid.NewGuid();
    private bool closing;
    private readonly bool smoke;
    private bool hideToTray;

    public MainForm(bool smoke, bool showConnections = false)
    {
        this.smoke = smoke;
        hideToTray = !smoke;
        index = new IndexStore(smoke ? Path.Combine(Path.GetTempPath(), "CloudDriveLite-ui-smoke-" + Guid.NewGuid().ToString("N"), "index.db") : ProductEnvironment.DatabasePath);
        runtime = new(index, vault);
        Text = "CloudDrive Lite · " + ProductEnvironment.Version + " 开发版"; Width = 1320; Height = 940; MinimumSize = new(1180, 850);
        Font = BodyFont; StartPosition = FormStartPosition.CenterScreen;
        var tabs = new PageDeck();
        var configTab = new TabPage("SMB 连接管理") { Padding = new(16) };
        var mappingTab = new TabPage("文件夹映射") { Padding = new(16) };
        var statusTab = new TabPage("存储状态") { Padding = new(16) }; statusTab.Controls.Add(status);
        var logsTab = new TabPage("任务日志") { Padding = new(16) }; logsTab.Controls.Add(logs);
        var syncTab = new TabPage("同步中心 · 新增试验") { Padding = new(16) };
        BuildSyncCenter(syncTab);
        var cacheTab = new TabPage("缓存管理") { Padding = new(10) }; BuildCacheCenter(cacheTab);
        var archiveTab = new TabPage("归档管理") { Padding = new(10) }; BuildArchiveCenter(archiveTab);
        var homeTab = new TabPage("首页总览") { Padding = new(20) }; BuildProductHome(homeTab);
        var settingsTab = new TabPage("设置与运维") { Padding = new(20) }; BuildProductSettings(settingsTab);
        var healthTab = new TabPage("运行健康中心") { Padding = new(12) }; BuildHealthCenter(healthTab);
        tabs.TabPages.AddRange([homeTab, configTab, mappingTab, statusTab, syncTab, cacheTab, archiveTab, logsTab, healthTab, settingsTab]); BuildAppShell(tabs);
        InitializeTray(tabs);
        BuildStorageConnections(configTab); BuildFolderMappings(mappingTab);
        if (showConnections) tabs.SelectedTab = configTab;
        foreach (string name in new[] { "文件夹映射", "状态与占位数", "远端索引项数", "最近扫描" }) status.Columns.Add(name, name);
        connections.SelectedIndexChanged += (_, _) => { if (connections.SelectedItem is SmbConnection c) LoadConfig(c); };
        login.Checked = LoginStartup.Enabled;
        login.CheckedChanged += (_, _) => ConfigureLogin();
        refresh.Tick += async (_, _) => { RefreshStatus(); if (!smoke) { await PollProductState(); await PollHealth(); } }; refresh.Start();
        Shown += async (_, _) =>
        {
            Reload();
            if (smoke)
            {
                await CheckConnectionPagesAsync();
                using var bitmap = new Bitmap(Width, Height); DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
                string path = Path.Combine(AppContext.BaseDirectory, "desktop-smoke.png"); bitmap.Save(path);
                tabs.SelectedTab = configTab; await Task.Delay(100);
                using var nasBitmap = new Bitmap(Width, Height); DrawToBitmap(nasBitmap, new Rectangle(0, 0, Width, Height)); nasBitmap.Save(Path.Combine(AppContext.BaseDirectory, "nas-connections-smoke.png"));
                tabs.SelectedTab = mappingTab; await Task.Delay(100);
                using var mappingBitmap = new Bitmap(Width, Height); DrawToBitmap(mappingBitmap, new Rectangle(0, 0, Width, Height)); mappingBitmap.Save(Path.Combine(AppContext.BaseDirectory, "folder-mappings-smoke.png"));
                var normalSize = Size; Size = MinimumSize; await Task.Delay(100);
                using var smallMapping = new Bitmap(Width, Height); DrawToBitmap(smallMapping, new Rectangle(0, 0, Width, Height)); smallMapping.Save(Path.Combine(AppContext.BaseDirectory, "folder-mappings-minimum-smoke.png"));
                tabs.SelectedTab = configTab; await Task.Delay(100);
                using var smallNas = new Bitmap(Width, Height); DrawToBitmap(smallNas, new Rectangle(0, 0, Width, Height)); smallNas.Save(Path.Combine(AppContext.BaseDirectory, "nas-connections-minimum-smoke.png"));
                Size = normalSize;
                tabs.SelectedTab = syncTab; await Task.Delay(100);
                using var syncBitmap = new Bitmap(Width, Height); DrawToBitmap(syncBitmap, new Rectangle(0, 0, Width, Height)); syncBitmap.Save(Path.Combine(AppContext.BaseDirectory, "sync-center-smoke.png"));
                tabs.SelectedTab = cacheTab; await Task.Delay(100);
                using var cacheBitmap = new Bitmap(Width, Height); DrawToBitmap(cacheBitmap, new Rectangle(0, 0, Width, Height)); cacheBitmap.Save(Path.Combine(AppContext.BaseDirectory, "cache-center-smoke.png"));
                cachePages.SelectedIndex = 1; await Task.Delay(100);
                using var policyBitmap = new Bitmap(Width, Height); DrawToBitmap(policyBitmap, new Rectangle(0, 0, Width, Height)); policyBitmap.Save(Path.Combine(AppContext.BaseDirectory, "cache-policy-smoke.png"));
                tabs.SelectedTab = archiveTab; await Task.Delay(100);
                using var archiveBitmap = new Bitmap(Width, Height); DrawToBitmap(archiveBitmap, new Rectangle(0, 0, Width, Height)); archiveBitmap.Save(Path.Combine(AppContext.BaseDirectory, "archive-center-smoke.png"));
                using var archiveEditor = CreateArchiveEditor(null); archiveEditor.Show(this); await Task.Delay(100);
                using var editorBitmap = new Bitmap(archiveEditor.Width, archiveEditor.Height); archiveEditor.DrawToBitmap(editorBitmap, new Rectangle(0, 0, archiveEditor.Width, archiveEditor.Height)); editorBitmap.Save(Path.Combine(AppContext.BaseDirectory, "archive-policy-smoke.png")); archiveEditor.Close();
                tabs.SelectedTab = homeTab; await Task.Delay(100);
                using var homeBitmap = new Bitmap(Width, Height); DrawToBitmap(homeBitmap, new Rectangle(0, 0, Width, Height)); homeBitmap.Save(Path.Combine(AppContext.BaseDirectory, "home-smoke.png"));
                tabs.SelectedTab = settingsTab; await Task.Delay(100);
                using var settingsBitmap = new Bitmap(Width, Height); DrawToBitmap(settingsBitmap, new Rectangle(0, 0, Width, Height)); settingsBitmap.Save(Path.Combine(AppContext.BaseDirectory, "settings-smoke.png"));
                tabs.SelectedTab = healthTab; displayedHealth = index.ReadHealth(); RenderHealth(); await Task.Delay(100);
                using var healthBitmap = new Bitmap(Width, Height); DrawToBitmap(healthBitmap, new Rectangle(0, 0, Width, Height)); healthBitmap.Save(Path.Combine(AppContext.BaseDirectory, "health-center-smoke.png"));
                await CaptureDesignSmokeAsync(tabs);
                hideToTray = true; Close();
                if (Visible || IsDisposed) throw new IOException("关闭窗口未正确隐藏；不能通过后台生命周期检查。");
                Show(); hideToTray = false;
                await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "desktop-smoke.txt"), "PASS ten tabs; actual NAS form saves without a folder/root, password masked and cleared; two offline mappings reuse NAS; mapping delete preserves credential; tray menu initialized; actual Close hides and Show reopens the live window; rendered " + path); Close();
            }
            else try { await ManagementClient.EnsureStartedAsync(lifetime.Token); await PollProductState(); } catch (Exception e) { diagnostic.Text = FailureClassifier.Describe("bootstrap", e).Message; }
        };
        ApplyProductTheme(this);
        FormClosing += OnClosing;
    }
    private static Button Button(string text, EventHandler handler)
    {
        var button = new ModernButton { Text = text };
        if (text is "新建归档配置" or "立即完整检查" or "保存配置" or "保存" or "保存当前连接策略") button.Tag = "primary";
        else if (text.StartsWith("删除", StringComparison.Ordinal) || text == "安全退出程序") button.Tag = "danger";
        button.Click += handler; return button;
    }
    private void BuildSyncCenter(TabPage page)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1 };
        layout.RowStyles.Add(new(SizeType.Absolute, 70)); layout.RowStyles.Add(new(SizeType.Absolute, 65)); layout.RowStyles.Add(new(SizeType.Absolute, 55)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        var information = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown };
        information.Controls.Add(syncScope);
        information.Controls.Add(new Label { Text = "仅上传新增文件至已有远端目录，成功后转只读。修改、目录、重命名、删除及冲突副本尚未实现。", AutoSize = true });
        layout.Controls.Add(information, 0, 0);
        var settings = new FlowLayoutPanel { Dock = DockStyle.Fill };
        settings.Controls.AddRange([newUploads, new Label { Text = "并发", AutoSize = true }, uploadConcurrency, new Label { Text = "防抖ms", AutoSize = true }, debounce]);
        settings.Controls.Add(Button("保存设置", async (_, _) => await PerformAsync(_ =>
        {
            var config = Saved(); index.SetSyncSettings(config.Id, index.SyncSettings(config.Id) with { Enabled = newUploads.Checked, UploadConcurrency = (int)uploadConcurrency.Value, DebounceMilliseconds = (int)debounce.Value });
            diagnostic.Text = "新增上传试验设置已保存；Agent 运行后生效。此功能仍需真实 NAS 验收。"; return Task.CompletedTask;
        })));
        layout.Controls.Add(settings, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill };
        foreach (var control in new[] { ("重试/恢复", SyncTaskStatus.Pending), ("暂停任务", SyncTaskStatus.Paused), ("取消待执行", SyncTaskStatus.Cancelled) })
        {
            var item = control;
            actions.Controls.Add(Button(item.Item1, async (_, _) => await PerformAsync(_ =>
            {
                var task = syncTasks.CurrentRow?.Tag as SyncTask ?? throw new ArgumentException("请先选择任务。");
                index.ControlSyncTask(task.ConnectionId, task.Id, item.Item2); return Task.CompletedTask;
            })));
        }
        actions.Controls.Add(Button("暂停/恢复认领", async (_, _) => await PerformAsync(_ =>
        {
            var config = Saved(); var value = index.SyncSettings(config.Id); index.SetSyncSettings(config.Id, value with { Enabled = !value.Enabled }); newUploads.Checked = !value.Enabled; return Task.CompletedTask;
        })));
        actions.Controls.Add(Button("打开本地位置", async (_, _) => await PerformAsync(_ =>
        {
            var task = syncTasks.CurrentRow?.Tag as SyncTask ?? throw new ArgumentException("请先选择任务。");
            var config = index.Get(task.ConnectionId) ?? throw new ArgumentException("连接已删除。");
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true }; start.ArgumentList.Add("/select," + Paths.Under(config.SyncRoot, task.SourcePath)); using var process = Process.Start(start); return Task.CompletedTask;
        })));
        layout.Controls.Add(actions, 0, 2);
        foreach (string column in new[] { "任务", "文件", "状态", "提交阶段", "重试", "传输字节", "原因" }) syncTasks.Columns.Add(column, column);
        layout.Controls.Add(syncTasks, 0, 3); page.Controls.Add(layout);
    }
    private string Value(string name) => inputs[name].Text.Trim();
    private SmbConnection Edited() => new FolderMapping(editing, (mappingStorage.SelectedItem as StorageConnection ?? throw new ArgumentException("请先保存并选择 NAS。" )).Id, localFolder.Text.Trim(), remoteFolder.Text.Trim().Replace('/', '\\'), auto.Checked).Bind(index.Storage(((StorageConnection)mappingStorage.SelectedItem!).Id));
    private SmbConnection Saved() => index.Get(editing) ?? throw new ArgumentException("请先选择并保存文件夹映射。");
    private void Reset() { connections.ClearSelected(); editing = Guid.NewGuid(); localFolder.Clear(); remoteFolder.Clear(); createFolders.Checked = false; auto.Checked = true; diagnostic.Clear(); }
    private void LoadConfig(SmbConnection c)
    {
        editing = c.Id; localFolder.Text = c.SyncRoot; remoteFolder.Text = c.SubPath; createFolders.Checked = false; auto.Checked = c.AutoConnect;
        mappingStorage.SelectedItem = mappingStorage.Items.Cast<StorageConnection>().SingleOrDefault(s => s.Id == (c.StorageId ?? c.Id));
        var settings = index.SyncSettings(c.Id); newUploads.Checked = settings.Enabled; uploadConcurrency.Value = settings.UploadConcurrency; debounce.Value = settings.DebounceMilliseconds; syncScope.Text = "当前映射：" + c.Name;
        LoadCachePolicy(c.Id);
    }
    private void Reload()
    {
        Guid selected = editing, nasSelected = storageEditing; Guid? chosen = (mappingStorage.SelectedItem as StorageConnection)?.Id;
        storages.Items.Clear(); mappingStorage.Items.Clear();
        foreach (var s in index.Storages()) { storages.Items.Add(s); mappingStorage.Items.Add(s); if (s.Id == nasSelected) storages.SelectedItem = s; if (s.Id == chosen) mappingStorage.SelectedItem = s; }
        if (mappingStorage.SelectedIndex < 0 && mappingStorage.Items.Count > 0) mappingStorage.SelectedIndex = 0;
        connections.Items.Clear(); foreach (var c in index.Connections()) { connections.Items.Add(c); if (c.Id == selected) connections.SelectedItem = c; }
        UpdateHomeProfiles(); RefreshStatus();
    }
    private void RefreshStatus()
    {
        status.Rows.Clear(); var mappings = index.Connections();
        foreach (var c in mappings) status.Rows.Add(c.Name, index.RuntimeStatus(c.Id), index.Count(c.Id), index.ScanStatus(c.Id));
        foreach (var s in index.Storages().Where(s => !mappings.Any(c => (c.StorageId ?? c.Id) == s.Id)))
            status.Rows.Add(s.Name, "尚未配置文件夹映射；可在 NAS 页连接测试", "—", "尚未扫描");
        logs.BeginUpdate(); logs.Items.Clear(); foreach (string line in index.Logs()) logs.Items.Add(line); logs.EndUpdate();
        var selected = (syncTasks.CurrentRow?.Tag as SyncTask)?.Id; syncTasks.Rows.Clear();
        foreach (var task in index.SyncTasks(editing))
        {
            int row = syncTasks.Rows.Add(task.Id, task.SourcePath, task.TaskStatus, task.Phase, task.RetryCount, task.TransferredBytes, task.ErrorMessage ?? ""); syncTasks.Rows[row].Tag = task;
            if (task.Id == selected) syncTasks.CurrentCell = syncTasks.Rows[row].Cells[0];
        }
        RefreshCacheViews();
        RefreshArchiveViews();
    }
    private async Task PerformAsync(Func<CancellationToken, Task> action)
    {
        if (operation is not null) { diagnostic.Text = "已有操作运行；可先取消。"; return; }
        operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); SetConnectionBusy(true);
        try { await action(operation.Token); }
        catch (ArgumentException error) { productMessage.Text = diagnostic.Text = error.Message; }
        catch (System.Security.Cryptography.CryptographicException error) { productMessage.Text = diagnostic.Text = error.Message; }
        catch (Exception error) { productMessage.Text = diagnostic.Text = error is IOException ? error.Message : FailureClassifier.Describe("UI", error).Message; }
        finally { operation.Dispose(); operation = null; SetConnectionBusy(false); RefreshStatus(); }
    }
    private async Task SaveAsync(CancellationToken token)
    {
        var config = Edited(); var old = index.Get(config.Id); index.ValidateSave(config);
        await StopAsync(config.Id);
        using var gates = MappingMaintenance.Acquire([config.Id]);
        if (old is not null) await runtime.Mapper.DisconnectOwnedAsync(old, token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var directories = new MappingDirectories(index, runtime.Mapper.ConnectAsync);
            if (createFolders.Checked) await directories.PrepareAsync(config, true, timeout.Token);
            else directories.ValidateLocal(config);
            token.ThrowIfCancellationRequested(); index.Save(config); Reload(); diagnostic.Text = "文件夹映射已保存。可扫描目录，再启动 Agent 生成占位并按需读取；目录创建不代表完整双向同步。";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        { throw new IOException("映射未保存：" + (e is OperationCanceledException ? "已取消或超时。" : e.Message) + " 已创建的空目录会保留；没有删除文件。", e); }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await runtime.Mapper.DisconnectOwnedAsync(config, cleanup.Token); }
            catch (Exception e) when (e is IOException or OperationCanceledException) { index.Log(config.Id, "mapping-session-retained", "保存映射后的临时SMB会话未能解除，保留所属记录供重试。"); }
        }
    }
    private async Task ScanAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); await ProductCommand(new("scan", Saved().Id));
    }
    private async Task DeleteAsync(CancellationToken token)
    {
        var c = Saved(); await StopAsync(c.Id); using var gates = MappingMaintenance.Acquire([c.Id]); await runtime.Mapper.DisconnectOwnedAsync(c, token);
        index.Delete(c.Id); Reset(); Reload(); diagnostic.Text = "映射已删除。NAS 配置及共享凭据保留。既有同步根和恢复历史仍受保护。";
    }
    private async Task StartAsync(bool elevate)
    {
        try
        {
            var c = Saved();
            if (c.Port != 445 && !SmbMapper.Elevated)
            {
                if (!elevate) { diagnostic.Text = "请使用“自定义端口 UAC 启动”；普通 445 连接不请求提权。"; return; }
                var start = AgentProcess("smb-run-elevated", c.Id.ToString(), WindowsIdentity.GetCurrent().User!.Value); start.UseShellExecute = true; start.Verb = "runas"; start.WindowStyle = ProcessWindowStyle.Hidden;
                using var worker = Process.Start(start); diagnostic.Text = "已请求 UAC。提权 Agent 将在同一用户下完成映射、扫描和读取。";
            }
            else await ProductCommand(new("connection-start", c.Id));
            await Task.CompletedTask;
        }
        catch (Exception error) { diagnostic.Text = error is ArgumentException ? error.Message : FailureClassifier.Describe("start", error).Message; }
    }
    private async Task StopAsync(Guid id)
    {
        if (smoke) return; // Smoke owns a separate fixture database and must never stop the user's Agent.
        try { await ManagementClient.SendAsync(new("connection-stop", id), lifetime.Token, timeoutSeconds: 2); }
        catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException) { lifetime.Token.ThrowIfCancellationRequested(); }
        AgentRuntime.Stop(id);
        for (int attempt = 0; attempt < 60; attempt++)
        {
            try { using var gate = ConnectionLock.Acquire(id, "agent"); return; }
            catch (IOException) { await Task.Delay(500, lifetime.Token); }
        }
        throw new IOException("该连接仍在安全收尾；配置及凭据保持，请稍后重试。");
    }
    private static ProcessStartInfo AgentProcess(params string[] args)
    {
        // Use the running .NET installation explicitly, including portable deployments.
        string dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
        var start = new ProcessStartInfo(dotnet); start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "CloudDrive.Agent.dll")); foreach (var arg in args) start.ArgumentList.Add(arg); return start;
    }
    private void ConfigureLogin()
    {
        if (smoke) return;
        try
        {
            LoginStartup.Set(login.Checked);
        }
        catch (Exception error) { diagnostic.Text = FailureClassifier.Describe("startup", error).Message; }
    }
    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (closing) return;
        if (hideToTray && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
        closing = true; refresh.Stop(); lifetime.Cancel();
        tray.Visible = false; tray.Dispose(); showEvent?.Dispose(); refresh.Dispose(); lifetime.Dispose();
    }
}
