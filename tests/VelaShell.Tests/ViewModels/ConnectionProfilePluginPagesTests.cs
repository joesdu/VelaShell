using ReactiveUI.Primitives;
using VelaShell.Infrastructure.Plugins.Protocols;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 连接对话框里插件字段按**页键**分页:「常规」、插件声明的自定义页(「数据库」「SSL」……)、「高级」。
/// <para>
/// 机制与边界情况的用例直接 new 一份合成的 <see cref="PluginConnectionForm" />,经 <c>PluginFormOverride</c> 喂给视图模型 ——
/// 其余路径(插件注册表、惰性装载、字段进集合、显示条件、变体)与真实装载一样。
/// SDK 契约(<c>WorkspaceDescriptor.Pages</c>、字段的 <c>Page</c>)到内部模型的映射另有一条走真实装载的用例。
/// </para>
/// <para>纯视图模型用例:不起界面。</para>
/// </summary>
[TestClass]
public sealed class ConnectionProfilePluginPagesTests
{
    [TestMethod]
    public async Task SelectionGroups_FollowPagesAndVisibility_AndSaveOriginalKeys()
    {
        PluginProtocolRegistry registry = Registry(out IDisposable handle);
        using (handle)
        {
            var group = new ProtocolSettingGroup("devices", "Devices");
            PluginConnectionForm form = Form(
                [
                    new() { Key = "showSecond", Label = "Show second", Kind = ProtocolSettingKind.Boolean, DefaultValue = "true" },
                    new() { Key = "drive:C", Label = "C:", Kind = ProtocolSettingKind.Boolean, DefaultValue = "true", SelectionGroup = group },
                    new() { Key = "drive:D", Label = "D:", Kind = ProtocolSettingKind.Boolean, DefaultValue = "false", SelectionGroup = group, VisibleWhen = new("showSecond", "true") },
                    new() { Key = "printer:one", Label = "Printer", Kind = ProtocolSettingKind.Boolean, SelectionGroup = group },
                    new() { Key = "folders", Label = "Folders", SelectionGroup = group },
                    new() { Key = "hidden", Label = "Hidden", Kind = ProtocolSettingKind.Boolean, IsHidden = true, SelectionGroup = group }
                ],
                [new(DatabasePage, "Resources"), new(SslPage, "Printers")],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["showSecond"] = DatabasePage, ["drive:C"] = DatabasePage,
                    ["drive:D"] = DatabasePage, ["folders"] = DatabasePage, ["printer:one"] = SslPage
                });
            ConnectionProfileViewModel vm = await OpenAsync(registry, form);
            PluginFormRow[] groups = [.. vm.PluginFieldRows.Where(row => !row.IsField)];
            Assert.HasCount(2, groups, "The same group key on different pages must stay separate.");
            Assert.HasCount(2, groups[0].Group!.Children, "Hidden fields must not enter the tree.");
            Assert.IsTrue(vm.PluginFieldRows.Any(row => row.Field?.Key == "folders"), "Non-Boolean fields retain their editor.");
            Assert.IsTrue(groups.All(row => !row.IsGroupVisible));

            vm.SelectPluginPageCommand.Execute(DatabasePage).Subscribe();
            PluginSelectionNode drives = groups[0].Group!;
            Assert.IsTrue(groups[0].IsGroupVisible);
            Assert.IsFalse(groups[1].IsGroupVisible);
            Assert.IsNull(drives.IsChecked);
            Assert.AreEqual("1/2", drives.SelectionCount);
            Assert.IsFalse(drives.IsExpanded);
            await drives.ToggleCommand.Execute().FirstAsync();
            Assert.AreEqual(true, drives.IsChecked, "Clicking a partial selection must select all.");
            Assert.AreEqual("true", Row(vm, "drive:D").Text);

            Row(vm, "showSecond").Toggle = false;
            Assert.AreEqual("1/1", drives.SelectionCount);
            await drives.ToggleCommand.Execute().FirstAsync();
            Assert.AreEqual("false", Row(vm, "drive:C").Text);
            Assert.AreEqual("true", Row(vm, "drive:D").Text, "Bulk selection must preserve conditionally hidden values.");
            Row(vm, "showSecond").Toggle = true;
            Assert.IsNull(drives.IsChecked);
            drives.IsExpanded = true;
            vm.SelectPluginPageCommand.Execute(SslPage).Subscribe();
            Assert.IsFalse(groups[0].IsGroupVisible);
            Assert.IsTrue(groups[1].IsGroupVisible);
            vm.SelectPluginPageCommand.Execute(DatabasePage).Subscribe();
            Assert.IsTrue(drives.IsExpanded);
            Assert.IsNull(drives.IsChecked);

            var saved = await vm.SaveCommand.Execute().FirstAsync();
            Assert.IsNotNull(saved?.PluginSettings);
            Assert.AreEqual("false", saved.PluginSettings["drive:C"]);
            Assert.AreEqual("true", saved.PluginSettings["drive:D"]);
            Assert.IsFalse(saved.PluginSettings.ContainsKey("devices"), "Group state must never become a saved setting.");

            var reloadRegistry = new PluginProtocolRegistry();
            using IDisposable reloadHandle = reloadRegistry.RegisterWorkspace(PagedId, new WorkspaceDescriptor
            {
                Id = PagedId, DisplayName = "Resources", DefaultPort = 3389,
                Pages = [new(DatabasePage, "Resources"), new(SslPage, "Printers")],
                Fields = [.. form.Fields.Select(field => field with { Page = form.PageKeyFor(field) })]
            }, new NoopWorkspace());
            var reopened = new ConnectionProfileViewModel(saved, protocolRegistry: reloadRegistry);
            await reopened.PendingPluginLoad;
            reopened.SelectPluginPageCommand.Execute(DatabasePage).Subscribe();
            PluginSelectionNode restored = reopened.PluginFieldRows.First(row => row.Group is not null).Group!;
            Assert.IsNull(restored.IsChecked);
            Assert.AreEqual("1/2", restored.SelectionCount);
        }
    }

    [TestMethod]
    public void SelectionGroups_IgnoreBlankKeys_UseOrdinalKeys_AndDetachObservers()
    {
        PluginProtocolFieldViewModel Field(string key, string group, string label) => new(new()
        {
            Key = key, Label = key, Kind = ProtocolSettingKind.Boolean,
            SelectionGroup = new(group, label)
        }, "false");
        var first = Field("one", "devices", "First label");
        var second = Field("two", "devices", "Ignored label");
        IReadOnlyList<PluginFormRow> rows = PluginFormRow.Build([first, second, Field("three", "Devices", "Other"), Field("four", " ", "Blank")]);
        Assert.HasCount(3, rows);
        Assert.AreEqual("First label", rows[0].Group!.Label);
        Assert.AreEqual("Other", rows[1].Group!.Label);
        Assert.AreEqual("four", rows[2].Field!.Key);
        int notifications = 0;
        rows[0].Group!.PropertyChanged += (_, _) => notifications++;
        first.Toggle = true;
        Assert.IsTrue(notifications > 0);
        foreach (PluginFormRow row in rows) { row.Dispose(); }
        notifications = 0;
        second.Toggle = true;
        Assert.AreEqual(0, notifications);
    }

    private const string PagedId = "test.paged";
    private const string DatabasePage = "database";
    private const string SslPage = "ssl";

    private sealed class NoopWorkspace : IWorkspaceProvider
    {
        public Task<IWorkspaceDocument> OpenAsync(
            WorkspaceConnectRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>注册一个工作台页签;它的字段无所谓 —— 表单由 <c>PluginFormOverride</c> 整个换掉。</summary>
    private static PluginProtocolRegistry Registry(out IDisposable handle)
    {
        var registry = new PluginProtocolRegistry();
        handle = registry.RegisterWorkspace(PagedId, new WorkspaceDescriptor
        {
            Id = PagedId,
            DisplayName = "数据库",
            DefaultPort = 3306
        }, new NoopWorkspace());
        return registry;
    }

    /// <summary>合成表单:没有变体(不出卡片),只有字段与分页。</summary>
    private static PluginConnectionForm Form(
        IReadOnlyList<ProtocolSettingField> fields,
        IReadOnlyList<PluginFormPage>? pages,
        IReadOnlyDictionary<string, string>? fieldPages) =>
        new(PluginConnectionKind.Workspace,
            HostLabel: null,
            HostPlaceholder: null,
            UsernameLabel: null,
            PasswordLabel: null,
            Fields: fields,
            AllowsAnonymous: true,
            SupportsSshTunnel: false,
            ShowsCredentials: true,
            Pages: pages,
            FieldPages: fieldPages);

    private static async Task<ConnectionProfileViewModel> OpenAsync(PluginProtocolRegistry registry, PluginConnectionForm form)
    {
        var vm = new ConnectionProfileViewModel(protocolRegistry: registry)
        {
            Host = "10.0.0.9",
            PluginFormOverride = _ => form
        };
        await vm.SelectPluginProtocolCommand.Execute(PagedId).FirstAsync();
        return vm;
    }

    private static PluginProtocolFieldViewModel Row(ConnectionProfileViewModel vm, string key) =>
        vm.PluginFields.Single(f => f.Key == key);

    /// <summary>此刻看得见的行,按集合顺序。</summary>
    private static string[] VisibleRows(ConnectionProfileViewModel vm) =>
        [.. vm.PluginFields.Where(f => f.IsRowVisible).Select(f => f.Key)];

    private static PluginPageTabViewModel Tab(ConnectionProfileViewModel vm, string key) =>
        vm.PluginPages.Single(tab => tab.Key == key);

    /// <summary>
    /// <b>带页键的字段只画在那一页:不在「常规」、不在「高级」</b>。
    /// <para>
    /// MySQL 的样子:常规页放字符集,「数据库」页放默认库与 schema,「SSL」页放 SSL 档位,「高级」放超时。
    /// 切页时只露出那一页的行;页签高亮跟着走,离开自定义页时没有一个自定义页签是亮的。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task FieldsWithAPageKey_LiveOnThatPage_NotOnGeneralOrAdvanced()
    {
        PluginProtocolRegistry registry = Registry(out IDisposable handle);
        using (handle)
        {
            PluginConnectionForm form = Form(
                [
                    new() { Key = "charset", Label = "字符集", DefaultValue = "utf8mb4" },
                    new() { Key = "database", Label = "默认数据库" },
                    new() { Key = "schema", Label = "Schema" },
                    new()
                    {
                        Key = "sslMode",
                        Label = "SSL 模式",
                        Kind = ProtocolSettingKind.Choice,
                        DefaultValue = "preferred",
                        Choices = [new("disabled", "禁用"), new("preferred", "优先"), new("required", "必须")]
                    },
                    new() { Key = "timeout", Label = "连接超时", Kind = ProtocolSettingKind.Integer, IsAdvanced = true }
                ],
                [new(DatabasePage, "数据库"), new(SslPage, "SSL")],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["database"] = DatabasePage,
                    ["schema"] = DatabasePage,
                    ["sslMode"] = SslPage
                });
            ConnectionProfileViewModel vm = await OpenAsync(registry, form);

            Assert.AreEqual(DatabasePage, Row(vm, "database").PageKey);
            Assert.AreEqual(SslPage, Row(vm, "sslMode").PageKey);
            Assert.AreEqual(PluginFormPage.GeneralKey, Row(vm, "charset").PageKey);
            Assert.AreEqual(PluginFormPage.AdvancedKey, Row(vm, "timeout").PageKey);

            Assert.IsTrue(vm.IsGeneralSection);
            Assert.AreSequenceEqual(["charset"], VisibleRows(vm), "「常规」页只有没页键、也没标高级的字段。");
            Assert.IsTrue(vm.ShowPluginGeneralFields);
            Assert.IsTrue(vm.ShowPluginPageTabs);
            Assert.IsTrue(vm.PluginPages.All(tab => tab.HasVisibleFields && !tab.IsSelected), "两页都有东西;还没点进去,一个都不亮。");

            vm.SelectPluginPageCommand.Execute(DatabasePage).Subscribe();
            Assert.AreEqual(ConnectionProfileSection.PluginPage, vm.SelectedSection);
            Assert.IsTrue(vm.IsPluginPageSection);
            Assert.IsFalse(vm.IsGeneralSection);
            Assert.AreEqual(DatabasePage, vm.SelectedPluginPageKey);
            Assert.AreSequenceEqual(["database", "schema"], VisibleRows(vm), "「数据库」页只有它自己的两行。");
            Assert.IsTrue(Tab(vm, DatabasePage).IsSelected);
            Assert.IsFalse(Tab(vm, SslPage).IsSelected);

            // 自定义页之间切换:分页没变(都是 PluginPage),行与高亮也得跟着换。
            vm.SelectPluginPageCommand.Execute(SslPage).Subscribe();
            Assert.AreEqual(SslPage, vm.SelectedPluginPageKey);
            Assert.AreSequenceEqual(["sslMode"], VisibleRows(vm));
            Assert.IsFalse(Tab(vm, DatabasePage).IsSelected);
            Assert.IsTrue(Tab(vm, SslPage).IsSelected);

            vm.SelectSectionCommand.Execute(ConnectionProfileSection.Advanced).Subscribe();
            Assert.IsTrue(vm.IsAdvancedSection);
            Assert.IsFalse(vm.IsPluginPageSection);
            Assert.AreSequenceEqual(["timeout"], VisibleRows(vm), "「高级」页只有标了高级、又没页键的字段。");
            Assert.IsTrue(vm.PluginPages.All(tab => !tab.IsSelected), "离开自定义页,自定义页签都不亮。");
            Assert.AreEqual("1", vm.AdvancedBadge);

            vm.SelectSectionCommand.Execute(ConnectionProfileSection.General).Subscribe();
            Assert.AreSequenceEqual(["charset"], VisibleRows(vm));
        }
    }

    /// <summary>
    /// <b>页键优先于 IsAdvanced,而且不算进「高级」的徽标与圆点</b>(决策 D5)。
    /// <para>
    /// Oracle 的「连接身份」原先标着高级;声明进「数据库」页之后它就只在那一页 ——
    /// 「高级」页签上的数字不该把它算进去,改了它的值也不该给「高级」点圆点(自定义页不做徽标与圆点)。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task PageBeatsIsAdvanced_AndIsNotCountedInTheAdvancedBadge()
    {
        PluginProtocolRegistry registry = Registry(out IDisposable handle);
        using (handle)
        {
            PluginConnectionForm form = Form(
                [
                    new() { Key = "database", Label = "默认数据库" },
                    new()
                    {
                        Key = "connectAs",
                        Label = "连接身份",
                        Kind = ProtocolSettingKind.Choice,
                        DefaultValue = "normal",
                        Choices = [new("normal", "普通"), new("sysdba", "SYSDBA")],
                        IsAdvanced = true
                    },
                    new() { Key = "timeout", Label = "连接超时", Kind = ProtocolSettingKind.Integer, DefaultValue = "15", IsAdvanced = true },
                    new() { Key = "poolSize", Label = "连接池", Kind = ProtocolSettingKind.Integer, DefaultValue = "4", IsAdvanced = true }
                ],
                [new(DatabasePage, "数据库")],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["database"] = DatabasePage,
                    ["connectAs"] = DatabasePage
                });
            ConnectionProfileViewModel vm = await OpenAsync(registry, form);

            Assert.AreEqual(DatabasePage, Row(vm, "connectAs").PageKey, "填了页键就进那一页,IsAdvanced 不再看。");
            Assert.IsTrue(Row(vm, "connectAs").IsAdvanced, "前提:它确实标着高级。");
            Assert.AreEqual("2", vm.AdvancedBadge, "「高级」页签只数真在那一页上的两项。");
            Assert.IsTrue(vm.HasAdvancedBadge);
            Assert.IsFalse(vm.ShowPluginGeneralFields, "「常规」页没有插件字段,那一节连标题一起收起。");

            vm.SelectSectionCommand.Execute(ConnectionProfileSection.Advanced).Subscribe();
            Assert.AreSequenceEqual(["timeout", "poolSize"], VisibleRows(vm), "进了自定义页的不在「高级」上重复出现。");

            vm.SelectPluginPageCommand.Execute(DatabasePage).Subscribe();
            Assert.AreSequenceEqual(["database", "connectAs"], VisibleRows(vm));

            Row(vm, "connectAs").Text = "sysdba";
            Assert.IsFalse(vm.IsAdvancedSectionModified, "自定义页上的字段改了值,不给「高级」点圆点。");
            Row(vm, "timeout").Text = "30";
            Assert.IsTrue(vm.IsAdvancedSectionModified, "「高级」页自己的字段改了值,圆点照旧亮。");
        }
    }

    /// <summary>
    /// <b>一页的字段全被显示条件藏掉:页签收起,停在那一页的落回「常规」</b>。
    /// <para>
    /// 关掉「启用 SSL」之后 SSL 页上一行都不剩 —— 留着页签点进去是一片空白,停在那里则右边空着、
    /// 页签条上又没有一个是亮的。重新打开后页签回来,但不会把人自己拽过去。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task TabHiddenWhenAllItsFieldsAreHiddenByVisibleWhen_AndSelectionFallsBackToGeneral()
    {
        PluginProtocolRegistry registry = Registry(out IDisposable handle);
        using (handle)
        {
            PluginConnectionForm form = Form(
                [
                    new() { Key = "useSsl", Label = "启用 SSL", Kind = ProtocolSettingKind.Boolean, DefaultValue = "true" },
                    new() { Key = "database", Label = "默认数据库" },
                    new() { Key = "sslMode", Label = "SSL 模式", VisibleWhen = new("useSsl", "true") },
                    new() { Key = "sslCa", Label = "CA 证书", VisibleWhen = new("useSsl", "true") }
                ],
                [new(DatabasePage, "数据库"), new(SslPage, "SSL")],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["database"] = DatabasePage,
                    ["sslMode"] = SslPage,
                    ["sslCa"] = SslPage
                });
            ConnectionProfileViewModel vm = await OpenAsync(registry, form);
            vm.SelectPluginPageCommand.Execute(SslPage).Subscribe();
            Assert.IsTrue(vm.IsPluginPageSection, "前提:停在「SSL」页上。");
            Assert.AreSequenceEqual(["sslMode", "sslCa"], VisibleRows(vm));

            Row(vm, "useSsl").Toggle = false;

            Assert.IsFalse(Tab(vm, SslPage).HasVisibleFields, "一行都不剩,页签收起。");
            Assert.IsTrue(Tab(vm, DatabasePage).HasVisibleFields, "别的页不受牵连。");
            Assert.IsTrue(vm.ShowPluginPageTabs);
            Assert.IsTrue(vm.IsGeneralSection, "停在被收起的那一页上,落回「常规」。");
            Assert.IsFalse(vm.IsPluginPageSection);
            Assert.IsTrue(vm.PluginPages.All(tab => !tab.IsSelected));
            Assert.AreSequenceEqual(["useSsl"], VisibleRows(vm));

            vm.SelectPluginPageCommand.Execute(SslPage).Subscribe();
            Assert.IsTrue(vm.IsGeneralSection, "收起的页签点不进去。");

            Row(vm, "useSsl").Toggle = true;
            Assert.IsTrue(Tab(vm, SslPage).HasVisibleFields, "条件重新成立,页签回来。");
            Assert.IsTrue(vm.IsGeneralSection, "页签回来不等于把人拽过去。");
        }
    }

    /// <summary>
    /// <b>页键不在分页表里:按没填处理,落回 IsAdvanced 决定的默认位置</b>(决策 D5)。
    /// <para>
    /// 不报错、不吞字段 —— 与显示条件引用不存在的键同一态度:用户总还找得到它。
    /// 没声明分页的表单(没声明分页的描述符经 <c>From()</c> 就是这样)没有自定义页签,整组收起。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task UnknownPageKey_FallsBackToDefaultPlacement()
    {
        ProtocolSettingField plain = new() { Key = "plain", Label = "普通" };
        ProtocolSettingField tuned = new() { Key = "tuned", Label = "调优", IsAdvanced = true };
        ProtocolSettingField placed = new() { Key = "placed", Label = "有页" };
        var fieldPages = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["plain"] = "nosuch",
            ["tuned"] = "nosuch",
            ["placed"] = DatabasePage
        };

        // 分页表里没有 nosuch:按 IsAdvanced 落位。
        PluginConnectionForm form = Form([plain, tuned, placed], [new(DatabasePage, "数据库")], fieldPages);
        Assert.AreEqual(PluginFormPage.GeneralKey, form.PageKeyFor(plain));
        Assert.AreEqual(PluginFormPage.AdvancedKey, form.PageKeyFor(tuned));
        Assert.AreEqual(DatabasePage, form.PageKeyFor(placed));

        // 根本没声明分页:页键一律不认。
        PluginConnectionForm noPages = Form([plain, tuned, placed], null, fieldPages);
        Assert.AreEqual(PluginFormPage.GeneralKey, noPages.PageKeyFor(placed));
        Assert.AreEqual(PluginFormPage.AdvancedKey, noPages.PageKeyFor(tuned));

        // 描述符没声明分页、字段没填页键:两个 From() 都不填分页,行为与今天一样。
        var fromDescriptor = PluginConnectionForm.From(new WorkspaceDescriptor
        {
            Id = PagedId,
            DisplayName = "数据库",
            Fields = [plain, tuned]
        });
        Assert.IsNull(fromDescriptor.Pages);
        Assert.IsNull(fromDescriptor.FieldPages);
        Assert.IsNull(PluginConnectionForm.From(new ProtocolDescriptor { Id = "test.files", DisplayName = "文件" }).Pages);

        PluginProtocolRegistry registry = Registry(out IDisposable handle);
        using (handle)
        {
            ConnectionProfileViewModel vm = await OpenAsync(registry, form);
            Assert.AreEqual(PluginFormPage.GeneralKey, Row(vm, "plain").PageKey);
            Assert.AreEqual(PluginFormPage.AdvancedKey, Row(vm, "tuned").PageKey);
            Assert.AreSequenceEqual(["plain"], VisibleRows(vm), "不认识的页键落回「常规」,照样看得见。");
            Assert.AreEqual("1", vm.AdvancedBadge, "标了高级的那一个照旧算进「高级」。");
            Assert.AreSequenceEqual([DatabasePage], [.. vm.PluginPages.Select(tab => tab.Key)]);

            ConnectionProfileViewModel plainVm = await OpenAsync(registry, Form([plain, tuned], null, null));
            Assert.IsEmpty(plainVm.PluginPages, "没声明分页就没有自定义页签。");
            Assert.IsFalse(plainVm.ShowPluginPageTabs, "整组收起,「高级」不挪位置。");
            Assert.AreSequenceEqual(["plain"], VisibleRows(plainVm));
            Assert.AreEqual("1", plainVm.AdvancedBadge);
        }
    }

    /// <summary>
    /// <b>页签顺序 = 分页的声明顺序;页里的行 = 字段的声明顺序</b>。
    /// <para>
    /// 与字段先后无关(字段表里「数据库」的字段排在前面,页签照样是「SSL」在前)。
    /// 「常规」「高级」是宿主自己的页,插件声明了同键的页不再多出一个页签;同一个键声明两次只取第一个 ——
    /// 两个同键的页签,选中一个会亮两个。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task OrderFollowsDeclaration()
    {
        PluginProtocolRegistry registry = Registry(out IDisposable handle);
        using (handle)
        {
            PluginConnectionForm form = Form(
                [
                    new() { Key = "database", Label = "默认数据库" },
                    new() { Key = "sslCert", Label = "客户端证书" },
                    new() { Key = "schema", Label = "Schema" },
                    new() { Key = "sslMode", Label = "SSL 模式" }
                ],
                [new(SslPage, "SSL"), new(PluginFormPage.AdvancedKey, "高级?"), new(DatabasePage, "数据库"), new(SslPage, "SSL 2")],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["database"] = DatabasePage,
                    ["schema"] = DatabasePage,
                    ["sslCert"] = SslPage,
                    ["sslMode"] = SslPage
                });
            ConnectionProfileViewModel vm = await OpenAsync(registry, form);

            Assert.AreSequenceEqual([SslPage, DatabasePage], [.. vm.PluginPages.Select(tab => tab.Key)], "页签按分页的声明顺序;宿主自己的键与重复的键不多出页签。");
            Assert.AreSequenceEqual(["SSL", "数据库"], [.. vm.PluginPages.Select(tab => tab.Label)]);

            vm.SelectPluginPageCommand.Execute(SslPage).Subscribe();
            Assert.AreSequenceEqual(["sslCert", "sslMode"], VisibleRows(vm), "页里的行按字段的声明顺序。");
            vm.SelectPluginPageCommand.Execute(DatabasePage).Subscribe();
            Assert.AreSequenceEqual(["database", "schema"], VisibleRows(vm));
            Assert.ContainsSingle(tab => tab.IsSelected, vm.PluginPages, "任何时候最多亮一个。");
        }
    }

    /// <summary>
    /// <b>SDK 的分页契约经 <c>From()</c> 进内部模型:描述符的 Pages 成页签,字段的 Page 定落在哪一页</b>。
    /// <para>
    /// 不经 <c>PluginFormOverride</c>,走真实装载(注册表 → <c>PluginConnectionForm.From</c> → 视图模型)。
    /// 文件协议没有分页列表:字段上填了 Page 也被忽略,照旧按 IsAdvanced 落位。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task From_WorkspaceDescriptor_MapsPagesAndFieldPages()
    {
        var descriptor = new WorkspaceDescriptor
        {
            Id = PagedId,
            DisplayName = "数据库",
            DefaultPort = 3306,
            Pages = [new ProtocolSettingPage(DatabasePage, "数据库"), new ProtocolSettingPage(SslPage, "SSL")],
            Fields =
            [
                new() { Key = "charset", Label = "字符集" },
                new() { Key = "database", Label = "默认数据库", Page = DatabasePage },
                new() { Key = "sslMode", Label = "SSL 模式", Page = SslPage, IsAdvanced = true },
                new() { Key = "timeout", Label = "连接超时", Kind = ProtocolSettingKind.Integer, IsAdvanced = true }
            ]
        };

        var form = PluginConnectionForm.From(descriptor);
        Assert.AreSequenceEqual(
            [new PluginFormPage(DatabasePage, "数据库"), new PluginFormPage(SslPage, "SSL")], [.. form.Pages!], "分页按声明顺序,键与文案原样映射。");
        Assert.HasCount(2, form.FieldPages!, "只有填了 Page 的字段进字典。");
        Assert.AreEqual(DatabasePage, form.FieldPages!["database"]);
        Assert.AreEqual(SslPage, form.FieldPages!["sslMode"]);

        var files = PluginConnectionForm.From(new ProtocolDescriptor
        {
            Id = "test.files",
            DisplayName = "文件",
            Fields = [new() { Key = "database", Label = "默认数据库", Page = DatabasePage }]
        });
        Assert.IsNull(files.Pages, "文件协议没有分页列表。");
        Assert.IsNull(files.FieldPages, "文件协议字段上的 Page 被忽略。");
        Assert.AreEqual(PluginFormPage.GeneralKey, files.PageKeyFor(files.Fields[0]));

        var registry = new PluginProtocolRegistry();
        using (registry.RegisterWorkspace(PagedId, descriptor, new NoopWorkspace()))
        {
            var vm = new ConnectionProfileViewModel(protocolRegistry: registry) { Host = "10.0.0.9" };
            await vm.SelectPluginProtocolCommand.Execute(PagedId).FirstAsync();

            Assert.AreSequenceEqual([DatabasePage, SslPage], [.. vm.PluginPages.Select(tab => tab.Key)], "描述符声明了两页,出两个自定义页签。");
            Assert.AreSequenceEqual(["数据库", "SSL"], [.. vm.PluginPages.Select(tab => tab.Label)]);
            Assert.AreEqual(DatabasePage, Row(vm, "database").PageKey);
            Assert.AreEqual(SslPage, Row(vm, "sslMode").PageKey, "页键优先于 IsAdvanced。");
            Assert.AreEqual(PluginFormPage.GeneralKey, Row(vm, "charset").PageKey);
            Assert.AreEqual(PluginFormPage.AdvancedKey, Row(vm, "timeout").PageKey);
            Assert.AreSequenceEqual(["charset"], VisibleRows(vm), "「常规」页只剩没页键、没标高级的字段。");
            Assert.AreEqual("1", vm.AdvancedBadge, "进了「SSL」页的那一项不算进「高级」。");

            vm.SelectPluginPageCommand.Execute(DatabasePage).Subscribe();
            Assert.AreSequenceEqual(["database"], VisibleRows(vm));
            vm.SelectPluginPageCommand.Execute(SslPage).Subscribe();
            Assert.AreSequenceEqual(["sslMode"], VisibleRows(vm));
        }
    }
}
