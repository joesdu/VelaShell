using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VelaShell.Services.MasterPassword;

/// <summary>读当前登录会话是否处于锁屏状态。</summary>
public interface ISystemSessionLockProbe
{
    /// <summary>这个平台读得到锁屏状态(读不到时设置页不显示「系统锁屏时锁定」,免得开了却不起作用)。</summary>
    bool IsSupported { get; }

    /// <summary>读一次。</summary>
    /// <param name="locked">锁着为 true。</param>
    /// <returns>读到了为 true;读不到(调用失败、状态未知)为 false —— 这时调用方什么都不做。</returns>
    bool TryRead(out bool locked);
}

/// <summary>按平台挑锁屏状态的读法。</summary>
public static class SystemSessionLockProbes
{
    /// <summary>
    /// 这个平台的读法:Windows 用 <c>WTSQuerySessionInformation(WTSSessionInfoEx)</c>;macOS / Linux 暂时没有
    /// (macOS 的 <c>CGSessionCopyCurrentDictionary</c>、Linux 的 logind <c>LockedHint</c> 都只能在真机上验证,这一轮不做,
    /// 设置页也就不显示那一项)。
    /// </summary>
    public static ISystemSessionLockProbe ForCurrentPlatform() =>
        OperatingSystem.IsWindows() ? new WindowsSessionLockProbe() : new UnsupportedSessionLockProbe();

    private sealed class UnsupportedSessionLockProbe : ISystemSessionLockProbe
    {
        public bool IsSupported => false;

        public bool TryRead(out bool locked)
        {
            locked = false;
            return false;
        }
    }
}

/// <summary>
/// Windows:<c>WTSQuerySessionInformationW(WTS_CURRENT_SERVER_HANDLE, WTS_CURRENT_SESSION, WTSSessionInfoEx)</c>
/// 交回 <c>WTSINFOEXW</c>,其中 <c>WTSINFOEX_LEVEL1_W.SessionFlags</c> 为 <c>WTS_SESSIONSTATE_LOCK</c>(0)时锁着、
/// <c>WTS_SESSIONSTATE_UNLOCK</c>(1)时没锁,别的值(<c>WTS_SESSIONSTATE_UNKNOWN</c>)当作读不到。
/// </summary>
/// <remarks>
/// 轮询而不是订阅 <c>WM_WTSSESSION_CHANGE</c>:那要一个窗口句柄与消息钩子,轮询几秒一次足够,也不依赖界面框架。
/// 结构布局:<c>Level</c>(4 字节)之后的联合体按 8 字节对齐(里面有 <c>LARGE_INTEGER</c>),<c>SessionFlags</c> 在偏移 16。
/// Windows 7 / Server 2008 R2 上锁与没锁的取值是反的(已知问题),.NET 11 不支持那两版。
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsSessionLockProbe : ISystemSessionLockProbe
{
    private const int WtsSessionInfoEx = 25;
    private const int CurrentSession = -1;
    private const int SessionFlagsOffset = 16;
    private const int StateLock = 0;
    private const int StateUnlock = 1;

    public bool IsSupported => TryRead(out _);

    public bool TryRead(out bool locked)
    {
        locked = false;
        nint buffer = 0;
        try
        {
            if (!WTSQuerySessionInformationW(0, CurrentSession, WtsSessionInfoEx, out buffer, out int bytes)
                || buffer == 0 || bytes < SessionFlagsOffset + sizeof(int) || Marshal.ReadInt32(buffer) != 1)
            {
                return false;
            }
            switch (Marshal.ReadInt32(buffer, SessionFlagsOffset))
            {
                case StateLock:
                    locked = true;
                    return true;
                case StateUnlock:
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            if (buffer != 0)
            {
                WTSFreeMemory(buffer);
            }
        }
    }

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQuerySessionInformationW(nint server, int sessionId, int infoClass, out nint buffer, out int bytesReturned);

    [LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(nint memory);
}
