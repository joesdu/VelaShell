namespace VelaShell.Core.Data;

/// <summary>
/// 已存的机密此刻被主密码锁着:<see cref="ISecretProtector.Protect" /> 不能加密新的明文。
/// </summary>
/// <remarks>
/// <para>
/// 主密码开着、还没解锁(或已自动锁定)时,本机加密密钥只以包裹过的形式存在。这时加密新的明文
/// 只有两条路:另起一把主密码管不到的新密钥(等于悄悄关掉保护),或者明说做不到 —— 只走后一条。
/// </para>
/// <para>
/// 调用方要么先经 <c>IMasterPasswordGate.EnsureUnlockedAsync</c> 解锁,要么(仓储)在锁着时
/// 不改已存的机密字段。继承 <see cref="InvalidOperationException" />:漏网的调用点按「状态不允许」报出来,
/// 而不是被当成数据损坏。
/// </para>
/// </remarks>
public sealed class SecretsLockedException : InvalidOperationException
{
    /// <summary>用默认说明构造。</summary>
    public SecretsLockedException()
        : base("Saved secrets are locked: the master password has not been entered.") { }

    /// <summary>用给定说明构造。</summary>
    /// <param name="message">说明。</param>
    public SecretsLockedException(string message) : base(message) { }

    /// <summary>用给定说明与内层异常构造。</summary>
    /// <param name="message">说明。</param>
    /// <param name="innerException">内层异常。</param>
    public SecretsLockedException(string message, Exception innerException) : base(message, innerException) { }
}
