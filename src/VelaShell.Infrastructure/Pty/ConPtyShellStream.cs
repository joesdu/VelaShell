using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using VelaShell.Core.Ssh;

namespace VelaShell.Infrastructure.Pty;

/// <summary>
/// 本地终端传输层(§12 P1-1):Windows ConPTY(CreatePseudoConsole)实现
/// <see cref="IShellStreamWrapper"/>,把 PowerShell/CMD/WSL/Git Bash 等本地 shell
/// 接进既有的 SshTerminalBridge → VT 引擎 → 自绘控件管线,SSH 侧零改动。
///
/// 管道拓扑:我们写 _input(子进程 stdin ← ConPTY),读 _output(子进程 stdout → ConPTY,
/// 输出为 UTF-8 的 VT 序列)。子进程退出时关闭伪控制台,读端得到断管 → 归一化为 EOF(返回 0),
/// 桥的读循环据此走远端关闭路径(标签变为已断开,可重开)。
/// </summary>
[SupportedOSPlatform(nameof(OSPlatform.Windows))]
public sealed class ConPtyShellStream : IShellStreamWrapper
{
    private readonly IntPtr _console;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly Process _process;
    private readonly object _teardownGate = new();
    private volatile bool _closed;
    private bool _disposed;

    private ConPtyShellStream(IntPtr console, FileStream input, FileStream output, Process process)
    {
        _console = console;
        _input = input;
        _output = output;
        _process = process;
    }

    /// <summary>启动本地 shell 并挂上伪控制台。commandLine 含参数(如 "bash.exe --login -i")。</summary>
    public static ConPtyShellStream Start(string commandLine, string? workingDirectory, int columns, int rows)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("本地终端目前仅支持 Windows(ConPTY)。");

        columns = Math.Clamp(columns, 2, 500);
        rows = Math.Clamp(rows, 2, 500);

