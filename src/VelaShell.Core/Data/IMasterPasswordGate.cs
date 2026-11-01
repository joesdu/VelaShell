using VelaShell.Core.Models;

namespace VelaShell.Core.Data;

/// <summary>
/// 可选主密码的异步闸口:用到已存机密的入口(连接的构建点、云同步、插件机密、插件信任库、编辑框保存)
/// 在解析 / 保存之前先 await 它。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ISecretProtector" /> 是同步接口、调用点遍布全仓,不能在每个调用点就地弹框;锁着时它只如实说「解不开」
/// (<see cref="ISecretProtector.IsLocked" />)。弹不弹解锁框由入口决定:用户主动发起的(连接、手动重连、手动同步、
/// 编辑框保存)是交互式,弹;自动重连、睡眠唤醒后的批量重连、隧道自动重建、自动同步是非交互式,不弹 ——
/// 网络恢复后 20 个标签同时自动重连,不能弹出一个用户不知由谁触发的解锁框(钓鱼疑虑)。
/// </para>
/// <para>
/// 同一时刻只有一个解锁框:并发的交互式请求等同一个框的结果(启动时恢复 10 个标签只弹一次)。
/// 主密码没开时恒为解着。
/// </para>
/// </remarks>
public interface IMasterPasswordGate
{
    /// <summary>主密码保护开着(事实来源是包裹格式本身,不是设置里的 <c>MasterPasswordProtection</c>)。</summary>
    /// <remarks>首次访问会触发密钥解析(可能阻塞,别在 UI 线程上第一个调)。</remarks>
    bool IsEnabled { get; }

    /// <summary>主密码开着、此刻锁着。</summary>
    /// <remarks>首次访问会触发密钥解析(可能阻塞,别在 UI 线程上第一个调)。</remarks>
    bool IsLocked { get; }

    /// <summary>
    /// 确保已解锁:没开主密码或已解锁时立即为 true;锁着时交互式入口弹(或等着正开着的)解锁框,非交互式入口直接为 false。
    /// </summary>
    /// <param name="interactive">是用户主动发起的操作(可以弹框)。</param>
    /// <param name="cancellationToken">取消令牌(只取消这一方的等待,不关掉别人也在等的那个框)。</param>
    /// <returns>此刻解着锁为 true;用户取消了解锁框、或非交互式时为 false。</returns>
    Task<bool> EnsureUnlockedAsync(bool interactive, CancellationToken cancellationToken = default);

    /// <summary>解锁成功之后触发(在线程池上;订阅方自己切回 UI 线程)。</summary>
    event EventHandler? Unlocked;

    /// <summary>锁定之后触发(手动锁定、自动锁定;在调用方的线程上)。</summary>
    event EventHandler? Locked;
}

/// <summary>交互式解锁框(界面层实现:模态于主窗口,框里自己调 <see cref="IMasterPasswordService.UnlockAsync" />)。</summary>
public interface IMasterPasswordPrompt
{
    /// <summary>弹解锁框并等用户作答。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解开了为 true;用户取消为 false。</returns>
    Task<bool> PromptUnlockAsync(CancellationToken cancellationToken);
}

/// <summary>一次解锁的结果分类。</summary>
public enum MasterPasswordUnlockStatus
{
    /// <summary>解开了。</summary>
    Unlocked,

    /// <summary>主密码(或恢复码)不对。</summary>
    WrongSecret,

    /// <summary>上一次输错之后的等待还没过(见 <see cref="MasterPasswordUnlockResult.RetryAfter" />)。</summary>
    Throttled,

    /// <summary>没有能解的东西(见 <see cref="MasterKeyStatus.MasterPasswordProblem" />)。</summary>
    NothingToUnlock,

    /// <summary>恢复码写错了(字符认不出或校验不通过,不算一次输错)。</summary>
    MalformedRecoveryCode,

    /// <summary>开启时跳过了恢复码。</summary>
    NoRecoveryCode,

    /// <summary>主密码没开。</summary>
    NotEnabled,

