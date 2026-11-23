using CloudDrive.Storage;
using System.Security;

namespace CloudDrive.Desktop;

public sealed partial class MainForm
{
    private readonly ListBox storages = new() { Dock = DockStyle.Fill, DisplayMember = "Name", HorizontalScrollbar = true };
    private readonly ComboBox mappingStorage = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Name" };
    private readonly TextBox localFolder = new() { Dock = DockStyle.Fill, PlaceholderText = @"例如 D:\CloudDrive\文档" };
    private readonly TextBox remoteFolder = new() { Dock = DockStyle.Fill, PlaceholderText = @"共享内相对路径，例如 资料\文档；留空映射整个共享" };
    private readonly CheckBox createFolders = new() { Text = "创建缺失目录（本地目录及远端共享内子目录）", AutoSize = true };
    private readonly TextBox storageDiagnostic = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private Guid storageEditing = Guid.NewGuid();

    private void BuildStorageConnections(TabPage page)
    {
        var split = new SplitContainer { Width = 1100, Dock = DockStyle.Fill, SplitterDistance = 216, SplitterWidth = 16, FixedPanel = FixedPanel.Panel1, Panel1MinSize = 180, Panel2MinSize = 530 };
        page.Controls.Add(split); split.Panel1.Controls.Add(ConnectionSidebar("我的 NAS", storages, Button("+ 新增 NAS", (_, _) => ResetStorage())));
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1 };
        right.RowStyles.Add(new(SizeType.Absolute, 88)); right.RowStyles.Add(new(SizeType.Absolute, 304)); right.RowStyles.Add(new(SizeType.Absolute, 58)); right.RowStyles.Add(new(SizeType.Percent, 100));
        right.Controls.Add(ConnectionHeading("连接设置", "先保存 NAS，再到“文件夹映射”维护路径。\n带 * 为必填项；密码首次必填，工作组可留空。"), 0, 0);
        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        columns.ColumnStyles.Add(new(SizeType.Percent, 50)); columns.ColumnStyles.Add(new(SizeType.Percent, 50));
        var network = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
        var credentials = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
        string[] names = ["服务器", "端口", "共享名称", "用户名", "密码", "域/工作组"];
        string[] labels = ["连接地址 *", "端口 *", "共享名称 *", "用户名 *", "密码（首次必填）", "域 / 工作组（可选）"];
        for (int field = 0; field < names.Length; field++)
        {
            var fields = field < 3 ? network : credentials; int row = field % 3 * 2;
            fields.RowStyles.Add(new(SizeType.Absolute, 24)); fields.RowStyles.Add(new(SizeType.Absolute, 50));
            fields.Controls.Add(new Label { Text = labels[field], AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            Control editor;
            if (names[field] == "端口") { port.Width = 140; editor = port; }
            else
            {
                var box = new TextBox { Dock = DockStyle.Top, AutoSize = false, Height = 32, UseSystemPasswordChar = names[field] == "密码", AccessibleName = labels[field], PlaceholderText = names[field] switch { "服务器" => "192.168.1.10 或 nas.example.com", "共享名称" => "例如 data，不含子目录", "密码" => "留空保留已保存的密码", "域/工作组" => "例如 WORKGROUP", _ => "NAS 上的登录用户名" } };
                inputs.Add(names[field], box); editor = box;
            }
            var surface = new InputSurface(editor) { Dock = DockStyle.Top, TabIndex = field };
            fields.Controls.Add(surface, 0, row + 1);
        }
        var networkCard = FormCard("网络与共享", network); networkCard.Margin = new(0, 0, 6, 8);
        var accountCard = FormCard("身份认证", credentials); accountCard.Margin = new(6, 0, 0, 8);
        columns.Controls.Add(networkCard, 0, 0); columns.Controls.Add(accountCard, 1, 0); right.Controls.Add(columns, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new(0, 4, 0, 0) };
        actions.Controls.Add(MainAction("保存 NAS", async (_, _) => await PerformAsync(SaveStorageAsync)));
        actions.Controls.Add(Button("连接测试", async (_, _) => await PerformAsync(TestStorageAsync)));
        actions.Controls.Add(DestructiveAction("删除 NAS", async (_, _) => await PerformAsync(DeleteStorageAsync)));
        actions.Controls.Add(CancelConnectionAction()); right.Controls.Add(actions, 0, 2);
        storageDiagnostic.BorderStyle = BorderStyle.None; storageDiagnostic.BackColor = Color.White;
        storageDiagnostic.Text = "尚未测试。填写后先保存 NAS，再执行连接测试。\r\n密码仅保存在此 Windows 用户的凭据管理器中。";
        right.Controls.Add(FormCard("操作结果", storageDiagnostic), 0, 3); split.Panel2.Controls.Add(right);
        diagnostic.TextChanged += (_, _) => storageDiagnostic.Text = diagnostic.Text;
        storages.SelectedIndexChanged += (_, _) => { if (storages.SelectedItem is StorageConnection storage) LoadStorage(storage); };
    }
    private void BuildFolderMappings(TabPage page)
    {
        var split = new SplitContainer { Width = 1100, Dock = DockStyle.Fill, SplitterDistance = 216, SplitterWidth = 16, FixedPanel = FixedPanel.Panel1, Panel1MinSize = 180, Panel2MinSize = 530 };
        page.Controls.Add(split); split.Panel1.Controls.Add(ConnectionSidebar("文件夹映射", connections, Button("+ 新增映射", (_, _) => Reset())));
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        right.RowStyles.Add(new(SizeType.Absolute, 88)); right.RowStyles.Add(new(SizeType.Absolute, 282)); right.RowStyles.Add(new(SizeType.Absolute, 104)); right.RowStyles.Add(new(SizeType.Absolute, 54)); right.RowStyles.Add(new(SizeType.Percent, 100));
        right.Controls.Add(ConnectionHeading("路径设置", "一个 NAS 可以用于多个文件夹映射。\n当前已有文件保持只读；保存映射不会扫描或下载文件。"), 0, 0);
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5 };
        fields.ColumnStyles.Add(new(SizeType.Absolute, 110)); fields.ColumnStyles.Add(new(SizeType.Percent, 100));
        Control[] editors = [mappingStorage, localFolder, remoteFolder, createFolders, auto];
        string[] labels = ["使用 NAS *", "本地文件夹 *", "远端文件夹", "", ""];
        for (int row = 0; row < editors.Length; row++)
        {
            fields.RowStyles.Add(new(SizeType.Absolute, row < 3 ? 42 : 32));
            fields.Controls.Add(new Label { Text = labels[row], AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            if (row == 1)
            {
                var browse = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
                browse.ColumnStyles.Add(new(SizeType.Percent, 100)); browse.ColumnStyles.Add(new(SizeType.Absolute, 80));
                localFolder.AutoSize = false; localFolder.Height = 32; localFolder.Dock = DockStyle.Top; browse.Controls.Add(new InputSurface(localFolder) { Dock = DockStyle.Top }, 0, 0);
                var choose = Button("浏览…", (_, _) => { using var picker = new FolderBrowserDialog { Description = "选择空的本地 NTFS 文件夹或原有受管根", UseDescriptionForTitle = true }; if (picker.ShowDialog(this) == DialogResult.OK) localFolder.Text = picker.SelectedPath; });
                choose.Margin = new(6, 0, 0, 0); browse.Controls.Add(choose, 1, 0); fields.Controls.Add(browse, 1, row);
            }
            else { editors[row].Dock = DockStyle.Top; fields.Controls.Add(editors[row] is TextBox or ComboBox ? new InputSurface(editors[row]) { Dock = DockStyle.Top } : editors[row], 1, row); }
        }
        mappingStorage.AccessibleName = "使用 NAS"; localFolder.AccessibleName = "本地文件夹"; remoteFolder.AccessibleName = "远端文件夹";
        remoteFolder.AutoSize = false; remoteFolder.Height = 32;
        right.Controls.Add(FormCard("路径对应关系", fields), 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new(0, 4, 0, 0) };
        actions.Controls.Add(MainAction("保存映射", async (_, _) => await PerformAsync(SaveAsync)));
        actions.Controls.Add(Button("扫描目录", async (_, _) => await PerformAsync(ScanAsync)));
        actions.Controls.Add(Button("启动 Agent", async (_, _) => await PerformAsync(_ => StartAsync(false))));
        actions.Controls.Add(Button("停止 Agent", async (_, _) => await PerformAsync(_ => StopAsync(editing))));
        actions.Controls.Add(DestructiveAction("删除映射", async (_, _) => await PerformAsync(DeleteAsync)));
        actions.Controls.Add(CancelConnectionAction());
        actions.Controls.Add(Button("自定义端口 UAC 启动", async (_, _) => await PerformAsync(_ => StartAsync(true)))); right.Controls.Add(actions, 0, 2);
        right.Controls.Add(new Label { Text = "勾选“创建缺失目录”后保存会访问 NAS，需有创建目录权限；共享本身必须已存在。\n未勾选只保存映射，可离线配置。已有索引的映射不能直接改换 NAS 或路径。", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(102, 102, 102) }, 0, 3);
        diagnostic.BorderStyle = BorderStyle.None; diagnostic.BackColor = Color.White; right.Controls.Add(FormCard("操作结果", diagnostic), 0, 4); split.Panel2.Controls.Add(right);
    }
    private StorageConnection EditedStorage() => new StorageConnection(storageEditing, Value("服务器"), (int)port.Value, Value("共享名称"), Value("用户名"), Value("域/工作组")).Validate();
    private void ResetStorage() { storages.ClearSelected(); storageEditing = Guid.NewGuid(); foreach (var box in inputs.Values) box.Clear(); port.Value = 445; diagnostic.Clear(); }
    private void LoadStorage(StorageConnection s)
    {
        storageEditing = s.Id; inputs["服务器"].Text = s.Server; port.Value = s.Port; inputs["共享名称"].Text = s.Share; inputs["用户名"].Text = s.UserName; inputs["域/工作组"].Text = s.Domain; inputs["密码"].Clear();
    }
    private async Task SaveStorageAsync(CancellationToken token)
    {
        var storage = EditedStorage(); var old = index.Storages().SingleOrDefault(s => s.Id == storage.Id);
        index.ValidateStorageSave(storage);
        using var password = new SecureString(); foreach (char c in inputs["密码"].Text) password.AppendChar(c);
        if (old is null && password.Length == 0) throw new ArgumentException("首次保存 NAS 需要填写密码。工作组可留空。");
        if (password.Length == 0) { using var existing = vault.Read(storage.CredentialKey); }
        var mappings = index.MappingsForStorage(storage.Id);
        foreach (var mapping in mappings) await StopAsync(mapping.Id);
        using var gates = MappingMaintenance.Acquire(mappings.Select(c => c.Id).Append(storage.Id));
        foreach (var mapping in mappings) await runtime.Mapper.DisconnectOwnedAsync(mapping, token);
        if (old is not null) await runtime.Mapper.DisconnectOwnedAsync(old.Access(), token);
        token.ThrowIfCancellationRequested(); index.SaveStorage(storage);
        if (password.Length > 0) vault.Save(storage.CredentialKey, storage.Account, password);
        inputs["密码"].Clear(); Reload(); diagnostic.Text = "NAS 已保存，密码保存在 Windows 凭据管理器。现在可连接测试，或稍后到“文件夹映射”维护路径。关联 Agent 已安全停止，请在映射页按需启动。";
    }
    private async Task TestStorageAsync(CancellationToken token)
    {
        var saved = index.Storage(storageEditing);
        if (saved != EditedStorage() || inputs["密码"].Text.Length > 0) throw new ArgumentException("请先保存当前 NAS 参数及密码，再测试。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var results = await runtime.Tester.TestAsync(saved.Access(), false, timeout.Token);
        diagnostic.Text = (results.All(d => d.Success) ? "连接测试通过，可以配置文件夹映射。" : "连接测试未通过，请检查下方结果。") + Environment.NewLine + Environment.NewLine +
            string.Join(Environment.NewLine + Environment.NewLine, results.Select(d => (d.Success ? "✓  " : "×  ") + d.Stage + "  ·  " + (d.Success ? "" : "[" + d.Kind + "] ") + d.Message));
        foreach (var d in results) index.Log(saved.Id, "diagnostic", d.Stage + " " + d.Kind + " " + d.Message);
    }
    private async Task DeleteStorageAsync(CancellationToken token)
    {
        var saved = index.Storage(storageEditing);
        if (index.MappingsForStorage(saved.Id).Count > 0) throw new IOException("仍有文件夹映射使用此 NAS，不能删除连接或凭据。");
        using var gates = MappingMaintenance.Acquire([saved.Id]);
        await runtime.Mapper.DisconnectOwnedAsync(saved.Access(), token); index.DeleteStorage(saved.Id); vault.Delete(saved.CredentialKey); ResetStorage(); Reload(); diagnostic.Text = "NAS 配置及其凭据已移除。";
    }
    private async Task CheckConnectionPagesAsync()
    {
        ResetStorage(); Guid fixtureNas = storageEditing;
        inputs["服务器"].Text = "nas.example.invalid"; inputs["共享名称"].Text = "data"; inputs["用户名"].Text = "fixture-user"; inputs["密码"].Text = "UI-FIXTURE-NOT-A-NAS";
        try
        {
            await SaveStorageAsync(lifetime.Token);
            if (index.Connections().Count != 0 || index.Storages().Count != 1 || inputs.Count != 5 || !inputs["密码"].UseSystemPasswordChar || inputs["密码"].Text.Length != 0)
                throw new IOException("NAS页面未通过独立保存或密码保护检查。");
            Reset(); localFolder.Text = Path.Combine(Path.GetDirectoryName(index.DatabasePath)!, "文档"); remoteFolder.Text = @"资料\文档";
            await SaveAsync(lifetime.Token); Guid first = editing;
            Reset(); localFolder.Text = Path.Combine(Path.GetDirectoryName(index.DatabasePath)!, "照片"); remoteFolder.Text = "照片";
            await SaveAsync(lifetime.Token);
            if (index.MappingsForStorage(fixtureNas).Count != 2 || index.Get(first)!.CredentialKey != index.Get(editing)!.CredentialKey || Directory.Exists(localFolder.Text))
                throw new IOException("文件夹映射页面未通过共用NAS及离线保存检查。");
            await DeleteAsync(lifetime.Token);
            using var retained = vault.Read(index.Storage(fixtureNas).CredentialKey);
            if (retained.Length == 0 || index.MappingsForStorage(fixtureNas).Count != 1) throw new IOException("删除映射丢失了共享NAS凭据。");
            LoadConfig(index.Get(first)!); connections.SelectedItem = connections.Items.Cast<SmbConnection>().Single(c => c.Id == first);
            diagnostic.Text = "界面测试样例（未连接 NAS）：NAS 单独保存、两个映射共用凭据及离线保存检查通过。";
        }
        finally { vault.Delete("CloudDriveLite/" + fixtureNas.ToString("N")); }
    }
}
