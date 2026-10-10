using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Avalonia.Controls;
using VelaShell.XServer;

namespace VelaShell.Services.XServer;

/// <summary>
/// 任务栏按 X 程序归组(xs_plan F17):Windows 上给每个 X 窗口设一个按 WM_CLASS 的类名算出的 AppUserModelID
/// (窗口的属性存储里的 System.AppUserModel.ID)—— 原先 X 窗口都挤在 VelaShell 主程序的任务栏按钮里,开着几个远端程序时分不清。
/// 同时设 System.AppUserModel.PreventPinning:固定到任务栏的项要能重新启动,而远端程序没法从本机直接启动。
/// 窗口关掉之前清掉这两项(Windows 要求)。macOS 与 Linux 的宿主窗口没有对应的机制,什么也不做。
/// </summary>
internal static partial class TaskbarGroup
{
    /// <summary>AppUserModelID 的长度上限是 128 个字符;类名截到这么长,前缀留在里面。</summary>
    private const int MaxClassChars = 96;

    /// <summary>按类名算出的组名;没有类名的窗口不另外归组(返回 null,留在 VelaShell 的按钮里)。</summary>
    public static string? IdFor(XTopLevelSnapshot snapshot)
    {
        if (snapshot.ClassName.Length == 0)
        {
            return null;
        }
        // 只留字母、数字、点、横线、下划线(AppUserModelID 不许有空格);其余换成下划线。
        StringBuilder id = new("VelaShell.X11.");
        foreach (char c in snapshot.ClassName.AsSpan(0, Math.Min(snapshot.ClassName.Length, MaxClassChars)))
        {
            id.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');
        }
        return id.ToString();
    }

    /// <summary>把窗口归进 <paramref name="id" /> 这一组;null = 清掉(回到 VelaShell 的按钮)。做不到时返回 false。</summary>
    public static bool Apply(Window window, string? id)
    {
        if (!OperatingSystem.IsWindows() || window.TryGetPlatformHandle() is not { } handle)
        {
            return false;
        }
        try
        {
            return SetProperties(handle.Handle, id);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            return false;
        }
    }

    // System.AppUserModel 属性集({9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}):5 = ID,9 = PreventPinning。
    private static readonly Guid AppUserModel = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private static readonly Guid PropertyStoreIid = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private const ushort VtBool = 11, VtLpwstr = 31;   // default(PropVariant) 是 VT_EMPTY:清掉这一项

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid set, uint id)
    {
        public Guid Set = set;
        public uint Id = id;
    }

    /// <summary>PROPVARIANT:类型、三个保留字,再是 8 字节对齐的值(按 64 位的大小开,32 位上多出来的不会被读)。</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public nint Pointer;
        [FieldOffset(8)] public short Bool;
    }

    [SupportedOSPlatform("windows")]
    private static unsafe bool SetProperties(nint hwnd, string? id)
    {
        Guid iid = PropertyStoreIid;
        if (SHGetPropertyStoreForWindow(hwnd, &iid, out nint store) < 0 || store == 0)
        {
            return false;
        }
        nint text = id is null ? 0 : Marshal.StringToCoTaskMemUni(id);
        try
        {
            // IPropertyStore 的虚表:IUnknown 三项之后是 GetCount、GetAt、GetValue、SetValue、Commit。
            void** table = *(void***)store;
            var setValue = (delegate* unmanaged[Stdcall]<nint, PropertyKey*, PropVariant*, int>)table[6];
            var commit = (delegate* unmanaged[Stdcall]<nint, int>)table[7];
            PropertyKey idKey = new(AppUserModel, 5), pinKey = new(AppUserModel, 9);
            PropVariant pin = id is null ? default : new PropVariant { Type = VtBool, Bool = -1 };
            PropVariant value = id is null ? default : new PropVariant { Type = VtLpwstr, Pointer = text };
            // 设的时候先设 PreventPinning 再设 ID;清的时候反过来。
            bool ok = id is null
                ? setValue(store, &idKey, &value) >= 0 && setValue(store, &pinKey, &pin) >= 0
                : setValue(store, &pinKey, &pin) >= 0 && setValue(store, &idKey, &value) >= 0;
            return ok && commit(store) >= 0;
        }
        finally
        {
            if (text != 0)
            {
                Marshal.FreeCoTaskMem(text);   // SetValue 自己复制了一份
            }
            ((delegate* unmanaged[Stdcall]<nint, uint>)(*(void***)store)[2])(store);   // Release
        }
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("shell32.dll")]
    private static unsafe partial int SHGetPropertyStoreForWindow(nint hwnd, Guid* iid, out nint store);
}
