using System.Text.RegularExpressions;
using VelaShell.Core.Ssh;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 「生成密钥」下拉的档位表与 axaml 里那串 <c>ComboBoxItem</c> 必须逐项对齐。
/// </summary>
/// <remarks>
/// 这条缝的特别之处在于<b>错了也不报错</b>:两边只靠 <c>SelectedIndex</c> 这个整数对齐,
/// 在 axaml 里插一项、删一项、或者调个顺序,编译照过、测试照绿、界面照常渲染 ——
/// 用户选「Ed25519」拿到的却是一把 RSA。列表里如实写着 RSA,可没人会去怀疑自己刚选的那一项。
/// 所以这里直接去读 axaml 文本,把顺序与键名逐项比。
/// </remarks>
[TestClass]
public partial class SshKeyChoiceCatalogTests
{
    /// <summary>从算法 ComboBox 起到它闭合为止的那一段。</summary>
    [GeneratedRegex(@"SshKeys\.SelectedAlgorithmIndex.*?</ComboBox>", RegexOptions.Singleline)]
    private static partial Regex AlgorithmComboBox { get; }

    [GeneratedRegex(@"<ComboBoxItem\s+Content=""\{loc:Localize\s+([A-Za-z0-9_]+)\s*\}""\s*/>")]
    private static partial Regex ItemKey { get; }

    [TestMethod]
    public void AxamlComboBoxItems_MatchAlgorithmChoices_InOrder()
    {
        string axaml = File.ReadAllText(KeyManagementPagePath());
        Match combo = AlgorithmComboBox.Match(axaml);
        Assert.IsTrue(combo.Success,
                      "在 KeyManagementPage.axaml 里找不到绑定 SshKeys.SelectedAlgorithmIndex 的 ComboBox —— " +
                      "要么下拉被挪走了,要么绑定改名了,这条测试已经失去对象。");

        string[] keysInXaml = [.. ItemKey.Matches(combo.Value).Select(m => m.Groups[1].Value)];
        string[] keysInCatalog = [.. SshKeyManagerViewModel.AlgorithmChoices.Select(c => c.LocalizationKey)];

        Assert.AreSequenceEqual(keysInCatalog, keysInXaml, "下拉条目与 AlgorithmChoices 对不上(顺序也算)。唯一事实来源是 " +
                                  "SshKeyManagerViewModel.AlgorithmChoices,要增删档位先改那里,再照着改 axaml。\n" +
                                  $"  axaml   : {string.Join(", ", keysInXaml)}\n" +
                                  $"  catalog : {string.Join(", ", keysInCatalog)}");
    }

    /// <summary>第 0 项是默认值,必须是 Ed25519 —— 用户不动下拉直接按「生成」时拿到的就是它。</summary>
    [TestMethod]
    public void FirstChoice_IsEd25519()
    {
        SshKeyChoice first = SshKeyManagerViewModel.AlgorithmChoices[0];
        Assert.AreEqual(SshKeyAlgorithm.Ed25519, first.Algorithm);
        Assert.AreEqual(0, new SshKeyManagerViewModel().SelectedAlgorithmIndex, "下拉默认选中第 0 项");
    }

    /// <summary>建议文件名不许重复 —— 重了的话两档会去抢同一个名字,第二档永远带 _2 后缀。</summary>
    [TestMethod]
    public void BaseNames_AreDistinct()
    {
        string[] names = [.. SshKeyManagerViewModel.AlgorithmChoices.Select(c => c.BaseName)];
        Assert.HasCount(names.Length, names.Distinct(StringComparer.Ordinal),
                        $"建议文件名有重复:{string.Join(", ", names)}");
    }

    private static string KeyManagementPagePath()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Directory.GetParent(dir)?.FullName)
        {
            string candidate = Path.Combine(dir, "src", "VelaShell", "Views", "Settings", "KeyManagementPage.axaml");
            if (File.Exists(Path.Combine(dir, "VelaShell.slnx")) && File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new InvalidOperationException("未能从测试输出目录向上定位到 KeyManagementPage.axaml。");
    }
}
