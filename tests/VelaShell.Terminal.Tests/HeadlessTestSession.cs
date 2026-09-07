using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// 全程序集共用的 headless 会话。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么是每程序集一个,而不是每测试类一个。</b>本项目的 12 个 headless UI 测试类原先
/// 各自 <c>StartNew</c> + <c>Dispose</c> 一个会话,拆除时偶发在 <c>[ClassCleanup]</c> 里抛
/// <see cref="NullReferenceException" />。改成全程序集一个之后,建立与拆除各只发生一次,
/// 命中率大幅下降,顺带也省掉了 11 次 Avalonia 应用启动。各测试类保留 <c>_session</c> 这个
/// 名字(指向本类),故用法不变。
/// </para>
/// <para>
/// <b>但那条 NRE 不是"多个会话互相拆台"。</b>真正的来源在 Avalonia 12.1.2 的
/// <c>HeadlessUnitTestSession.StartNew</c>:它把 <c>Task.Run</c> 的返回值经闭包变量交给会话
/// 构造器(源码里那句 <c>task!</c>),写入方是调用线程、读取方是工作线程,中间没有任何同步。
/// 工作线程读到 null 时,<c>_dispatchTask</c> 就永久为 null,而它只在 <c>Dispose()</c> 的
/// <c>_dispatchTask.Wait()</c> 处才炸 —— 所有用例照常通过,只有清理红一条,重跑即绿。
/// 单会话下照样会中(CI job 101499021347 就是本类的 <c>[AssemblyCleanup]</c> 中的招),
/// 所以 <see cref="Cleanup" /> 必须吞掉这条 NRE。上游 master 至今未修。
/// </para>
/// <para>
/// 注意:headless UI 测试共用同一条 UI 线程,窗口用完必须关,异步准备要放在 Dispatch 体内
/// (见各测试类)。这条纪律与本会话是否共享无关,但共享之后一处泄漏会影响全程序集,更要守住。
/// </para>
/// </remarks>
[TestClass]
public class HeadlessTestSession
{
    private static HeadlessUnitTestSession? _shared;

    /// <summary>共用会话;在 <see cref="Init" /> 之后、<see cref="Cleanup" /> 之前有效。</summary>
    public static HeadlessUnitTestSession Current =>
        _shared ?? throw new InvalidOperationException(
            "headless 会话尚未建立 —— [AssemblyInitialize] 没跑到,通常是测试宿主没有加载本程序集的初始化。");

    /// <summary>建立全程序集唯一的 headless 会话。</summary>
    [AssemblyInitialize]
    public static void Init(TestContext _) => _shared = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));

    /// <summary>拆除会话。</summary>
    [AssemblyCleanup]
    public static void Cleanup()
    {
        try
        {
            _shared?.Dispose();
        }
        catch (NullReferenceException)
        {
            // Avalonia StartNew 的竞态,见类型注释第二段:此处炸掉的只是 _dispatchTask.Wait(),
            // 调度循环已被 Cancel()/CompleteAdding() 停下,吞掉不泄漏任何东西。
        }
        _shared = null;
    }
}

/// <summary>headless 测试宿主应用。</summary>
public class HeadlessTestApp : Application
{
    /// <summary>菜单的模板由主题提供:缺了它 MenuItem 无模板、无命中区,真实点击测不了。</summary>
    public override void Initialize() => Styles.Add(new FluentTheme());

    /// <summary>供 <see cref="HeadlessUnitTestSession" /> 反射调用。</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<HeadlessTestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
