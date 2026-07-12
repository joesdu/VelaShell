namespace VelaShell.Docking.Model;

/// <summary>布局树节点基类:标签组(叶)或分栏(枝)。</summary>
public abstract class DockNode : DockElement
{
    private double _proportion = double.NaN;

    /// <summary>
    /// 在父分栏中占的比例权重;NaN 表示与兄弟均分(渲染层视作 1 星)。
    /// 拖动分割条后由控件层回写实际比例。
    /// </summary>
    public double Proportion
    {
        get => _proportion;
        set => SetField(ref _proportion, value);
    }

    /// <summary>所属分栏;根节点为 null。由 <see cref="DockSplit" /> 集合维护。</summary>
    public DockSplit? Parent { get; internal set; }
}
