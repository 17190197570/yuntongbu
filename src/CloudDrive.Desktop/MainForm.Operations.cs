using CloudDrive.Storage;
using System.Diagnostics;
using System.Text.Json;

namespace CloudDrive.Desktop;

public sealed partial class MainForm
{
    private void AddOperationsButtons(FlowLayoutPanel actions)
    {
        actions.Controls.Add(Button("备份数据库", async (_, _) => await PerformAsync(async ct => { string path = await Task.Run(() => ProductBackup.Create(index, Path.Combine(ProductEnvironment.DataRoot, "Backups")), ct); productMessage.Text = "备份完成（不含密码）：" + path; })));
        actions.Controls.Add(Button("预览并恢复备份", async (_, _) => await RestoreBackup()));
        actions.Controls.Add(Button("诊断预览/导出", async (_, _) => await ShowDiagnostics()));
        actions.Controls.Add(Button("卸载安全检查", async (_, _) => await AssessUninstall()));
        actions.Controls.Add(Button("产品设置", (_, _) => EditProductSettings()));
    }
    private async Task CheckProductUpdate()
    {
        Show();
        await PerformAsync(async ct =>
        {
            var settings = ProductSettings.Load(); var update = await ProductUpdate.CheckAsync(settings, ct);
            productMessage.Text = $"当前 {ProductEnvironment.Version}；可信清单版本 {update.Manifest.Version}\n{update.Manifest.Notes}";
            if (Version.Parse(update.Manifest.Version) <= Version.Parse(ProductEnvironment.Version)) return;
            if (MessageBox.Show(this, productMessage.Text + "\n下载并校验更新包？", "可信更新", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            var progress = new Progress<(long Bytes, long Total)>(p => productMessage.Text = $"下载 {p.Bytes:N0}/{p.Total:N0} 字节。取消当前操作可暂停，已下载部分保留。");
            string package = await ProductUpdate.DownloadAsync(update.Envelope, settings.UpdateChannel, Path.Combine(ProductEnvironment.DataRoot, "Cache", "Updates"), progress, ct);
            productMessage.Text = "安装包已通过清单签名、SHA-256和发布者签名校验：" + package;
            if (MessageBox.Show(this, productMessage.Text + "\n安装将安全停止后台，并保留数据库备份。立即安装？", "安装更新", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            using var process = Process.Start(ProductEnvironment.StartInfo("CloudDrive.Bootstrap", "--apply-update", package)) ?? throw new IOException("更新协调程序启动失败。");
            closing = true; tray.Visible = false; tray.Dispose(); refresh.Stop(); Close();
        });
    }
    private async Task RestoreBackup()
    {
        using var picker = new OpenFileDialog { Filter = "CloudDrive Lite 备份|CloudDriveLite-*.zip", CheckFileExists = true };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        await PerformAsync(async ct =>
        {
            var manifest = ProductBackup.Inspect(picker.FileName);
            bool same = manifest.UserKey == ProductEnvironment.UserKey && manifest.DeviceKey == ProductBackup.DeviceKey;
            string text = $"备份：{manifest.CreatedAt.ToLocalTime():g}，软件 {manifest.Version}，数据库 v{manifest.Schema}\n{(same ? "原用户/设备。" : "其他用户/设备：凭据需要重新输入，原路径也必须重新核对。清单中的设备标识不证明备份来源。此备份仍需完整性校验。\n")}恢复前会再备份当前数据。检测到较新任务/映射时拒绝覆盖。\n恢复后自动连接、上传及归档均暂停，需要逐项核对。继续？";
            if (MessageBox.Show(this, text, "恢复预检", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            refresh.Stop();
            try
            {
                await ProductMaintenance.StopAsync(index, ct);
                using var offline = ProductMaintenance.AcquireOffline(index);
                string before = await Task.Run(() => ProductBackup.RestoreOffline(index, picker.FileName, Path.Combine(ProductEnvironment.DataRoot, "Backups"), ProductEnvironment.SettingsPath, true), ct);
                LoginStartup.Set(false); Reload(); productMessage.Text = "已恢复并检查完整性。请核对连接和凭据后手动启动。恢复前备份：" + before;
            }
            finally { refresh.Start(); }
        });
    }
    private async Task ShowDiagnostics()
    {
        await PerformAsync(async ct =>
        {
            ProductSnapshot? state = null;
            try { state = (await ManagementClient.SendAsync(new("status"), ct, timeoutSeconds: 2)).Snapshot; } catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException) { ct.ThrowIfCancellationRequested(); }
            var report = await Task.Run(() => ProductDiagnostics.Preview(index, state), ct);
            using var dialog = new Form { Text = "脱敏诊断预览 · 不导出原始日志或路径", Width = 900, Height = 700, StartPosition = FormStartPosition.CenterParent };
            var preview = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, Text = ProductDiagnostics.Render(report) };
            var export = Button("导出所见报告", (_, _) => { productMessage.Text = ProductDiagnostics.Export(report, Path.Combine(ProductEnvironment.DataRoot, "Diagnostics")); dialog.Close(); }); export.Dock = DockStyle.Bottom;
            dialog.Controls.Add(preview); dialog.Controls.Add(export); ApplyProductTheme(dialog); dialog.ShowDialog(this);
        });
    }
    private async Task AssessUninstall()
    {
        await PerformAsync(async ct =>
        {
            var assessment = await Task.Run(() => ProductUninstall.Assess(index, runtime.CachePlatform), ct);
            productMessage.Text = JsonSerializer.Serialize(assessment, new JsonSerializerOptions { WriteIndented = true });
            MessageBox.Show(this, $"待处理任务：{assessment.UnfinishedTasks}\n受管根：{assessment.ManagedRoots}\n未知/修改项：{assessment.UnknownOrModified}\n仅联机/部分水合：{assessment.OnlineOnly}\n固定：{assessment.Pinned}\n保留完整文件所需下载至少：{assessment.BytesToHydrate:N0} 字节\n\n{assessment.Reason}\n\n本开发包仅提供检查和取消；非空根的方案A/B转换清理尚未开放。", "卸载安全检查", MessageBoxButtons.OK, assessment.Allowed ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        });
    }
    private void EditProductSettings()
    {
        using var dialog = CreateProductSettingsDialog(); dialog.ShowDialog(this);
    }
    private Form CreateProductSettingsDialog()
    {
        var settings = smoke ? new ProductSettings() : ProductSettings.Load();
        var dialog = new Form { Text = "产品设置", Width = 760, Height = 650, MinimumSize = new(720, 610), StartPosition = FormStartPosition.CenterParent };
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true, Padding = new(16) }; table.ColumnStyles.Add(new(SizeType.Absolute, 210)); table.ColumnStyles.Add(new(SizeType.Percent, 100));
        var fields = new Dictionary<string, TextBox>();
        var values = new[] { ("更新地址（HTTPS）", settings.UpdateManifestUrl ?? ""), ("更新渠道 stable/preview", settings.UpdateChannel), ("更新检查间隔（小时）", settings.UpdateHours.ToString()), ("自动备份间隔（小时）", settings.BackupHours.ToString()), ("自动备份保留份数", settings.BackupKeep.ToString()), ("日志保留天数", settings.LogDays.ToString()), ("单日志文件 MiB", settings.LogFileMiB.ToString()), ("日志级别", settings.LogLevel) };
        int row = 0;
        var updates = new CheckBox { Text = "启用启动及周期更新检查（需可信发布配置）", Checked = settings.CheckUpdates, AutoSize = true }; table.Controls.Add(updates, 0, row++); table.SetColumnSpan(updates, 2);
        foreach (var value in values) { table.RowStyles.Add(new(SizeType.Absolute, 45)); var box = new TextBox { Text = value.Item2, Dock = DockStyle.Fill }; fields[value.Item1] = box; table.Controls.Add(new Label { Text = value.Item1, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row); table.Controls.Add(new InputSurface(box) { Dock = DockStyle.Top }, 1, row++); }
        table.Controls.Add(Button("保存", (_, _) =>
        {
            try
            {
                if (!smoke) ProductSettings.Change(current => current with { CheckUpdates = updates.Checked, UpdateManifestUrl = fields[values[0].Item1].Text.Trim() is var url && url.Length > 0 ? url : null, UpdateChannel = fields[values[1].Item1].Text.Trim(), UpdateHours = int.Parse(fields[values[2].Item1].Text), BackupHours = int.Parse(fields[values[3].Item1].Text), BackupKeep = int.Parse(fields[values[4].Item1].Text), LogDays = int.Parse(fields[values[5].Item1].Text), LogFileMiB = int.Parse(fields[values[6].Item1].Text), LogLevel = fields[values[7].Item1].Text.Trim() });
                dialog.Close();
            }
            catch (Exception e) { MessageBox.Show(dialog, e.Message, "设置未保存"); }
        }), 1, row);
        dialog.Controls.Add(table); ApplyProductTheme(dialog); return dialog;
    }
}
