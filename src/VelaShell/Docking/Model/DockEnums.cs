namespace VelaShell.Docking.Model;

/// <summary>分栏方向:Horizontal = 子节点左右排列,Vertical = 上下排列。</summary>
public enum DockOrientation
{
    Horizontal,
    Vertical
}

/// <summary>标签条在组内的停靠位置(右键菜单“标签位置”,与原 Dock 的 Top/Left/Right 对应)。</summary>
public enum DockTabsPosition
{
    Top,
    Left,
    Right
}

/// <summary>拖放目标区:中心 = 并入该组,四边 = 在该组对应侧拆分。</summary>
public enum DockPosition
{
    Center,
    Left,
    Top,
    Right,
    Bottom
}
