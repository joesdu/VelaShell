using System.Runtime.InteropServices;
using System.Security;

namespace VelaShell.Security;

/// <summary>
/// SecureString ↔ 明文的最小转换工具。仅在必须与只接受 <see cref="string" /> 的
/// 边界(持久化 DTO、SSH.NET 等)交接时使用;materialize 出的明文应尽快转交并弃用。
/// </summary>
public static class SecureStringConvert
{
    /// <summary>把 SecureString 还原为明文字符串(经非托管缓冲并即时清零);null/空返回对应值。</summary>
    public static string? ToPlaintext(SecureString? secure)
    {
        if (secure is null)
        {
            return null;
        }
        if (secure.Length == 0)
        {
            return string.Empty;
        }
        IntPtr ptr = Marshal.SecureStringToGlobalAllocUnicode(secure);
        try
        {
            return Marshal.PtrToStringUni(ptr);
        }
        finally
        {
            Marshal.ZeroFreeGlobalAllocUnicode(ptr);
        }
    }

    /// <summary>
    /// 把 SecureString 编成 UTF-8 字节(主密码交给派生用),不经托管字符串:非托管缓冲按 UTF-16 读出、逐字编码,随即清零。
    /// 调用方拥有返回的数组,用完清零(主密码服务会替它清)。null 为空数组。
    /// </summary>
    public static unsafe byte[] ToUtf8(SecureString? secure)
    {
        if (secure is null || secure.Length == 0)
        {
            return [];
        }
        IntPtr ptr = Marshal.SecureStringToGlobalAllocUnicode(secure);
        try
        {
            var chars = new ReadOnlySpan<char>((void*)ptr, secure.Length);
            byte[] bytes = new byte[System.Text.Encoding.UTF8.GetByteCount(chars)];
            System.Text.Encoding.UTF8.GetBytes(chars, bytes);
            return bytes;
        }
        finally
        {
            Marshal.ZeroFreeGlobalAllocUnicode(ptr);
        }
    }

    /// <summary>用明文构造一个 SecureString;null 返回 null。调用方负责释放返回值。</summary>
    public static SecureString? FromPlaintext(string? plaintext)
    {
        if (plaintext is null)
        {
            return null;
        }
        var secure = new SecureString();
        foreach (char c in plaintext)
        {
            secure.AppendChar(c);
        }
        return secure;
    }
}
