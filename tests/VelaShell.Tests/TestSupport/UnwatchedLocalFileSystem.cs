using VelaShell.ViewModels;

namespace VelaShell.Tests.TestSupport;

/// <summary>
/// 读写真实磁盘、但不挂目录监视的本地文件系统:给在临时目录上开本地栏 / 上传选择框的 headless UI 测试用。
/// </summary>
/// <remarks>
/// 本程序集把 <c>RxSchedulers.MainThreadScheduler</c> 换成了 <c>CurrentThreadSequencer</c>(见 <c>ModuleInit</c>),
/// 监视触发的刷新因此不回 UI 线程,而是就地跑在防抖计时器的线程池线程上:在 UI 线程之外重建 <c>Entries</c>,
/// ListBox 当场抛 "The calling thread cannot access this object because a different thread owns it.",
/// 选中集被清掉、来不及恢复;刷新若晚到本次 dispatch 之后,还会把 <c>Dispatcher.UIThread</c> 绑到那条线程上,
/// 坑下一个测试(见 <c>LocalPathPickerMarqueeUiTests</c> 的关窗处)。
/// macOS 的 FSEvents 会在开启监视之后补投建临时目录那一下的创建事件(Windows / Linux 不会),
/// 所以这类测试只在 macOS 上偶发失败(PR #705 的 CI:切到图标视图后选中被清空)。
/// 这些测试盯的是视图接线;自动刷新由 <c>LocalFilePaneViewModelTests</c> 用假文件系统单独盯着,这里干脆不监视。
/// </remarks>
internal sealed class UnwatchedLocalFileSystem : ILocalFileSystem
{
    private readonly PhysicalLocalFileSystem _disk = new();

    /// <summary>在临时目录之类的真实目录上用的本地栏视图模型,不监视目录。</summary>
    public static LocalFilePaneViewModel NewPane() => new(new(), new UnwatchedLocalFileSystem());

    public Task<IReadOnlyList<LocalFileSystemEntry>> EnumerateAsync(string path, CancellationToken cancellationToken) =>
        _disk.EnumerateAsync(path, cancellationToken);

    public Task DeleteFileAsync(string path, CancellationToken cancellationToken) =>
        _disk.DeleteFileAsync(path, cancellationToken);

    public Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken) =>
        _disk.DeleteDirectoryAsync(path, cancellationToken);

    public Task MoveAsync(string sourcePath, string destPath, CancellationToken cancellationToken) =>
        _disk.MoveAsync(sourcePath, destPath, cancellationToken);

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) =>
        _disk.CreateDirectoryAsync(path, cancellationToken);

    public Task<long> GetFileSizeAsync(string path, CancellationToken cancellationToken) =>
        _disk.GetFileSizeAsync(path, cancellationToken);

    public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken) =>
        _disk.OpenReadAsync(path, cancellationToken);

    public Task<Stream> OpenWriteAsync(string path, FileMode mode, CancellationToken cancellationToken) =>
        _disk.OpenWriteAsync(path, mode, cancellationToken);

    /// <summary>不监视:返回 null 即「监视不了」,视图模型照常退化为不自动刷新。</summary>
    public IDisposable? Watch(string directory, Action onChanged) => null;
}