    /// <summary>本来就解着锁。</summary>
    AlreadyUnlocked
}

/// <summary>一次解锁的结果。</summary>
/// <param name="Status">结果分类。</param>
/// <param name="RetryAfter">输错之后要等多久才能再试(没有等待时为零)。</param>
public readonly record struct MasterPasswordUnlockResult(MasterPasswordUnlockStatus Status, TimeSpan RetryAfter = default);

/// <summary>主密码的状态与解锁 / 锁定(设置页、状态栏锁形入口、解锁框用)。</summary>
public interface IMasterPasswordService : IMasterPasswordGate
{
    /// <summary>开启时生成过恢复码。</summary>
    bool HasRecoveryCode { get; }

    /// <summary>这一次是用恢复码解开的(界面据此要求设一个新的主密码)。</summary>
    bool UnlockedWithRecoveryCode { get; }

    /// <summary>输错之后还要等多久才能再试。</summary>
    TimeSpan RetryAfter { get; }

    /// <summary>本机加密密钥与主密码的状态(为什么解不了锁等)。</summary>
    MasterKeyStatus GetStatus();

    /// <summary>用主密码解锁(派生在后台线程上做)。</summary>
    /// <param name="passwordUtf8">主密码的 UTF-8 字节;交出所有权,用完由服务清零。</param>
    /// <param name="cancellationToken">取消令牌(派生一旦开算就停不下来,只是不再等它)。</param>
    Task<MasterPasswordUnlockResult> UnlockAsync(byte[] passwordUtf8, CancellationToken cancellationToken = default);

    /// <summary>用恢复码解锁(之后要求设新的主密码)。</summary>
    /// <param name="recoveryCode">用户输入的恢复码。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<MasterPasswordUnlockResult> UnlockWithRecoveryCodeAsync(string recoveryCode, CancellationToken cancellationToken = default);

    /// <summary>立即锁定(清零内存里的密钥)。主密码没开或本来就锁着时返回 false。</summary>
    bool LockNow();

    /// <summary>
    /// 开启主密码:用它派生的密钥包裹本机加密密钥(逐条写新、读回核对),任一步失败整体回到未开启。已存的密文一字不改。
    /// </summary>
    /// <param name="passwordUtf8">新主密码的 UTF-8 字节;交出所有权,用完由服务清零。</param>
    /// <param name="createRecoveryCode">同时生成一份恢复码(它也能解开,结果里交回给用户抄下)。</param>
    /// <param name="cancellationToken">取消令牌(只是不再等,写存储一旦开始就做完)。</param>
    Task<MasterPasswordChangeResult> EnableAsync(byte[] passwordUtf8, bool createRecoveryCode, CancellationToken cancellationToken = default);

    /// <summary>关闭主密码:核对当前主密码,把本机加密密钥写回第一步的形态,删掉 KDF 头。</summary>
    /// <param name="passwordUtf8">当前主密码;交出所有权。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<MasterPasswordChangeResult> DisableAsync(byte[] passwordUtf8, CancellationToken cancellationToken = default);

    /// <summary>修改主密码:旧的核对过才换;先加新的一份包裹、换头、再删旧的一份,中途断在哪儿都有一个口令解得开。</summary>
    /// <param name="currentUtf8">当前主密码;交出所有权。</param>
    /// <param name="nextUtf8">新主密码;交出所有权。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<MasterPasswordChangeResult> ChangeAsync(byte[] currentUtf8, byte[] nextUtf8, CancellationToken cancellationToken = default);

    /// <summary>用恢复码解开之后设一个新的主密码(恢复码仍然有效)。只在这一次解锁是用恢复码时可用。</summary>
    /// <param name="nextUtf8">新主密码;交出所有权。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<MasterPasswordChangeResult> SetPasswordAfterRecoveryAsync(byte[] nextUtf8, CancellationToken cancellationToken = default);

