using System.Collections.ObjectModel;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;

namespace VelaShell.ViewModels;

/// <summary>「生成密钥」下拉里的一档。</summary>
/// <param name="Algorithm">密钥算法。</param>
/// <param name="Bits">RSA 位数或 ECDSA 曲线;0 表示按算法取默认值。</param>
/// <param name="BaseName">生成时的建议文件名(重名会自动加 _2、_3…)。</param>
/// <param name="LocalizationKey">下拉项文案在 Strings.resx 里的键。</param>
public sealed record SshKeyChoice(SshKeyAlgorithm Algorithm, int Bits, string BaseName, string LocalizationKey);

/// <summary>设置 - 密钥管理页(设计 UBP59):枚举/搜索/导入/生成/删除 ~/.ssh 密钥。</summary>
public class SshKeyManagerViewModel : ReactiveObject
{
    /// <summary>
    /// 「生成密钥」旁边那个下拉的全部选项,<b>顺序即下拉顺序</b>,第 0 项是默认值。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这里是唯一事实来源。</b>`KeyManagementPage.axaml` 里那串 <c>ComboBoxItem</c> 只是把
    /// <see cref="SshKeyChoice.LocalizationKey" /> 按同样顺序摆出来,靠 <c>SelectedIndex</c> 与本表对齐 ——
    /// 两边一旦错位,用户选 "Ed25519" 会拿到一把 RSA,而且<b>界面上看不出任何异常</b>
    /// (列表里如实显示 RSA,但没人会怀疑自己选错了)。`SshKeyChoiceCatalogTests` 直接去读
    /// axaml,逐项比对键名与顺序,把这条缝钉死。
    /// </para>
    /// <para>
    /// 位数不给选:RSA 只给 4096(4096 能用的地方 2048 一定能用,反过来不成立,列出来只是个坑),
    /// ECDSA 的三条曲线则各自成一档 —— SSH 只认这三条,它们的算法名本就是分开的。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<SshKeyChoice> AlgorithmChoices { get; } =
    [
        new(SshKeyAlgorithm.Ed25519, 0, "velashell_ed25519", "SetKeys_AlgoEd25519"),
        new(SshKeyAlgorithm.Ecdsa, 256, "velashell_ecdsa256", "SetKeys_AlgoEcdsa256"),
        new(SshKeyAlgorithm.Ecdsa, 384, "velashell_ecdsa384", "SetKeys_AlgoEcdsa384"),
        new(SshKeyAlgorithm.Ecdsa, 521, "velashell_ecdsa521", "SetKeys_AlgoEcdsa521"),
        new(SshKeyAlgorithm.Rsa, 4096, "velashell_rsa", "SetKeys_AlgoRsa4096")
    ];

    private readonly ISshKeyService? _keyService;

    /// <summary>构造密钥管理视图模型,注入密钥服务并初始化集合与命令。</summary>
    public SshKeyManagerViewModel(ISshKeyService? keyService = null)
    {
        _keyService = keyService;
        Keys = [];
        FilteredKeys = [];
        KeyNames = [];
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
        GenerateCommand = ReactiveCommand.CreateFromTask(GenerateAsync);
        this.WhenAnyValue(x => x.SearchQuery).Subscribe(_ => ApplyFilter());
    }

    /// <summary>已枚举到的全部 ~/.ssh 密钥。</summary>
    public ObservableCollection<SshKeyInfo> Keys { get; }

    /// <summary>按搜索条件过滤后的密钥,供列表展示。</summary>
    public ObservableCollection<SshKeyInfo> FilteredKeys { get; }

    /// <summary>密钥名称列表,供“默认认证密钥”下拉。</summary>
    public ObservableCollection<string> KeyNames { get; }

    /// <summary>密钥搜索关键字,变更时触发过滤。</summary>
    public string SearchQuery
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>操作结果状态提示文案。</summary>
    public string StatusMessage
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>
    /// 「生成密钥」用哪一档,是 <see cref="AlgorithmChoices" /> 的下标;越界时按默认值(第 0 项)处理。
    /// </summary>
    public int SelectedAlgorithmIndex
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>是否正在执行异步密钥操作(刷新/生成等)。</summary>
    public bool IsBusy
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>重新枚举密钥列表的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> RefreshCommand { get; }

