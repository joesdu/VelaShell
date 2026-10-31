using System.Collections.ObjectModel;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.PluginSdk.Protocols;

namespace VelaShell.ViewModels;

/// <summary>
/// 「选类型」这一步(卡片 → 下一步 → 表单;上一步 / 更换)。
/// <para>
/// 一个插件页签承载一族相近的连接类型时(数据库页签:MySQL / PostgreSQL / SQLite……),
/// 「是哪一种」决定了端口、主机那一栏装什么、要不要凭据 —— 它是整张表单的前提,
/// 而不是表单里排在中间的一个下拉。所以新建连接先在一屏卡片里选定类型,再进表单;
/// 表单里那一行下拉不再出现,由表头「类型:X」替代。
/// </para>
/// <para>
/// 出不出卡片由 <see cref="PluginConnectionForm.HasTypePicker" /> 决定(宿主规则,不是 SDK 开关);
/// 不出卡片的协议(SSH、Redis、S3……)全程停在 <see cref="ConnectionProfileStep.Form" />,
/// 与没有这一步时完全一样。
/// </para>
/// <para>
/// 转移(T1–T7)一律发生在 UI 线程:命令里,或 <c>ConfigureAwait(true)</c> 的续体里。
/// <see cref="TypeCards" /> 是绑在界面上的集合,只能在那里改。
/// </para>
/// </summary>
public partial class ConnectionProfileViewModel
{
    /// <summary>
    /// 这一页签的类型是否已经由用户确认过(点过「下一步」)。
    /// 换协议时复位;决定装载完成 / 重复点同一页签时回到卡片还是留在表单。
    /// </summary>
    private bool _typeConfirmed;

    /// <summary>是否在编辑一条已有配置(构造时传了 existing)。编辑态不出卡片,改类型走表头的「更换」。</summary>
    public bool IsEditing { get; }

