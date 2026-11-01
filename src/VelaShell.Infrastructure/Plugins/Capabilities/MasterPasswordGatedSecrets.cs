using VelaShell.Core.Data;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Secrets;

namespace VelaShell.Infrastructure.Plugins.Capabilities;

/// <summary>
/// 插件机密过主密码闸口的一层:锁着时读写先等交互式解锁(弹框,或等着已经开着的那个框),
/// 用户取消就抛 <see cref="PluginPermissionDeniedException" />。删除不用解密,照常放行。
/// </summary>
/// <remarks>
/// <para>
/// <b>绝不交回 null</b>:「不存在」与「此刻读不出来」对插件是两回事 —— 交回 null 插件会以为机密不存在、让用户重填,
/// 把旧值覆盖掉。SDK 现有的异常里最接近「用户没让读」的就是权限被拒(线上码 <c>permission-denied</c>,
/// 隔离插件经 RPC 照样还原成同一个类型),插件对它的既有处理是放弃这一次操作、不改已存的东西。
/// </para>
/// <para>
/// SDK 里另写好了专门的「已锁定」契约(<c>SecretsLockedException</c> + 能力位,velashell-plugin-sdk,未发版);
/// 宿主这一轮按维护者 2026-10-31 的定法先「等待交互式解锁」,不接它。隔离插件的 RPC 有自己的超时:用户在解锁框前
/// 停太久,插件那一侧先收到超时(同样不是 null)。
/// </para>
/// </remarks>
internal sealed class MasterPasswordGatedSecrets(ISecretsApi inner, IMasterPasswordGate gate) : ISecretsApi
{
    /// <summary>有闸口就套一层,没有就原样交回。</summary>
    public static ISecretsApi Wrap(ISecretsApi inner, IMasterPasswordGate? gate) =>
        gate is null || inner is UnavailableSecrets ? inner : new MasterPasswordGatedSecrets(inner, gate);

    public async Task<string?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        await EnsureUnlockedAsync(cancellationToken).ConfigureAwait(false);
        return await inner.GetAsync(name, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        await EnsureUnlockedAsync(cancellationToken).ConfigureAwait(false);
        await inner.SetAsync(name, value, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default) =>
        inner.DeleteAsync(name, cancellationToken);

    private async Task EnsureUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!await gate.EnsureUnlockedAsync(interactive: true, cancellationToken).ConfigureAwait(false))
        {
            throw new PluginPermissionDeniedException(
                "Saved secrets are locked: the user did not enter the VelaShell master password. Nothing was read or changed.");
        }
    }
}