    /// <summary>
    /// 忘记主密码:头、清单、密钥文件改名保留(<c>.forgotten-时间戳</c>,不删;密钥链条目原样不动),另起新的本机加密密钥,
    /// 主密码随之关掉。已存的机密在这台设备上从此解不开(没有后门),连接时重新输入。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<MasterPasswordChangeResult> ForgetAsync(CancellationToken cancellationToken = default);

    /// <summary>开启、关闭、修改、忘记之后触发(界面据此刷新状态;在线程池上)。</summary>
    event EventHandler? StateChanged;
}

/// <summary>开启 / 关闭 / 修改 / 忘记主密码的结果分类。</summary>
public enum MasterPasswordChangeStatus
{
    /// <summary>做完了。</summary>
    Done,

    /// <summary>当前主密码不对。</summary>
    WrongPassword,

    /// <summary>上一次输错之后的等待还没过。</summary>
    Throttled,

    /// <summary>主密码没开(关闭、修改时)。</summary>
    NotEnabled,

    /// <summary>主密码已经开着(开启时)。</summary>
    AlreadyEnabled,

    /// <summary>这一次不是用恢复码解开的(设新主密码时)。</summary>
    NotRecovered,

    /// <summary>有密钥这会儿取不到(系统密钥链用不了):不动任何东西,等它恢复再做。</summary>
    KeysUnavailable,

    /// <summary>写存储失败,已整体回到操作之前的状态。</summary>
    StorageFailed,

    /// <summary>写存储失败,而且没能完全撤回:主密码现在是开着的,用刚输入的那个主密码解锁(见 <see cref="MasterPasswordChangeResult.Detail" />)。</summary>
    StorageFailedPartially
}

/// <summary>开启 / 关闭 / 修改 / 忘记主密码的结果。</summary>
/// <param name="Status">结果分类。</param>
/// <param name="RecoveryCode">开启时生成的恢复码(给用户抄下;之后不再能看到)。</param>
/// <param name="RetryAfter">输错之后要等多久才能再试。</param>
/// <param name="Detail">给日志与提示的补充(改名保留成的文件、失败的位置),不含任何口令材料。</param>
public readonly record struct MasterPasswordChangeResult(
    MasterPasswordChangeStatus Status, string? RecoveryCode = null, TimeSpan RetryAfter = default, string? Detail = null);

/// <summary>闸口的几个小帮手。</summary>
public static class MasterPasswordGuard
{
    /// <summary>
    /// 这条连接会不会用到已存的机密:自己存着密码 / 私钥口令 / 插件机密,引用了共享凭据,或者经跳板(跳板配置从库里读)。
    /// </summary>
    /// <remarks>
    /// 只看「有没有」,不看「解没解开」:锁定之后内存里可能还留着锁定前解开的明文(已开的标签),
    /// 锁着时照样不许拿它去连 —— 否则锁定只挡住了新开的连接。
    /// </remarks>
    public static bool UsesSavedSecrets(SessionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.CredentialSource is not null
               || profile.JumpHostProfileId is not null
               || !string.IsNullOrEmpty(profile.Password)
               || !string.IsNullOrEmpty(profile.PrivateKeyPassphrase)
               || profile.PluginSecrets is { Count: > 0 };
    }

    /// <summary>锁没锁:还没解析过密钥时在线程池上问(解析可能要等系统密钥链),不压调用方的线程。</summary>
    public static async Task<bool> IsLockedAsync(this IMasterPasswordGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        return await Task.Run(() => gate.IsLocked).ConfigureAwait(false);
    }

    /// <summary>不弹框,一直等到解锁(没开主密码或已解锁时立即返回)。</summary>
    public static async Task WaitUntilUnlockedAsync(this IMasterPasswordGate gate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gate);
        var unlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnUnlocked(object? sender, EventArgs e) => unlocked.TrySetResult();
        gate.Unlocked += OnUnlocked;
        try
        {
            if (!await gate.IsLockedAsync().ConfigureAwait(false))
            {
                return;
            }
            await unlocked.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Unlocked -= OnUnlocked;
        }
    }
}