    // 删除**刻意不提供命令**:直接绑命令就没有确认这一步了(它以前正是这么绑的)。
    // 走 DeleteAsync,由密钥管理页先弹确认再调用。

    /// <summary>按 <see cref="SelectedAlgorithmIndex" /> 选中的那一档生成新密钥的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> GenerateCommand { get; }

    /// <summary>从密钥服务枚举密钥并刷新列表与下拉名单。</summary>
    public async Task RefreshAsync()
    {
        if (_keyService is null)
        {
            return;
        }
        IsBusy = true;
        try
        {
            List<SshKeyInfo> keys = await _keyService.ListKeysAsync();
            Keys.Clear();
            foreach (SshKeyInfo key in keys)
            {
                Keys.Add(key);
            }

            // 名单没变就不动 KeyNames:它是“默认认证密钥”下拉的 ItemsSource,
            // Clear 的瞬间 ComboBox 会把选中项清空并经 TwoWay 把 null 写回设置模型,
            // 已选择的默认密钥会被无谓抹掉。
            if (!keys.Select(k => k.Name).SequenceEqual(KeyNames))
            {
                KeyNames.Clear();
                foreach (SshKeyInfo key in keys)
                {
                    KeyNames.Add(key.Name);
                }
            }
            ApplyFilter();
        }
        catch (Exception ex)
        {
            StatusMessage = Strings.Format("Msg_ReadKeysFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>从指定路径导入密钥并刷新列表。</summary>
    public async Task ImportAsync(string sourcePath)
    {
        if (_keyService is null)
        {
            return;
        }
        try
        {
            SshKeyInfo? imported = await _keyService.ImportKeyAsync(sourcePath);
            StatusMessage = imported is null ? Strings.Get("Msg_KeyAlreadyExists") : Strings.Format("Msg_KeyImported", imported.Name);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = Strings.Format("Msg_ImportFailed", ex.Message);
        }
    }

    private async Task GenerateAsync()
    {
        if (_keyService is null)
        {
            return;
        }
        IsBusy = true;
        try
        {
            // 下拉越界按默认档处理 —— 空列表时 ComboBox 的 SelectedIndex 是 -1,
            // 那不该让「生成」直接崩掉。
            SshKeyChoice choice = SelectedAlgorithmIndex >= 0 && SelectedAlgorithmIndex < AlgorithmChoices.Count
                                      ? AlgorithmChoices[SelectedAlgorithmIndex]
                                      : AlgorithmChoices[0];

            // 自动挑选未占用的名称 velashell_ed25519[, _2, _3…]。名字带算法,是因为 ~/.ssh 里
            // 同时存着好几把是常态(`ssh-keygen` 自己也这么命名:id_rsa / id_ed25519)。
            // 注:以前这里恒为 velashell_rsa —— 老用户 ~/.ssh 下那把不会被动到,照常列出、照常可用。
            string name = choice.BaseName;
            for (int i = 2; Keys.Any(k => k.Name == name); i++)
            {
                name = $"{choice.BaseName}_{i}";
            }
            SshKeyInfo generated = await _keyService.GenerateKeyAsync(name, choice.Algorithm, choice.Bits);
            StatusMessage = Strings.Format("Msg_KeyGenerated", generated.Name, generated.Type);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = Strings.Format("Msg_GenerateFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 删除一把密钥并刷新列表。<b>确认在调用方(密钥管理页)做</b> —— 它拿得到窗口,
    /// 也才能把将要删掉的实际路径摆给用户看。
    /// </summary>
    public async Task DeleteAsync(SshKeyInfo key)
    {
        if (_keyService is null)
        {
            return;
        }
        try
        {
            await _keyService.DeleteKeyAsync(key.Name);
            StatusMessage = Strings.Format("Msg_KeyDeleted", key.Name);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = Strings.Format("Msg_DeleteFailed", ex.Message);
        }
    }

    private void ApplyFilter()
    {
        FilteredKeys.Clear();
        string query = SearchQuery.Trim();
        foreach (SshKeyInfo key in Keys)
        {
            if (query.Length == 0 || key.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || key.Type.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                FilteredKeys.Add(key);
            }
        }
    }
}
