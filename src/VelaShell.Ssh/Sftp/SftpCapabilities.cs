// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH PROTOCOL  limits@openssh.com 与其它 SFTP 扩展
//   行为规格:         velashell-docs/zh/ssh/spec/06-sftp.md §5.2、§七

namespace VelaShell.Ssh.Sftp;

/// <summary>这台服务端支持什么。</summary>
/// <remarks>
/// <para>
/// 〔决策 velashell-docs/zh/ssh/spec/06 §7.2〕<b>能力必须可查，而不只是内部悄悄降级。</b>
/// </para>
/// <para>
/// 理由很具体：<c>posix-rename</c> 与普通 <c>rename</c> 的语义<b>不一样</b> ——
/// 前者原子覆盖，后者在目标存在时失败（某些服务端连跨目录移动都拒）。
/// 库内部静默降级的话，上层就无从知道自己拿到的是哪一种语义，
/// 也没法在界面上提示「这台服务器不支持原子覆盖，移动操作可能不是原子的」。
/// </para>
/// </remarks>
public sealed class SftpCapabilities
{
    internal SftpCapabilities(uint serverVersion, IReadOnlyDictionary<string, byte[]> rawExtensions)
    {
        ServerVersion = serverVersion;
        RawExtensions = rawExtensions.ToDictionary(
            static pair => pair.Key, static pair => (ReadOnlyMemory<byte>)pair.Value, StringComparer.Ordinal);
        Limits = SftpLimits.Conservative;
    }

    /// <summary>服务端宣告的版本号（我们按 3 工作，更高的会降级）。</summary>
    public uint ServerVersion { get; }

    /// <summary>服务端在 <c>SSH_FXP_VERSION</c> 里宣告的全部扩展，原样（只读视图）。</summary>
    public IReadOnlyDictionary<string, ReadOnlyMemory<byte>> RawExtensions { get; }

    /// <summary>读写上限。</summary>
    public SftpLimits Limits { get; internal set; }

    /// <summary>支持原子重命名（覆盖目标）。</summary>
    public bool HasPosixRename => RawExtensions.ContainsKey(SftpExtensionNames.PosixRename);

    /// <summary>支持建硬链接。</summary>
    public bool HasHardLink => RawExtensions.ContainsKey(SftpExtensionNames.HardLink);

    /// <summary>支持强制落盘。</summary>
    public bool HasFsync => RawExtensions.ContainsKey(SftpExtensionNames.Fsync);

    /// <summary>支持查文件系统用量。</summary>
    public bool HasStatVfs => RawExtensions.ContainsKey(SftpExtensionNames.StatVfs);

    /// <summary>宣告了读写上限。</summary>
    public bool HasLimits => RawExtensions.ContainsKey(SftpExtensionNames.Limits);

    /// <summary>支持服务端内复制（不经过网络）。</summary>
    public bool HasCopyData => RawExtensions.ContainsKey(SftpExtensionNames.CopyData);

    /// <summary>支持取指定用户的家目录。</summary>
    public bool HasHomeDirectory => RawExtensions.ContainsKey(SftpExtensionNames.HomeDirectory);

    /// <summary>支持展开 <c>~</c>。</summary>
    public bool HasExpandPath => RawExtensions.ContainsKey(SftpExtensionNames.ExpandPath);

    /// <summary>服务端支不支持某个扩展。</summary>
    public bool Supports(string extensionName) => RawExtensions.ContainsKey(extensionName);
}
