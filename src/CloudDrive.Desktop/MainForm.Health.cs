using CloudDrive.Storage;
using System.Text.Json;

namespace CloudDrive.Desktop;

public sealed partial class MainForm
{
    private readonly Label healthOverview = new() { Dock = DockStyle.Top, Height = 65, Text = "健康状态：Unknown · 尚未检测", Font = new("Microsoft YaHei UI", 12) };
    private readonly DataGridView healthModules = HealthGrid();
    private readonly DataGridView healthIncidents = HealthGrid();
    private readonly TextBox healthDetail = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
    private readonly ComboBox healthModuleFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly ComboBox healthSeverityFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly ComboBox healthTimeFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private HealthSnapshot? displayedHealth;
    private FileDiagnosis? lastFileDiagnosis;
    private HealthNotificationPolicy? healthNotifications;
    private DateTimeOffset lastHealthNotice;
    private bool healthPolling;
    private static DataGridView HealthGrid() => new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };
    private void BuildHealthCenter(TabPage page)
    {
        var tabs = new SectionTabs { Dock = DockStyle.Fill }; var modules = new TabPage("模块与依据"); var incidents = new TabPage("异常与恢复"); var detail = new TabPage("详情及历史"); tabs.TabPages.AddRange([modules, incidents, detail]);
        modules.Controls.Add(healthModules); incidents.Controls.Add(healthIncidents); detail.Controls.Add(healthDetail);
        foreach (string name in new[] { "模块", "状态", "检测时间", "耗时ms", "诊断代码", "结论" }) healthModules.Columns.Add(name, name);
        foreach (string name in new[] { "模块", "等级", "状态", "最近发生", "次数", "原因" }) healthIncidents.Columns.Add(name, name);
        healthModuleFilter.Items.Add("全部模块"); healthModuleFilter.Items.AddRange(HealthModules.All); healthModuleFilter.SelectedIndex = 0;
        healthSeverityFilter.Items.AddRange(["全部等级", "Warning", "Degraded", "Critical"]); healthSeverityFilter.SelectedIndex = 0;
        healthTimeFilter.Items.AddRange(["24小时及未解决", "最近1小时", "仅未解决"]); healthTimeFilter.SelectedIndex = 0;
        var filters = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 43 }; filters.Controls.AddRange([healthModuleFilter, healthSeverityFilter, healthTimeFilter]); incidents.Controls.Add(filters);
        foreach (var filter in new[] { healthModuleFilter, healthSeverityFilter, healthTimeFilter }) filter.SelectedIndexChanged += (_, _) => RenderHealthIncidents();
        healthModules.CellDoubleClick += (_, _) => { if (healthModules.CurrentRow?.Tag is HealthResult r) { healthDetail.Text = JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true }) + "\r\n\r\n最近100次检查：\r\n" + JsonSerializer.Serialize(index.HealthHistory(r.Key), new JsonSerializerOptions { WriteIndented = true }) + "\r\n相关原始日志请在任务日志/日志目录按模块与连接ID查阅，默认诊断不导出原始日志。"; tabs.SelectedTab = detail; } };
        healthIncidents.CellDoubleClick += (_, _) => { if (healthIncidents.CurrentRow?.Tag is HealthIncident i) { healthDetail.Text = JsonSerializer.Serialize(i, new JsonSerializerOptions { WriteIndented = true }) + "\r\n修复记录：\r\n" + JsonSerializer.Serialize(displayedHealth?.Recoveries.Where(a => a.IncidentId == i.IncidentId), new JsonSerializerOptions { WriteIndented = true }); tabs.SelectedTab = detail; } };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 85, AutoScroll = true };
        actions.Controls.Add(Button("立即完整检查", async (_, _) => { await ProductCommand(new("health-check")); await PollHealth(); }));
        actions.Controls.Add(Button("刷新结果", async (_, _) => await PollHealth()));
        actions.Controls.Add(Button("查看允许的修复", async (_, _) => await RepairHealthIncident()));
        actions.Controls.Add(Button("诊断预览/导出", (_, _) => ExportHealthReport()));
        actions.Controls.Add(Button("检查与通知设置", (_, _) => EditHealthSettings()));
        actions.Controls.Add(Button("诊断选中连接的文件", async (_, _) => await DiagnoseHealthFile()));
        page.Controls.Add(tabs); page.Controls.Add(actions); page.Controls.Add(healthOverview);
        if (smoke) { displayedHealth = index.ReadHealth(); RenderHealth(); healthOverview.Text += " · UI冒烟：空测试库，未连接Agent"; }
        if (!smoke) healthNotifications = new(Path.Combine(ProductEnvironment.DataRoot, "Config", "health-notifications.json"), index.OpenHealthIncidentIds);
    }
    private async Task PollHealth()
    {
        if (healthPolling || closing) return; healthPolling = true;
        try
        {
            var reply = await ManagementClient.SendAsync(new("health-status"), lifetime.Token, timeoutSeconds: 2);
            displayedHealth = reply.Health ?? throw new IOException("没有健康快照。"); RenderHealth();
            if (!smoke && DateTimeOffset.UtcNow - lastHealthNotice > TimeSpan.FromSeconds(30))
            {
                var settings = ProductSettings.Load();
                var notice = healthNotifications?.Select(displayedHealth, settings.HealthWarningNotifications, settings.HealthRecoveryNotifications).FirstOrDefault();
                if (notice is not null) { tray.ShowBalloonTip(7000, notice.Title, notice.Text, notice.Critical ? ToolTipIcon.Error : notice.Recovered ? ToolTipIcon.Info : ToolTipIcon.Warning); healthNotifications!.Delivered(notice); lastHealthNotice = DateTimeOffset.UtcNow; }
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException)
        { if (!closing) { if (displayedHealth is { } old) { displayedHealth = old with { Status = HealthStatus.Unknown, Modules = old.Modules.Select(r => r with { Status = HealthStatus.Unknown, Code = "AgentUnreachable", Recovery = [] }).ToArray() }; RenderHealth(); } healthOverview.Text = "健康状态：Unknown · Agent/IPC不可达。已有健康记录保留；不能以旧绿色结果代表当前状态。"; } }
        finally { healthPolling = false; }
    }
    private void RenderHealth()
    {
        if (displayedHealth is not { } h) return;
        healthOverview.Text = $"全局：{h.Status} · 正常{h.Modules.Count(r => r.Status == HealthStatus.Healthy)} / {h.Modules.Count} · 警告{h.Modules.Count(r => r.Status == HealthStatus.Warning)} · 严重{h.Modules.Count(r => r.Status == HealthStatus.Critical)}\n检查快照：{h.At.ToLocalTime():g} · 最近异常：{h.Incidents.Select(i => (DateTimeOffset?)i.LastAt).Max()?.ToLocalTime():g} · 7天异常：{h.LastSevenDayIncidents}";
        healthModules.Rows.Clear(); foreach (var r in h.Modules) { int row = healthModules.Rows.Add(r.Key, r.Status, r.CheckedAt.ToLocalTime().ToString("g"), r.DurationMs, r.Code, r.Summary); healthModules.Rows[row].Tag = r; }
        RenderHealthIncidents();
    }
    private void RenderHealthIncidents()
    {
        if (displayedHealth is null) return; healthIncidents.Rows.Clear();
        foreach (var i in displayedHealth.Incidents.Where(i => (healthModuleFilter.SelectedIndex == 0 || i.ModuleName.StartsWith(healthModuleFilter.Text + ":", StringComparison.Ordinal)) && (healthSeverityFilter.SelectedIndex == 0 || i.Severity == healthSeverityFilter.Text) && (healthTimeFilter.SelectedIndex != 1 || i.LastAt >= DateTimeOffset.UtcNow.AddHours(-1)) && (healthTimeFilter.SelectedIndex != 2 || i.Status == "Open")))
        { int row = healthIncidents.Rows.Add(i.ModuleName, i.Severity, i.Status, i.LastAt.ToLocalTime().ToString("g"), i.Occurrences, i.Title); healthIncidents.Rows[row].Tag = i; }
    }
    private async Task RepairHealthIncident()
    {
        if (healthIncidents.CurrentRow?.Tag is not HealthIncident incident || incident.Status != "Open") { MessageBox.Show(this, "请在异常与恢复中选择未解决异常。", "健康修复"); return; }
        var result = displayedHealth?.Modules.FirstOrDefault(r => r.Key == incident.ModuleName && r.Code == incident.RootCause);
        if (result?.Recovery is not { Length: > 0 } actions) { MessageBox.Show(this, "此异常没有允许的自动操作。请查看依据和建议；不提供删除、重建或清空按钮。", "健康修复"); return; }
        await PerformAsync(async ct =>
        {
            var response = await ManagementClient.SendAsync(new("health-plan", ItemId: Guid.Parse(incident.IncidentId), Operation: actions[0]), ct);
            var plan = response.Plan ?? throw new IOException(response.Message);
            string text = $"当前异常：{plan.Problem}\n方案：{plan.Action}\n风险：{plan.Risk}\n影响：{plan.Effect}\n重启进程：{plan.RestartsProcess}\n需重新输入凭据：{plan.NeedsCredentials}\n\n提交后将独立复检，不能保证修复成功。继续？";
            if (MessageBox.Show(this, text, "确认具体修复", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            bool authorized = plan.Risk != RecoveryRisk.ExplicitDataAuthorization || MessageBox.Show(this, "明确授权恢复原归档事务：该操作在校验提交后可能删除源NAS文件。取消将不执行。", "涉及源文件删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
            if (!authorized) return;
            var reply = await ManagementClient.SendAsync(new("health-recover", ItemId: Guid.Parse(incident.IncidentId), Operation: plan.Action, Confirmed: true, DataAuthorized: plan.Risk == RecoveryRisk.ExplicitDataAuthorization), ct);
            healthDetail.Text = reply.Message; if (!reply.Success) throw new IOException(reply.Message);
        });
    }
    private void ExportHealthReport()
    {
        if (displayedHealth is null) { MessageBox.Show(this, "尚无健康快照，请先检测。", "健康诊断"); return; }
        var preview = HealthReports.Preview(displayedHealth, lastFileDiagnosis);
        using var dialog = new Form { Text = "脱敏健康诊断预览 · 导出所见内容", Width = 940, Height = 740, StartPosition = FormStartPosition.CenterParent };
        dialog.Controls.Add(new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, Text = HealthReports.Json(preview) });
        var export = Button("导出所见JSON/HTML/ZIP", (_, _) =>
        {
            using var picker = new SaveFileDialog { Filter = "JSON|*.json|HTML|*.html|ZIP诊断包|*.zip", FileName = "CloudDriveLite-Health-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), AddExtension = true };
            if (picker.ShowDialog(dialog) != DialogResult.OK) return;
            try { healthDetail.Text = HealthReports.Export(preview, picker.FileName); dialog.Close(); } catch (Exception e) { MessageBox.Show(dialog, e.Message, "诊断导出未完成"); }
        }); export.Dock = DockStyle.Bottom; dialog.Controls.Add(export); ApplyProductTheme(dialog); dialog.ShowDialog(this);
    }
    private async Task DiagnoseHealthFile()
    {
        using var dialog = new Form { Text = "按已保存连接诊断文件 · 只读检查", Width = 680, Height = 210, StartPosition = FormStartPosition.CenterParent };
        var path = new TextBox { Dock = DockStyle.Top, PlaceholderText = "相对于当前映射本地文件夹的路径，例如 资料\\说明.txt" };
        dialog.Controls.Add(new Label { Dock = DockStyle.Top, Height = 55, Text = "先在文件夹映射中选中已保存的映射。仅检查本地元数据、Provider、映射、归档路由与远端版本只读打开。" });
        dialog.Controls.Add(path); var check = Button("检查", (_, _) => { dialog.DialogResult = DialogResult.OK; dialog.Close(); }); check.Dock = DockStyle.Bottom; dialog.Controls.Add(check);
        ApplyProductTheme(dialog); if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await PerformAsync(async ct => { var c = Saved(); var reply = await ManagementClient.SendAsync(new("health-diagnose-file", ConnectionId: c.Id, RelativePath: Paths.Relative(path.Text)), ct, timeoutSeconds: 18); if (!reply.Success) throw new IOException(reply.Message); lastFileDiagnosis = reply.Diagnosis; healthDetail.Text = JsonSerializer.Serialize(reply.Diagnosis, new JsonSerializerOptions { WriteIndented = true }); MessageBox.Show(this, healthDetail.Text, "文件诊断依据 · 完整结果见详情及历史"); });
    }
    private void EditHealthSettings()
    {
        using var dialog = CreateHealthSettingsDialog(); dialog.ShowDialog(this);
    }
    private Form CreateHealthSettingsDialog()
    {
        var dialog = new Form { Text = "健康检查配置", Width = 1000, Height = 620, StartPosition = FormStartPosition.CenterParent };
        var grid = new DataGridView { Dock = DockStyle.Fill, AllowUserToAddRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false };
        grid.Columns.Add("Module", "模块"); grid.Columns[0].ReadOnly = true; grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Enabled", HeaderText = "启用" });
        foreach (string name in new[] { "间隔秒", "超时秒", "警告阈值秒", "严重阈值秒" }) grid.Columns.Add(name, name);
        grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Auto", HeaderText = "自动校准（仅Cache）" });
        foreach (var c in index.HealthConfigurations()) grid.Rows.Add(c.ModuleName, c.Enabled, c.CheckIntervalSeconds, c.TimeoutSeconds, c.WarningThreshold, c.CriticalThreshold, c.AutoRecoveryEnabled);
        var settings = smoke ? new ProductSettings() : ProductSettings.Load(); var warning = new CheckBox { Text = "通知警告/降级（严重故障始终通知）", Checked = settings.HealthWarningNotifications, AutoSize = true }; var recovery = new CheckBox { Text = "通知已确认恢复", Checked = settings.HealthRecoveryNotifications, AutoSize = true };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 90 }; actions.Controls.AddRange([warning, recovery]); actions.Controls.Add(Button("保存", (_, _) =>
        {
            try
            {
                grid.EndEdit(); var configs = grid.Rows.Cast<DataGridViewRow>().Select(r => new HealthConfiguration(Convert.ToString(r.Cells[0].Value) ?? throw new IOException("缺少模块"), Convert.ToBoolean(r.Cells[1].Value), Convert.ToInt32(r.Cells[2].Value), Convert.ToInt32(r.Cells[3].Value), Convert.ToInt32(r.Cells[4].Value), Convert.ToInt32(r.Cells[5].Value), Convert.ToBoolean(r.Cells[6].Value)).Validate()).ToArray();
                foreach (var c in configs) index.SetHealthConfiguration(c);
                if (!smoke) ProductSettings.Change(s => s with { HealthWarningNotifications = warning.Checked, HealthRecoveryNotifications = recovery.Checked }); dialog.Close();
            }
            catch (Exception e) { MessageBox.Show(dialog, e.Message, "配置未保存"); }
        })); dialog.Controls.Add(grid); dialog.Controls.Add(actions); ApplyProductTheme(dialog); return dialog;
    }
}
