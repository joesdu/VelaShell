// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 第 8 节「Connection Setup」(传输与协议无关);
//   本地传输的约定:显示号 N 的服务端监听 /tmp/.X11-unix/XN(Linux 另有抽象命名空间里的同名套接字,
//   Xlib / XCB 对 DISPLAY=:N 先试它),与 X.Org 的 Xtrans 行为一致。
//
//   本机客户端(DISPLAY=:N)走这里,比 TCP 快,也不必开端口。连进来的一律算本机连接。

using System.Net.Sockets;

namespace VelaShell.XServer.Server;

public sealed partial class X11Server
{
    private readonly List<(Socket Socket, string? File)> _unixListeners = [];

    /// <summary>Unix 套接字的路径:选项给了就用它,否则 Windows 以外默认 /tmp/.X11-unix/X{N};空字符串 = 不监听。</summary>
    private string? UnixSocketPath => _options.UnixSocketPath switch
    {
        "" => null,
        { } path => path,
        null when OperatingSystem.IsWindows() => null,
        null => $"/tmp/.X11-unix/X{_options.DisplayNumber}",
    };

    private void StartUnixListeners(CancellationToken cancellationToken)
    {
        if (UnixSocketPath is not { } path || !Socket.OSSupportsUnixDomainSockets)
        {
            return;
        }
        if (OperatingSystem.IsLinux())
        {
            // 抽象命名空间:不落文件,进程退出自动消失;Xlib / XCB 对 :N 先试它。
            TryListen("\0" + path, file: null, cancellationToken);
        }
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (directory is not null && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                if (!OperatingSystem.IsWindows() && directory == "/tmp/.X11-unix")
                {
                    // 与 X.Org 一致:目录人人可写、带粘滞位,别的用户的服务端也能在这里建自己的套接字。
                    File.SetUnixFileMode(directory, (UnixFileMode)0x3FF);   // 八进制 1777
                }
            }
            if (File.Exists(path))
            {
                File.Delete(path);   // 上次没收拾干净的套接字文件(端口占用的检查由 TCP 那一侧完成)
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _options.Log?.Invoke($"Unix socket {path} unavailable: {ex.Message}");
            return;
        }
        TryListen(path, path, cancellationToken);
    }

    private void TryListen(string endpoint, string? file, CancellationToken cancellationToken)
    {
        Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Bind(new UnixDomainSocketEndPoint(endpoint));
            socket.Listen(64);
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            _options.Log?.Invoke($"Unix socket {endpoint.TrimStart('\0')} unavailable: {ex.SocketErrorCode}");
            return;
        }
        _unixListeners.Add((socket, file));
        _ = AcceptUnixLoopAsync(socket, cancellationToken);
    }

    private async Task AcceptUnixLoopAsync(Socket listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket connection;
            try
            {
                connection = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            TrackConnection(ServeUnixAsync(connection, cancellationToken));
        }
    }

    private async Task ServeUnixAsync(Socket connection, CancellationToken cancellationToken)
    {
        await using NetworkStream stream = new(connection, ownsSocket: true);
        try
        {
            await ServeAsync(stream, isLocal: true, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // 服务端正在收工。
        }
    }

    private void StopUnixListeners()
    {
        foreach ((Socket socket, string? file) in _unixListeners)
        {
            socket.Dispose();
            if (file is not null)
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // 删不掉就留着;下次启动会先删。
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        _unixListeners.Clear();
    }
}
