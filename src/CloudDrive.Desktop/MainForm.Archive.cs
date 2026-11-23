using CloudDrive.Storage;

namespace CloudDrive.Desktop;

public sealed partial class MainForm
{
    private readonly DataGridView archivePolicies = ArchiveGrid();
    private readonly DataGridView archivePreview = ArchiveGrid();
    private readonly DataGridView archiveRuns = ArchiveGrid();
    private readonly DataGridView archiveItems = ArchiveGrid();
    private readonly Label archiveSummary = new() { Dock = DockStyle.Fill, AutoSize = false };
    private readonly Label archivePreviewSummary = new() { Dock = DockStyle.Top, Height = 65 };
    private static DataGridView ArchiveGrid() => new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, RowHeadersVisible = false, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };
    private ArchivePolicy SelectedArchive() => archivePolicies.CurrentRow?.Tag as ArchivePolicy ?? throw new ArgumentException("请先选择归档配置。");
    private void BuildArchiveCenter(TabPage page)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new(SizeType.Absolute, 75)); layout.RowStyles.Add(new(SizeType.Absolute, 85)); layout.RowStyles.Add(new(SizeType.Absolute, 165)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        archiveSummary.Text = "跨存储归档：先复制和校验，再切换映射，最后删除源文件。\n自动执行默认关闭；真实双NAS及归档后编辑回传尚未验收。";
        layout.Controls.Add(archiveSummary, 0, 0);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true };
        actions.Controls.Add(Button("新建归档配置", (_, _) => EditArchive(null)));
        actions.Controls.Add(Button("编辑配置", (_, _) => ArchiveUi(() => EditArchive(SelectedArchive()))));
        actions.Controls.Add(Button("删除配置", (_, _) => ArchiveUi(() => index.DeleteArchivePolicy(SelectedArchive().Id))));
        actions.Controls.Add(Button("启用/暂停计划", (_, _) => ArchiveUi(() => { var p = SelectedArchive(); index.SaveArchivePolicy(p with { Enabled = !p.Enabled }); })));
        actions.Controls.Add(Button("预览（不迁移）", async (_, _) => await PerformAsync(PreviewArchive)));
        actions.Controls.Add(Button("立即归档", async (_, _) => await PerformAsync(async ct => { var p = SelectedArchive(); await ProductCommand(new("archive-start", ItemId: p.Id)); })));
        actions.Controls.Add(Button("全局暂停/恢复", (_, _) => ArchiveUi(() => index.ArchivePaused = !index.ArchivePaused)));
        actions.Controls.Add(Button("取消当前执行", (_, _) => operation?.Cancel()));
        layout.Controls.Add(actions, 0, 1);
        foreach (string name in new[] { "任务", "源存储", "目标存储", "时间条件 AND", "计划", "启用", "最近执行/结果" }) archivePolicies.Columns.Add(name, name);
        layout.Controls.Add(archivePolicies, 0, 2);
        var pages = new SectionTabs { Dock = DockStyle.Fill }; var preview = new TabPage("候选预览") { Padding = new(6) }; var history = new TabPage("执行中心与历史") { Padding = new(6) };
        preview.Controls.Add(archivePreview); preview.Controls.Add(archivePreviewSummary);
        foreach (string name in new[] { "原始路径", "目标路径", "大小", "原创建UTC", "原修改UTC", "资格", "原因" }) archivePreview.Columns.Add(name, name);
        var execution = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 }; execution.RowStyles.Add(new(SizeType.Absolute, 45)); execution.RowStyles.Add(new(SizeType.Percent, 40)); execution.RowStyles.Add(new(SizeType.Percent, 60));
        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill };
        foreach (var pair in new[] { ("暂停执行", "pause"), ("恢复/重试", "resume"), ("取消未开始", "cancel") })
        {
            string action = pair.Item2;
            controls.Controls.Add(Button(pair.Item1, async (_, _) =>
            {
                if (archiveRuns.CurrentRow?.Tag is not ArchiveRun run) return;
                if (action != "resume") ArchiveUi(() => index.ControlArchiveRun(run.Id, action));
                else await PerformAsync(async ct => await ProductCommand(new("archive-retry", ItemId: run.Id)));
            }));
        }
        foreach (string name in new[] { "执行ID", "任务", "触发", "状态", "开始UTC", "结束UTC", "错误" }) archiveRuns.Columns.Add(name, name);
        foreach (string name in new[] { "原路径", "目标路径", "阶段", "已复制", "大小", "源SHA256", "目标SHA256", "已删源", "错误" }) archiveItems.Columns.Add(name, name);
        archiveRuns.SelectionChanged += (_, _) => RefreshArchiveItems();
        execution.Controls.Add(controls, 0, 0); execution.Controls.Add(archiveRuns, 0, 1); execution.Controls.Add(archiveItems, 0, 2); history.Controls.Add(execution);
        pages.TabPages.AddRange([preview, history]); layout.Controls.Add(pages, 0, 3); page.Controls.Add(layout);
    }
    private void ArchiveUi(Action action)
    { try { action(); } catch (Exception e) { MessageBox.Show(this, e.Message, "归档", MessageBoxButtons.OK, MessageBoxIcon.Information); } finally { RefreshArchiveViews(); } }
    private async Task PreviewArchive(CancellationToken ct)
    {
        var p = SelectedArchive(); var preview = await Task.Run(() => runtime.Archive.PreviewAsync(p, ct), ct);
        archivePreview.Rows.Clear();
        foreach (var f in preview.Files.Take(2000)) archivePreview.Rows.Add(f.Entry.RelativePath, f.TargetPath, f.Entry.Size, DateTime.FromFileTimeUtc(f.Entry.CreationTime).ToString("u"), DateTime.FromFileTimeUtc(f.Entry.ModifiedTime).ToString("u"), f.Eligible ? "候选" : "排除", f.Reason);
        archivePreviewSummary.Text = $"只预览：候选 {preview.Candidates}，{preview.CandidateBytes:N0} 字节；排除 {preview.Rejected}。目标可用：{(preview.TargetFreeBytes is long free ? free.ToString("N0") + " 字节" : "未知")}\n预计源释放量仅为候选逻辑大小；未执行删除，不计为已释放。列表最多2000项，统计覆盖全部。";
    }
    private void RefreshArchiveViews()
    {
        if (archivePolicies.Columns.Count == 0) return;
        Guid? selected = (archivePolicies.CurrentRow?.Tag as ArchivePolicy)?.Id, selectedRun = (archiveRuns.CurrentRow?.Tag as ArchiveRun)?.Id;
        var runs = index.ArchiveRuns(); archivePolicies.Rows.Clear();
        foreach (var p in index.ArchivePolicies())
        {
            var last = runs.FirstOrDefault(r => r.ArchiveTaskId == p.Id); string Name(Guid id) => index.Get(id)?.Name ?? "连接缺失";
            int row = archivePolicies.Rows.Add(p.TaskName, Name(p.SourceStorageId), Name(p.TargetStorageId), $"创建>{p.CreationDays}天 AND 修改>{p.ModificationDays}天", $"{p.ScheduleType}/{p.ScheduleDay} {p.ScheduleTime}", p.Enabled, last is null ? "尚未执行" : last.StartedAt + " " + last.Status); archivePolicies.Rows[row].Tag = p;
            if (p.Id == selected) archivePolicies.CurrentCell = archivePolicies.Rows[row].Cells[0];
        }
        archiveRuns.Rows.Clear();
        foreach (var run in runs.Take(300)) { int row = archiveRuns.Rows.Add(run.Id, run.Policy.TaskName, run.Trigger, run.Status, run.StartedAt, run.CompletedAt ?? "", run.Error ?? ""); archiveRuns.Rows[row].Tag = run; if (run.Id == selectedRun) archiveRuns.CurrentCell = archiveRuns.Rows[row].Cells[0]; }
        var s = index.ArchiveStatistics();
        archiveSummary.Text = $"累计完成 {s.Completed} 个，归档 {s.ArchivedBytes:N0} 字节；确认删源 {s.SourceReleasedBytes:N0} 字节（逻辑量）；失败/待恢复 {s.Failed}。最近完成：{s.LastCompletedAt ?? "无"}\n全局{(index.ArchivePaused ? "暂停" : "运行")}；自动计划默认关闭，需运行 Agent 或归档调度命令。真实双NAS及编辑回传尚未验收。";
        RefreshArchiveItems();
    }
    private void RefreshArchiveItems()
    {
        archiveItems.Rows.Clear(); if (archiveRuns.CurrentRow?.Tag is not ArchiveRun run) return;
        var items = index.ArchiveItems(run.Id).ToList();
        foreach (var i in items.Take(2000)) archiveItems.Rows.Add(i.Source.RelativePath, i.TargetPath, i.Status, i.CopiedBytes, i.Source.Size, i.SourceHash ?? "", i.TargetHash ?? "", i.SourceDeleted, i.Error ?? "");
        archiveSummary.Text = archiveSummary.Text.Split("\n本次", StringSplitOptions.None)[0] + $"\n本次 {items.Count} 项；完成 {items.Count(i => i.Status == ArchiveStatus.Completed)} 项 / {items.Where(i => i.Status == ArchiveStatus.Completed).Sum(i => i.Source.Size):N0} 字节；确认删源 {items.Where(i => i.SourceDeleted).Sum(i => i.Source.Size):N0} 字节。";
    }
    private void EditArchive(ArchivePolicy? existing)
    {
        using var form = CreateArchiveEditor(existing); if (form.ShowDialog(this) == DialogResult.OK) RefreshArchiveViews();
    }
    private Form CreateArchiveEditor(ArchivePolicy? existing)
    {
        var form = new Form { Text = "归档配置 · SHA-256 与时间校验必需", Width = 800, Height = 790, MinimumSize = new(740, 660), StartPosition = FormStartPosition.CenterParent, Font = Font };
        var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new(14) }; var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 }; fields.ColumnStyles.Add(new(SizeType.Absolute, 220)); fields.ColumnStyles.Add(new(SizeType.Percent, 100)); panel.Controls.Add(fields); form.Controls.Add(panel);
        int row = 0; void Add(string label, Control input) { fields.RowStyles.Add(new(SizeType.Absolute, 44)); fields.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row); Control editor = input is TextBox or ComboBox or NumericUpDown ? new InputSurface(input) : input; editor.Anchor = AnchorStyles.Left | AnchorStyles.Right; fields.Controls.Add(editor, 1, row++); }
        TextBox Text(string value) => new() { Text = value, Width = 420 };
        NumericUpDown Number(decimal value, decimal min = 1, decimal max = 36500) => new() { Minimum = min, Maximum = max, Value = value, Width = 120 };
        CheckBox Flag(bool value) => new() { Checked = value, AutoSize = true };
        var all = index.Connections(); var p = existing ?? new ArchivePolicy(Guid.NewGuid(), "新归档任务", all.FirstOrDefault()?.Id ?? Guid.Empty, all.Skip(1).FirstOrDefault()?.Id ?? Guid.Empty, TimeZoneId: TimeZoneInfo.Local.Id);
        ComboBox Storage(Guid id) { var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Name", Width = 420 }; foreach (var c in all) box.Items.Add(c); box.SelectedItem = all.FirstOrDefault(c => c.Id == id); return box; }
        var name = Text(p.TaskName); var source = Storage(p.SourceStorageId); var sourcePath = Text(p.SourceRootPath); var target = Storage(p.TargetStorageId); var targetPath = Text(p.TargetRootPath);
        var creation = Number(p.CreationDays); var modification = Number(p.ModificationDays); var recursive = Flag(p.Recursive); var preserve = Flag(p.PreserveStructure); var hidden = Flag(p.IncludeHidden); var system = Flag(p.IncludeSystem); var pinned = Flag(p.IncludePinned);
        var enabled = Flag(p.Enabled); var type = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }; type.Items.AddRange(["Weekly", "Monthly"]); type.SelectedItem = p.ScheduleType;
        var day = Number(p.ScheduleDay, 0, 28); var time = Text(p.ScheduleTime); var zone = Text(p.TimeZoneId ?? TimeZoneInfo.Local.Id); var window = Number(p.WindowMinutes, 1, 1440); var concurrency = Number(p.Concurrency, 1, 4); var max = Number((p.MaxBytes ?? 0) / (decimal)(1L << 30), 0, 1000000); max.DecimalPlaces = 3;
        var collision = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }; foreach (var item in Enum.GetValues<ArchiveCollision>()) collision.Items.Add(item); collision.SelectedItem = p.Collision;
        var retry = Flag(p.RetryFailures); var notify = Flag(p.Notify);
        Add("任务名称", name); Add("源文件夹映射", source); Add("源目录（映射远端目录内）", sourcePath); Add("目标文件夹映射", target); Add("目标目录（映射远端目录内）", targetPath);
        Add("创建时间超过天数 AND", creation); Add("修改时间超过天数", modification); Add("递归扫描", recursive); Add("保留相对目录结构", preserve); Add("包含隐藏文件", hidden); Add("包含系统文件", system); Add("包含固定文件（需确认）", pinned);
        Add("启用自动执行", enabled); Add("计划类型", type); Add("日期：周0–6 / 月1–28", day); Add("本地执行时间 HH:mm", time); Add("时区 ID", zone); Add("每日执行窗口（分钟）", window); Add("最大并发数", concurrency); Add("单次容量 GiB（0不限）", max); Add("同名策略 Skip/改名/人工", collision); Add("失败自动重试", retry); Add("记录完成通知到任务日志", notify);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 70, Padding = new(10) }; var save = Button("保存配置", (_, _) =>
        {
            try
            {
                bool confirmed = !pinned.Checked || (p.IncludePinned && p.PinnedConfirmed) || MessageBox.Show(form, "包含固定文件会迁移其远端来源，但保持本地缓存和固定状态。确认包含？", "固定文件归档", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                if (!confirmed) return;
                var updated = p with { TaskName = name.Text.Trim(), SourceStorageId = (source.SelectedItem as SmbConnection)?.Id ?? Guid.Empty, TargetStorageId = (target.SelectedItem as SmbConnection)?.Id ?? Guid.Empty, SourceRootPath = sourcePath.Text.Trim(), TargetRootPath = targetPath.Text.Trim(), CreationDays = (int)creation.Value, ModificationDays = (int)modification.Value, Recursive = recursive.Checked, PreserveStructure = preserve.Checked, IncludeHidden = hidden.Checked, IncludeSystem = system.Checked, IncludePinned = pinned.Checked, PinnedConfirmed = confirmed, Enabled = enabled.Checked, ScheduleType = (string)type.SelectedItem!, ScheduleDay = (int)day.Value, ScheduleTime = time.Text.Trim(), TimeZoneId = zone.Text.Trim(), WindowMinutes = (int)window.Value, Concurrency = (int)concurrency.Value, MaxBytes = max.Value == 0 ? null : checked((long)(max.Value * (1L << 30))), Collision = (ArchiveCollision)collision.SelectedItem!, RetryFailures = retry.Checked, Notify = notify.Checked };
                index.SaveArchivePolicy(updated); form.DialogResult = DialogResult.OK; form.Close();
            }
            catch (Exception e) { MessageBox.Show(form, e.Message, "归档配置", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        });
        footer.Controls.Add(save); footer.Controls.Add(Button("取消", (_, _) => { form.DialogResult = DialogResult.Cancel; form.Close(); })); footer.Controls.Add(new Label { Text = "归档验证成功后删除源文件。使用中迁移与已有文件编辑回传暂不支持。", AutoSize = true }); form.Controls.Add(footer); ApplyProductTheme(form); return form;
    }
}
