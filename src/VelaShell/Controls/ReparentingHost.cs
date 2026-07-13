using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;

namespace VelaShell.Controls;

/// <summary>
/// Hosts a single shared <see cref="Control" /> (the live terminal surface) that must appear in
/// exactly one place at a time. Avalonia forbids a control from having two visual parents, so when
/// Dock realizes the same document in a second presenter (during a split, tab drag, or tear-off to
/// a floating window), the shared control would throw "already has a visual parent".
/// This host solves that by "stealing" the target: whenever a host attaches (or its target
/// changes) it first detaches the target from its previous parent, then adopts it. Because only
/// the currently visible view is attached, the target always ends up parented exactly once.
/// </summary>
public sealed class ReparentingHost : Decorator
{
    /// <summary>标识 <see cref="Target" /> 样式化属性。</summary>
    public static readonly StyledProperty<Control?> TargetProperty =
        AvaloniaProperty.Register<ReparentingHost, Control?>(nameof(Target));

    static ReparentingHost()
    {
        TargetProperty.Changed.AddClassHandler<ReparentingHost>((host, _) => host.Reattach());
    }

    /// <summary>
    /// 需要被独占托管的共享控件(如实时终端画面)。设置后本宿主会先将其从原父级
    /// 分离,再收养为自身子级,确保该控件在可视树中始终只有一个父级。
    /// </summary>
    public Control? Target
    {
        get => GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    /// <summary>挂载到可视树时将目标控件重新收养为子级。</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Reattach();
    }

    /// <summary>从可视树分离时释放对目标控件的收养,避免重复父级。</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (ReferenceEquals(Child, Target))
        {
            Child = null;
        }
    }

    private void Reattach()
    {
        Control? target = Target;
        if (target is null)
        {
            Child = null;
            return;
        }
        if (ReferenceEquals(Child, target))
        {
            return;
        }
        DetachFromCurrentParent(target);
        Child = target;
    }

    private static void DetachFromCurrentParent(Control control)
    {
        switch (control.Parent)
        {
            case Decorator decorator when ReferenceEquals(decorator.Child, control):
                decorator.Child = null;
                break;
            case ContentControl contentControl when ReferenceEquals(contentControl.Content, control):
                contentControl.Content = null;
                break;
            case ContentPresenter presenter when ReferenceEquals(presenter.Content, control):
                presenter.Content = null;
                break;
            case Panel panel:
                panel.Children.Remove(control);
                break;
        }
    }
}