        // 两对匿名管道:input(child ← us)、output(us ← child)。
        if (!NativeMethods.CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0))
            throw new InvalidOperationException($"CreatePipe(input) failed: {Marshal.GetLastWin32Error()}");
        if (!NativeMethods.CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
        {
            NativeMethods.CloseHandle(inputRead);
            NativeMethods.CloseHandle(inputWrite);
            throw new InvalidOperationException($"CreatePipe(output) failed: {Marshal.GetLastWin32Error()}");
        }

        int hr = NativeMethods.CreatePseudoConsole(
            new NativeMethods.COORD { X = (short)columns, Y = (short)rows },
            inputRead, outputWrite, 0, out var console);
        // 伪控制台会复制管道端点(官方文档),我们这侧的子进程端句柄立即关闭;
        // 关掉 outputWrite 也是读端在 conhost 退出后能拿到 EOF 的前提。
        NativeMethods.CloseHandle(inputRead);
        NativeMethods.CloseHandle(outputWrite);
        if (hr != 0)
        {
            NativeMethods.CloseHandle(inputWrite);
            NativeMethods.CloseHandle(outputRead);
            throw new InvalidOperationException($"CreatePseudoConsole failed: 0x{hr:X8}");
        }

        var startupInfo = new NativeMethods.STARTUPINFOEX();
        startupInfo.StartupInfo.cb = Marshal.SizeOf<NativeMethods.STARTUPINFOEX>();
        var attrListSize = IntPtr.Zero;
        NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrListSize);
        startupInfo.lpAttributeList = Marshal.AllocHGlobal(attrListSize);

        try
        {
            if (!NativeMethods.InitializeProcThreadAttributeList(startupInfo.lpAttributeList, 1, 0, ref attrListSize)
                || !NativeMethods.UpdateProcThreadAttribute(
                    startupInfo.lpAttributeList, 0,
                    (IntPtr)NativeMethods.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                throw new InvalidOperationException($"ProcThreadAttribute setup failed: {Marshal.GetLastWin32Error()}");
            }

            if (!NativeMethods.CreateProcess(
                null, commandLine, IntPtr.Zero, IntPtr.Zero, bInheritHandles: false,
                NativeMethods.EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero,
                string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory,
                ref startupInfo, out var processInfo))
            {
                throw new InvalidOperationException(
                    $"无法启动本地 shell(CreateProcess {Marshal.GetLastWin32Error()}):{commandLine}");
            }

            NativeMethods.CloseHandle(processInfo.hThread);
            NativeMethods.CloseHandle(processInfo.hProcess);

            var input = new FileStream(new SafeFileHandle(inputWrite, ownsHandle: true), FileAccess.Write, 4096);
            var output = new FileStream(new SafeFileHandle(outputRead, ownsHandle: true), FileAccess.Read, 16384);

            var process = Process.GetProcessById((int)processInfo.dwProcessId);
            var stream = new ConPtyShellStream(console, input, output, process);

            // 子进程退出(exit / 崩溃)→ 关伪控制台,读端断管 → EOF → 标签断开。
            // 注意:ClosePseudoConsole 会立刻终止 conhost 的输出泵,退出瞬间可能还有
            // 未写入管道的尾部输出(如 `cmd /c echo` 的结果),留一个短排空窗口再关。
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => _ = Task.Run(async () =>
            {
                await Task.Delay(300).ConfigureAwait(false);
                stream.CloseConsole();
            });

            return stream;
        }
        catch
        {
            NativeMethods.ClosePseudoConsole(console);
            NativeMethods.CloseHandle(inputWrite);
            NativeMethods.CloseHandle(outputRead);
            throw;
        }
        finally
        {
            NativeMethods.DeleteProcThreadAttributeList(startupInfo.lpAttributeList);
            Marshal.FreeHGlobal(startupInfo.lpAttributeList);
        }
    }

    public bool DataAvailable => false; // 桥不轮询;读循环阻塞在 ReadAsync 上。

    public bool CanRead => !_closed && !_disposed;

    public bool CanWrite => !_closed && !_disposed;

    public string? Expect(string regex, TimeSpan timeout) => null; // 本地 shell 无登录握手。

    public void WriteLine(string line)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(line + "\r");
        WriteAsync(bytes, 0, bytes.Length, CancellationToken.None).GetAwaiter().GetResult();
    }

    public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        try
        {
            return await _output.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // 断管/句柄关闭 = 会话结束,归一化为 EOF。
            return 0;
        }
    }

    public async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_closed || _disposed)
            return;

        try
        {
            await _input.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            await _input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // 子进程已退出:丢弃输入。
        }
    }

    public void Flush()
    {
        try
        {
            _input.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // ignore
        }
    }

    public void Resize(int columns, int rows)
    {
        if (_closed || _disposed)
            return;

        _ = NativeMethods.ResizePseudoConsole(_console, new NativeMethods.COORD
        {
            X = (short)Math.Clamp(columns, 2, 500),
            Y = (short)Math.Clamp(rows, 2, 500),
        });
    }

    /// <summary>关闭伪控制台并断开读端(幂等):由子进程退出回调与 Dispose 共用。</summary>
    private void CloseConsole()
    {
        lock (_teardownGate)
        {
            if (_closed)
                return;
            _closed = true;
            NativeMethods.ClosePseudoConsole(_console);
            try
            {
                _input.Dispose();
            }
            catch
            {
                // ignore
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // 用户关标签:先杀 shell 进程树,再收伪控制台与管道。
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 进程可能已自行退出。
        }

        CloseConsole();
        try
        {
            _output.Dispose();
        }
        catch
        {
            // ignore
        }
        _process.Dispose();
    }

    private static class NativeMethods
    {
        public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        public const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

        [StructLayout(LayoutKind.Sequential)]
        public struct COORD
        {
            public short X;
            public short Y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr lpPipeAttributes, int nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll")]
        public static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

        [DllImport("kernel32.dll")]
        public static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

        [DllImport("kernel32.dll")]
        public static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [DllImport("kernel32.dll")]
        public static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcess(
            string? lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            ref STARTUPINFOEX lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);
    }
}
