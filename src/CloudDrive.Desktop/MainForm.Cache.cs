using CloudDrive.Storage;

namespace CloudDrive.Desktop;

public sealed partial class MainForm
{
    private readonly SectionTabs cachePages = new() { Dock = DockStyle.Fill };
    private readonly Label cacheOverview = new() { Dock = DockStyle.Fill, AutoSize = false, Text = "请刷新实际状态。自动清理默认关闭；真实 NAS 尚未验收。" };
    private readonly Label cacheMessage = new() { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(40, 80, 130), AutoEllipsis = true };
    private readonly TextBox cacheScope = new() { Width = 240, PlaceholderText = @"相对文件/文件夹；空白表示整个连接" };
    private readonly ComboBox cacheFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 165 };
    private readonly CheckBox cacheAll = new() { Text = "全部存储", AutoSize = true };
    private readonly DataGridView cacheFiles = Grid();
    private readonly DataGridView cacheHistory = Grid();
    private readonly DataGridView cacheDetails = Grid();
    private readonly TextBox cacheGroups = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
    private readonly Dictionary<string, NumericUpDown> cacheNumbers = [];
    private readonly CheckBox cacheEnabled = new() { Text = "启用自动清理", AutoSize = true };
    private readonly CheckBox cacheAge = new() { Text = "按访问时间", AutoSize = true };
    private readonly CheckBox cacheCapacity = new() { Text = "按缓存容量", AutoSize = true };
    private readonly CheckBox cacheDisk = new() { Text = "按磁盘剩余空间", AutoSize = true };
    private bool cacheRefreshing;
    private DateTimeOffset nextCacheView;
    private static DataGridView Grid() => new() { Dock = DockStyle.Fill, ReadOnly = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, AllowUserToAddRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true };
    private void BuildCacheCenter(TabPage page)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new(SizeType.Absolute, 85)); layout.RowStyles.Add(new(SizeType.Absolute, 35)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.Controls.Add(cacheOverview, 0, 0); layout.Controls.Add(cacheMessage, 0, 1); layout.Controls.Add(cachePages, 0, 2); page.Controls.Add(layout);
        var filesPage = new TabPage("文件与统计") { Padding = new(6) }; var policyPage = new TabPage("清理策略") { Padding = new(12) }; var historyPage = new TabPage("清理任务与历史") { Padding = new(6) };
        cachePages.TabPages.AddRange([filesPage, policyPage, historyPage]);
        var filesLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1 };
        filesLayout.RowStyles.Add(new(SizeType.Absolute, 43)); filesLayout.RowStyles.Add(new(SizeType.Absolute, 83)); filesLayout.RowStyles.Add(new(SizeType.Percent, 100)); filesLayout.RowStyles.Add(new(SizeType.Absolute, 100)); filesPage.Controls.Add(filesLayout);
        var filters = new FlowLayoutPanel { Dock = DockStyle.Fill };
        cacheFilter.Items.AddRange(["全部状态", .. Enum.GetNames<CacheState>()]); cacheFilter.SelectedIndex = 0;
        cacheFilter.SelectedIndexChanged += (_, _) => { nextCacheView = DateTimeOffset.MinValue; RefreshCacheViews(); };
        cacheAll.CheckedChanged += (_, _) => { nextCacheView = DateTimeOffset.MinValue; RefreshCacheViews(); };
        filters.Controls.AddRange([cacheAll, cacheFilter, Button("刷新实际占用", async (_, _) => await ProductCommand(new("cache-refresh"))), Button("释放当前连接可清理缓存", async (_, _) => await CacheQueue("Release", ""))]); filesLayout.Controls.Add(filters, 0, 0);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true };
        actions.Controls.Add(cacheScope);
        foreach (var item in new[] { ("释放所选/指定范围", "Release"), ("始终保留", "Pin"), ("取消始终保留", "Unpin") })
        { var action = item; actions.Controls.Add(Button(action.Item1, async (_, _) => await CacheQueue(action.Item2, null))); }
        actions.Controls.Add(Button("保护范围", async (_, _) => await CacheAction(_ => { index.SetCacheExclusion(Saved().Id, cacheScope.Text.Trim(), true); return Task.CompletedTask; })));
        actions.Controls.Add(Button("取消范围保护", async (_, _) => await CacheAction(_ => { index.SetCacheExclusion(Saved().Id, cacheScope.Text.Trim(), false); return Task.CompletedTask; })));
        filesLayout.Controls.Add(actions, 0, 1);
        foreach (string name in new[] { "文件", "存储", "逻辑字节", "水合字节", "实际分配字节", "有效访问", "最近下载", "状态", "固定/保护" }) cacheFiles.Columns.Add(name, name);
        cacheFiles.Columns[0].FillWeight = 180; filesLayout.Controls.Add(cacheFiles, 0, 2); filesLayout.Controls.Add(cacheGroups, 0, 3);
        BuildCachePolicies(policyPage);
        var historyLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3 };
        historyLayout.RowStyles.Add(new(SizeType.Absolute, 45)); historyLayout.RowStyles.Add(new(SizeType.Percent, 55)); historyLayout.RowStyles.Add(new(SizeType.Percent, 45)); historyPage.Controls.Add(historyLayout);
        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill };
        foreach (var item in new[] { ("暂停", "Paused"), ("恢复", "Pending"), ("取消未执行", "Cancelled") })
        {
            var action = item; controls.Controls.Add(Button(action.Item1, async (_, _) =>
            {
                try
                {
                    var run = cacheHistory.CurrentRow?.Tag as CacheRun ?? throw new ArgumentException("请先选择任务"); index.ControlCacheRun(run.Id, action.Item2);
                    cacheMessage.Text = "任务状态已更新；执行中的文件等待安全收尾。"; nextCacheView = DateTimeOffset.MinValue; RefreshCacheViews();
                    if (action.Item2 == "Pending" && operation is null) await ProductCommand(new("cache-retry", ItemId: run.Id));
                }
                catch (Exception e) { cacheMessage.Text = e.Message; }
            }));
        }
        controls.Controls.Add(Button("取消当前批次后续文件", (_, _) => operation?.Cancel())); historyLayout.Controls.Add(controls, 0, 0);
        foreach (string name in new[] { "任务", "开始", "结束", "触发", "状态", "候选", "成功", "失败", "实际释放字节" }) cacheHistory.Columns.Add(name, name);
        foreach (string name in new[] { "文件", "操作", "状态", "操作前字节", "操作后字节", "释放字节", "原因" }) cacheDetails.Columns.Add(name, name);
        cacheHistory.SelectionChanged += (_, _) => ShowCacheDetails(); historyLayout.Controls.Add(cacheHistory, 0, 1); historyLayout.Controls.Add(cacheDetails, 0, 2);
    }
    private void BuildCachePolicies(TabPage page)
    {
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, RowCount = 4, Height = 528, Margin = Padding.Empty };
        foreach (int height in new[] { 48, 330, 50, 100 }) layout.RowStyles.Add(new(SizeType.Absolute, height));
        scroll.Controls.Add(layout); page.Controls.Add(scroll);
        var switches = new FlowLayoutPanel { Dock = DockStyle.Fill }; switches.Controls.AddRange([cacheEnabled, cacheAge, cacheCapacity, cacheDisk]); layout.Controls.Add(switches, 0, 0);
        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        columns.ColumnStyles.Add(new(SizeType.Percent, 50)); columns.ColumnStyles.Add(new(SizeType.Percent, 50));
        var capacity = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6 };
        var retention = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5 };
        foreach (var fields in new[] { capacity, retention }) { fields.ColumnStyles.Add(new(SizeType.Percent, 60)); fields.ColumnStyles.Add(new(SizeType.Percent, 40)); }
        var capacityCard = FormCard("容量与磁盘水位", capacity); capacityCard.Margin = new(0, 0, 6, 8);
        var retentionCard = FormCard("访问保护与执行", retention); retentionCard.Margin = new(6, 0, 0, 8);
        columns.Controls.Add(capacityCard, 0, 0); columns.Controls.Add(retentionCard, 1, 0); layout.Controls.Add(columns, 0, 1);
        string[] labels = ["最大缓存 GiB", "容量触发 GiB", "清理目标 GiB", "磁盘预警剩余 GiB", "磁盘触发剩余 GiB", "磁盘目标剩余 GiB", "允许清理：未访问天数", "最近访问保护天数", "优先清理天数", "高优先清理天数", "清理并发（1–4）"];
        for (int i = 0; i < labels.Length; i++)
        {
            var value = new NumericUpDown { Minimum = i == 10 ? 1 : 0, Maximum = i == 10 ? 4 : i < 6 ? 1048576 : 36500, DecimalPlaces = i < 6 ? 2 : 0, Width = 160 };
            var fields = i < 6 ? capacity : retention; int row = i < 6 ? i : i - 6;
            cacheNumbers.Add(labels[i], value); fields.RowStyles.Add(new(SizeType.Absolute, 41)); fields.Controls.Add(new Label { Text = labels[i], AutoSize = true, Anchor = AnchorStyles.Left }, 0, row); fields.Controls.Add(new InputSurface(value) { Dock = DockStyle.Top }, 1, row);
        }
        layout.Controls.Add(Button("保存当前连接策略", async (_, _) => await CacheAction(_ =>
        {
            long Bytes(int i) => checked((long)(cacheNumbers[labels[i]].Value * (1L << 30))); int Days(int i) => (int)cacheNumbers[labels[i]].Value;
            index.SetCachePolicy(Saved().Id, new(cacheEnabled.Checked, cacheAge.Checked, cacheCapacity.Checked, cacheDisk.Checked, Bytes(0), Bytes(1), Bytes(2), Bytes(3), Bytes(4), Bytes(5), Days(6), Days(7), Days(8), Days(9), Days(10)));
            return Task.CompletedTask;
        })), 0, 2);
        var note = new Label { Text = "策略作用于文件夹映射中当前选中的映射。启动连接的 Agent 后，后台策略按设置生效。\n访问不明、未同步、固定、正在使用或 NAS 不可用时保留缓存；取消固定不等于释放。\n容量采用高低水位；同一物理卷统一规划。真实 NAS 验收尚未执行。", Dock = DockStyle.Fill }; layout.Controls.Add(note, 0, 3);
        LoadCachePolicy(editing);
    }
    private void LoadCachePolicy(Guid connection)
    {
        if (cacheNumbers.Count == 0) return;
        var p = index.CachePolicy(connection); cacheEnabled.Checked = p.Enabled; cacheAge.Checked = p.AgeEnabled; cacheCapacity.Checked = p.CapacityEnabled; cacheDisk.Checked = p.DiskEnabled;
        decimal[] values = [(decimal)p.MaxCacheBytes / (1L << 30), (decimal)p.TriggerCacheBytes / (1L << 30), (decimal)p.TargetCacheBytes / (1L << 30), (decimal)p.MinDiskFreeBytes / (1L << 30), (decimal)p.TriggerDiskFreeBytes / (1L << 30), (decimal)p.TargetDiskFreeBytes / (1L << 30), p.InactivityDays, p.ProtectionDays, p.PriorityDays, p.HighPriorityDays, p.CleanupConcurrency];
        int i = 0; foreach (var input in cacheNumbers.Values) input.Value = Math.Clamp(values[i++], input.Minimum, input.Maximum);
        nextCacheView = DateTimeOffset.MinValue;
    }
    private async Task CacheAction(Func<CancellationToken, Task> action)
    {
        if (operation is not null) { cacheMessage.Text = "已有操作执行中；可在历史页暂停或取消后续文件。"; return; }
        operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); cacheMessage.Text = "执行中…";
        try { await action(operation.Token); cacheMessage.Text = "操作已结束；请查看每个文件的实际结果。"; }
        catch (OperationCanceledException) { cacheMessage.Text = "已停止后续文件；执行中的原生操作等待安全收尾，任务可恢复。"; }
        catch (Exception e) { cacheMessage.Text = e.Message; }
        finally { operation.Dispose(); operation = null; nextCacheView = DateTimeOffset.MinValue; RefreshCacheViews(); }
    }
    private async Task CacheQueue(string type, string? explicitPath)
    {
        await CacheAction(async token =>
        {
            var selected = cacheFiles.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<CacheFile>().ToList();
            var requests = explicitPath is not null ? new[] { (Saved().Id, explicitPath) } : cacheScope.Text.Trim().Length > 0 ? new[] { (Saved().Id, cacheScope.Text.Trim()) } : selected.Count > 0 ? selected.Select(f => (f.Snapshot.ConnectionId, f.Snapshot.RelativePath)).ToArray() : throw new ArgumentException("请选中文件或填写相对范围；整个连接请使用一键释放。");
            foreach (var (connection, path) in requests)
            {
                token.ThrowIfCancellationRequested(); await ProductCommand(new("cache-manual", connection, RelativePath: path, Operation: type));
            }
        });
    }
    private async void RefreshCacheViews()
    {
        if (cacheRefreshing || IsDisposed || DateTimeOffset.UtcNow < nextCacheView || cacheFiles.Columns.Count == 0) return;
        cacheRefreshing = true; nextCacheView = DateTimeOffset.UtcNow.AddSeconds(10);
        try
        {
            Guid? connection = cacheAll.Checked ? null : editing; string filter = cacheFilter.SelectedItem as string ?? "全部状态";
            var data = await Task.Run(() =>
            {
                var snapshots = index.CacheSnapshots(connection).Where(s => !s.IsDirectory).ToList();
                var files = snapshots.Select(index.DecorateCache).ToList(); var configs = index.Connections();
                var volumes = configs.Where(c => connection is null || c.Id == connection).Select(c => { try { return runtime.CachePlatform.Volume(c); } catch { return null; } }).OfType<CacheVolume>().DistinctBy(v => v.Key).ToList();
                return (files, configs, volumes, runs: index.CacheRuns());
            });
            if (IsDisposed) return;
            if (connection != (cacheAll.Checked ? null : (Guid?)editing) || filter != (cacheFilter.SelectedItem as string ?? "全部状态")) { nextCacheView = DateTimeOffset.MinValue; return; }
            long bytes = data.files.Sum(f => f.Snapshot.AllocatedBytes), free = data.volumes.Sum(v => v.AvailableBytes), total = data.volumes.Sum(v => v.TotalBytes);
            var last = data.runs.FirstOrDefault(r => r.Status is "Completed" or "Failed");
            cacheOverview.Text = $"实际本地分配 {bytes:N0} 字节   / 磁盘比例 {(total == 0 ? 0 : 100.0 * bytes / total):F2}%   可用 {free:N0} 字节（按卷去重）\n" +
                $"完整缓存 {data.files.Count(f => f.Snapshot.Owned && f.Snapshot.HydratedBytes >= f.Snapshot.LogicalBytes && f.Snapshot.HydratedBytes > 0)}   仅联机 {data.files.Count(f => f.State == CacheState.OnlineOnly)}   始终保留 {data.files.Count(f => f.State == CacheState.AlwaysAvailable)}\n" +
                $"最近结束 {last?.CompletedAt ?? "尚无"}   该批次释放 {last?.ReleasedBytes ?? 0:N0} 字节；当前策略连接：{data.configs.FirstOrDefault(c => c.Id == editing)?.Name ?? "未选择"}";
            if (data.volumes.Count == 0) cacheOverview.Text = "请选择已保存的文件夹映射，再刷新实际缓存状态。\n当前尚无可读取的同步根磁盘容量。自动清理默认关闭。\n真实 NAS 验收尚未执行。";
            cacheFiles.Rows.Clear();
            foreach (var f in data.files.Where(f => filter == "全部状态" || f.State.ToString() == filter).Take(1000))
            {
                var s = f.Snapshot; int row = cacheFiles.Rows.Add(s.RelativePath, data.configs.FirstOrDefault(c => c.Id == s.ConnectionId)?.Name ?? s.ConnectionId.ToString(), s.LogicalBytes, s.HydratedBytes, s.AllocatedBytes, f.LastUserAccessAt?.LocalDateTime.ToString("g") ?? "未知（自动保护）", f.LastHydratedAt?.LocalDateTime.ToString("g") ?? "未知", f.State, s.Pinned || f.Excluded);
                cacheFiles.Rows[row].Tag = f; cacheFiles.Rows[row].Cells[7].ToolTipText = f.BlockReason ?? s.Error ?? "";
            }
            cacheGroups.Text = "列表最多显示1000项；统计包含全部已校准文件。字节数可点击列头排序。\r\n" + string.Join("； ", data.files.GroupBy(f => (f.Snapshot.ConnectionId, Folder: Paths.Parent(f.Snapshot.RelativePath), Type: Path.GetExtension(f.Snapshot.RelativePath))).Take(80).Select(g => $"{data.configs.FirstOrDefault(c => c.Id == g.Key.ConnectionId)?.Name}/{g.Key.Folder} {g.Key.Type}: {g.Sum(f => f.Snapshot.AllocatedBytes):N0} B"));
            Guid? selection = (cacheHistory.CurrentRow?.Tag as CacheRun)?.Id; cacheHistory.Rows.Clear();
            foreach (var r in data.runs) { int row = cacheHistory.Rows.Add(r.Id, r.StartedAt, r.CompletedAt ?? "", r.Trigger, r.Status, r.Candidates, r.Succeeded, r.Failed, r.ReleasedBytes); cacheHistory.Rows[row].Tag = r; if (r.Id == selection) cacheHistory.CurrentCell = cacheHistory.Rows[row].Cells[0]; }
            ShowCacheDetails();
        }
        catch (Exception e) { if (!IsDisposed) cacheMessage.Text = e.Message; }
        finally { cacheRefreshing = false; }
    }
    private void ShowCacheDetails()
    {
        cacheDetails.Rows.Clear(); if (cacheHistory.CurrentRow?.Tag is not CacheRun run) return;
        foreach (var o in index.CacheOperations(run.Id).Take(1000)) cacheDetails.Rows.Add(o.Path, o.Type, o.Status, o.BeforeBytes, (object?)o.AfterBytes ?? "", o.ReleasedBytes, o.Error ?? "");
    }
}