    /// <summary>右栏当前的步骤。</summary>
    public ConnectionProfileStep Step
    {
        get;
        private set
        {
            if (field == value)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref field, value);
            RaiseTypeStepDependents();
        }
    }

    /// <summary>当前是否在选类型的那一屏。</summary>
    public bool IsPickingType => Step == ConnectionProfileStep.PickType;

    /// <summary>
    /// 卡片,一张对应变体字段的一个候选项(顺序同 <c>Choices</c>)。
    /// 当前页签不出卡片时为空。
    /// </summary>
    public ObservableCollection<PluginTypeCardViewModel> TypeCards { get; } = [];

    /// <summary>
    /// 卡片上当前选中的值。单击只改它,点「下一步」才写进变体字段。
    /// </summary>
    public string? SelectedTypeValue
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>表头「类型:X」里的 X:变体字段当前值在候选项里的名字;不出卡片时为 null。</summary>
    public string? CurrentTypeLabel
    {
        get
        {
            if (TypePickerField is not { } picker)
            {
                return null;
            }
            string? current = PluginFieldValue(picker.Key);
            foreach (ProtocolSettingChoice choice in picker.Choices)
            {
                if (string.Equals(choice.Value, current, StringComparison.Ordinal))
                {
                    return choice.Label;
                }
            }
            return null;
        }
    }

    /// <summary>表单顶部是否显示「类型:X」那一行(出卡片的页签,且已经走过了卡片那一屏)。</summary>
    public bool ShowTypeHeader => TypePickerField is not null && !IsPickingType;

    /// <summary>表头上是否带「更换」:只在编辑态 —— 新建时回去改类型走页脚的「上一步」。</summary>
    public bool ShowChangeTypeLink => ShowTypeHeader && IsEditing;

    /// <summary>页脚是否显示「上一步」:只在新建、且已经从卡片走到了表单。</summary>
    public bool ShowBackButton => TypePickerField is not null && !IsPickingType && !IsEditing;

    /// <summary>单击卡片(T2):只选不走。</summary>
    public ReactiveCommand<string, RxVoid> SelectTypeCardCommand { get; }

    /// <summary>双击卡片(T7):选中并直接进表单,等于单击 + 「下一步」。</summary>
    public ReactiveCommand<string, RxVoid> ConfirmTypeCardCommand { get; }

    /// <summary>「下一步」(T3):把选中的类型写进变体字段,进表单。</summary>
    public ReactiveCommand<RxVoid, RxVoid> NextCommand { get; }

    /// <summary>「上一步」(T4,新建):回到卡片,不清任何已填的值。</summary>
    public ReactiveCommand<RxVoid, RxVoid> BackCommand { get; }

    /// <summary>表头「更换」(T4,编辑态):回到卡片,不清任何已填的值。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ChangeTypeCommand { get; }

    /// <summary>当前表单里能画成卡片的那个变体字段;不出卡片时为 null。</summary>
    private ProtocolSettingField? TypePickerField => _pluginBaseForm?.TypePickerField;

    /// <summary>
    /// 这一行是不是被卡片替代掉的变体字段。只要出卡片,它在表单里就**永不**显示
    /// (表头「类型:X」替代它),两个入口改同一个值只会让人困惑哪个算数。
    /// </summary>
    private bool IsTypePickerRow(PluginProtocolFieldViewModel row) =>
        TypePickerField is { } picker && string.Equals(row.Key, picker.Key, StringComparison.Ordinal);

    /// <summary>
    /// T1:表单装载完成(或重复点了同一个页签)之后定步骤。
    /// <para>
    /// 出卡片、不是编辑态、类型还没确认过 → 卡片,预选变体字段当前那一档(新建就是字段默认值),
    /// 「下一步」立即可用;否则 → 表单。卡片本身只要页签出卡片就重建 ——
    /// 编辑态点「更换」要用到它们。
    /// </para>
    /// <para>只能从已经确认不是陈旧续体的路径上调(陈旧续体不碰步骤)。</para>
    /// </summary>
    private void EnterStepForLoadedForm()
    {
        RebuildTypeCards();
        Step = TypePickerField is not null && !IsEditing && !_typeConfirmed
            ? ConnectionProfileStep.PickType
            : ConnectionProfileStep.Form;
        // 步骤可能没变而"出不出卡片"变了(从 Redis 换到数据库,两边都是 Form):依赖项要补发。
        RaiseTypeStepDependents();
    }

    /// <summary>
    /// T5:换了协议。上一个页签的卡片、选中值、确认状态都不能留到下一个页签上。
    /// <para>
    /// 落在 <see cref="ConnectionProfileStep.Form" />:它是没有卡片的协议的中性态,
    /// 新页签若出卡片,由装载完成时的 T1 再进卡片。
    /// </para>
    /// </summary>
    private void ResetTypeStep()
    {
        _typeConfirmed = false;
        TypeCards.Clear();
        SelectedTypeValue = null;
        Step = ConnectionProfileStep.Form;
        RaiseTypeStepDependents();
    }

    /// <summary>按当前表单的变体字段重建卡片,并选中字段当前那一档。</summary>
    private void RebuildTypeCards()
    {
        TypeCards.Clear();
        if (TypePickerField is not { } picker)
        {
            SelectedTypeValue = null;
            return;
        }
        // 卡片图标:同 Value 变体自带的那一个 → 连接类型自报的那一个 → null(界面画通用插件图标)。
        PluginSdk.PluginIcon? fallback = _pluginBaseForm?.Icon;
        foreach (ProtocolSettingChoice choice in picker.Choices)
        {
            TypeCards.Add(new(choice.Value, choice.Label, VariantIcon(choice.Value) ?? fallback));
        }
        MarkSelectedType(PluginFieldValue(picker.Key));
    }

    /// <summary>
    /// 变体表里 <c>Value</c> 与 <paramref name="value" /> 相同(序数比较)那一项的图标;
    /// 没有这一项、或它没给图标时为 null。
    /// </summary>
    private PluginSdk.PluginIcon? VariantIcon(string value)
    {
        if (_pluginBaseForm?.Variants is not { Count: > 0 } variants)
        {
            return null;
        }
        for (int i = 0; i < variants.Count; i++)
        {
            if (string.Equals(variants[i].Value, value, StringComparison.Ordinal))
            {
                return variants[i].Icon;
            }
        }
        return null;
    }

    /// <summary>改选中值,并让各卡片的高亮跟着走。</summary>
    private void MarkSelectedType(string? value)
    {
        SelectedTypeValue = value;
        foreach (PluginTypeCardViewModel card in TypeCards)
        {
            card.IsSelected = string.Equals(card.Value, value, StringComparison.Ordinal);
        }
    }

    /// <summary>T2:单击卡片,只选不走。不在卡片那一屏、或不是这一组卡片里的值时什么也不做。</summary>
    private void SelectTypeCard(string value)
    {
        if (!IsPickingType || !TypeCards.Any(card => string.Equals(card.Value, value, StringComparison.Ordinal)))
        {
            return;
        }
        MarkSelectedType(value);
    }

    /// <summary>T7:双击卡片 = 单击 + 下一步。</summary>
    private void ConfirmTypeCard(string value)
    {
        if (!IsPickingType || IsBusy)
        {
            return;
        }
        SelectTypeCard(value);
        ConfirmTypeStep();
    }

    /// <summary>
    /// T3:「下一步」。把选中的类型写进变体字段那一行,进表单。
    /// <para>
    /// 写进那一行会经由 <c>OnPluginFieldChanged</c> 让字段显隐与变体形态跟着变。
    /// <b>但值没变时不会有那一路</b>,所以新建、且这一页签还没确认过类型时,值没变也显式按变体刷新一次
    /// (主机标签、凭据、端口):选 PostgreSQL → 切到 SSH → 点回数据库,下拉还是 PostgreSQL,
    /// 端口却已经回到页签默认的 3306 —— 只靠字段变化就停在 3306 上了。
    /// (这一条路现在点回页签时就把端口还回了切走之前的值,见 <c>SelectPluginProtocolAsync</c>;
    /// 这里留作兜底:第一次点进页签时端口是页签默认值,默认那一档的变体端口可以与它不同。)
    /// 已确认过的类型(「上一步」回来、编辑态「更换」)没换就不刷新:那时的端口是用户的,不是窗口留下的。
    /// 端口照常只在「还是某个协议的默认值」时跟随,用户手填的 13306 不动。
    /// </para>
    /// <para>其它已填的值(主机、用户名、各字段)一律不动。</para>
    /// </summary>
    private void ConfirmTypeStep()
    {
        if (!IsPickingType || SelectedTypeValue is not { } value || IsBusy)
        {
            return;
        }
        if (TypePickerField is { } picker
            && PluginFields.FirstOrDefault(row => string.Equals(row.Key, picker.Key, StringComparison.Ordinal)) is { } row
            && !string.Equals(row.Text, value, StringComparison.Ordinal))
        {
            row.Text = value;
        }
        // 只在「新建、且这一页签还没确认过类型」时补这一下 —— 那个窗口(PG → SSH → 点回来)只出现在这里:
        // 换协议会清掉确认,「上一步」不清,编辑态从不经装载进卡片。
        // 编辑态点「更换」看一眼再「下一步」、或新建时手填了 3306 再「上一步 / 下一步」,类型没换,
        // 无条件刷新就会把已存 / 手填的 3306 当成"还是默认值"改成 5432。
        // 判断必须在 `_typeConfirmed = true` 之前。值真变了的那一路照旧经 OnPluginFieldChanged 跟随端口。
        if (!IsEditing && !_typeConfirmed)
        {
            ApplyPluginVariant(followPort: true);
        }
        _typeConfirmed = true;
        Step = ConnectionProfileStep.Form;
        SelectedSection = ConnectionProfileSection.General;
        RaiseTypeStepDependents();
    }

    /// <summary>
    /// T4:回到卡片(「上一步」与「更换」共用)。预选变体字段当前那一档;
    /// 不清任何已填的值、不碰端口 —— 回来看一眼再点「下一步」,表单应当原样还在。
    /// </summary>
    private void ReturnToTypeStep()
    {
        if (TypePickerField is not { } picker || IsPickingType || IsBusy)
        {
            return;
        }
        if (TypeCards.Count == 0)
        {
            RebuildTypeCards();
        }
        MarkSelectedType(PluginFieldValue(picker.Key));
        Step = ConnectionProfileStep.PickType;
    }

    /// <summary>
    /// 步骤、"出不出卡片"、表头文字任一变化时补发的整组通知。
    /// 这几个都是计算属性,命令的 canExecute 也挂在它们上面,漏发就是按钮状态不跟着走。
    /// </summary>
    private void RaiseTypeStepDependents()
    {
        this.RaisePropertyChanged(nameof(IsPickingType));
        this.RaisePropertyChanged(nameof(ShowTypeHeader));
        this.RaisePropertyChanged(nameof(ShowChangeTypeLink));
        this.RaisePropertyChanged(nameof(ShowBackButton));
        this.RaisePropertyChanged(nameof(CurrentTypeLabel));
    }
}
