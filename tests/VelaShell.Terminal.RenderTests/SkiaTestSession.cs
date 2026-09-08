using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

namespace VelaShell.Terminal.RenderTests;

/// <summary>
/// 全程序集共用的 Skia headless 会话(与 VelaShell.Terminal.Tests 的 HeadlessTestSession 同一套路)。
/// <para>
/// Avalonia 的平台注册是进程级的,本程序集里每多一个测试类就多起一次会话,除了白白多启一次
/// Avalonia,还会把 <c>StartNew</c> 的那条竞态(见 <see cref="Cleanup" />)多摇一次骰子。
/// </para>
/// </summary>
[TestClass]
public class SkiaTestSession
{
    private static HeadlessUnitTestSession? _shared;

    /// <summary>共用会话;在 <see cref="Init" /> 之后、<see cref="Cleanup" /> 之前有效。</summary>
    public static HeadlessUnitTestSession Current =>
        _shared ?? throw new InvalidOperationException(
            "headless 会话尚未建立 —— [AssemblyInitialize] 没跑到,通常是测试宿主没有加载本程序集的初始化。");

    /// <summary>建立全程序集唯一的 headless 会话。</summary>
    [AssemblyInitialize]
    public static void Init(TestContext _) => _shared = HeadlessUnitTestSession.StartNew(typeof(SkiaHeadlessApp));

    /// <summary>拆除会话。</summary>
    [AssemblyCleanup]
    public static void Cleanup()
    {
        // Avalonia 12.1.2 的 HeadlessUnitTestSession.StartNew 有竞态:它把 Task.Run 的返回值
        // 经闭包变量交给会话构造器(源码里那句 `task!`),写入方是调用线程、读取方是工作线程,
        // 中间没有任何同步。工作线程读到 null 时 _dispatchTask 就永久为 null —— 而它只在
        // Dispose() 的 _dispatchTask.Wait() 处才炸:所有用例照常通过,只有清理红一条,
        // 重跑即绿。此时 Cancel() 与 CompleteAdding() 已执行,调度循环必然退出,
        // 吞掉这条 NRE 不泄漏任何东西。上游 master 至今未修。
        try
        {
            _shared?.Dispose();
        }
        catch (NullReferenceException)
        {
            // 见上。
        }
        _shared = null;
    }
}

/// <summary>Skia 软件光栅的 headless 测试宿主(<c>UseHeadlessDrawing = false</c> 才有真像素)。</summary>
public class SkiaHeadlessApp : Application
{
    /// <inheritdoc />
    public override void Initialize() => Styles.Add(new FluentTheme());

    /// <summary>供 <see cref="HeadlessUnitTestSession" /> 反射调用。</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<SkiaHeadlessApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
