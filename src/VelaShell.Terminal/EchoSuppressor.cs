using System;
using System.IO;

namespace VelaShell.Terminal;

/// <summary>
/// 一次性回显抑制器:连接初始化命令是作为击键注入远端 shell 的,PTY 会把它原样回显
/// (可能出现两次:内核规范模式回显 + readline 预输入重绘),用户要求静默执行。
/// 本类在时间窗内从输出流里剥除该命令的回显字节;回显可能被网络分块任意切开,
/// 因此做跨块流式匹配:块尾的部分命中(≥<see cref="MinHold"/> 字节,避免把提示符
/// 尾部空格之类的巧合扣住)先扣下,下一块续判。超时或命中数用尽后放行一切。
/// </summary>
public sealed class EchoSuppressor
{
    /// <summary>块尾部分命中至少要匹配这么多字节才扣住等下一块,防止普通输出被延迟显示。</summary>
    private const int MinHold = 4;

    private readonly byte[] _needle;
    private readonly DateTime _deadline;
    private int _hitsLeft;
    private int _held; // 上一块末尾已匹配的 needle 前缀长度(字节内容即 needle[.._held])

    public EchoSuppressor(byte[] needle, int maxHits, TimeSpan window)
    {
        if (needle.Length < MinHold)
            throw new ArgumentException("Needle too short to suppress safely.", nameof(needle));

        _needle = needle;
        _hitsLeft = maxHits;
        _deadline = DateTime.UtcNow + window;
    }

    public bool Expired => _hitsLeft <= 0 || DateTime.UtcNow > _deadline;

    public byte[] Process(byte[] data)
    {
        if (Expired)
            return ReleaseHeld(data);

        // 把上次扣住的前缀接回输入统一扫描(其内容与 needle 前缀相同,可直接重建)。
        byte[] input;
        if (_held > 0)
        {
            input = new byte[_held + data.Length];
            Array.Copy(_needle, 0, input, 0, _held);
            Array.Copy(data, 0, input, _held, data.Length);
            _held = 0;
        }
        else
        {
            input = data;
        }

        var output = new MemoryStream(input.Length);
        int i = 0;
        while (i < input.Length)
        {
            if (_hitsLeft > 0 && input[i] == _needle[0])
            {
                int matched = MatchFrom(input, i);
                if (matched == _needle.Length)
                {
                    // 整段命中:剥除。
                    i += matched;
                    _hitsLeft--;
                    continue;
                }

                if (matched > 0 && i + matched == input.Length && matched >= MinHold)
                {
                    // 块尾部分命中:扣住,等下一块续判。
                    _held = matched;
                    i += matched;
                    continue;
                }
            }

            output.WriteByte(input[i]);
            i++;
        }

        return output.ToArray();
    }

    /// <summary>从 <paramref name="start"/> 起与 needle 连续匹配的字节数;首个不匹配即返回 0..k。
    /// 返回值等于可用长度表示"到块尾都还在匹配"。</summary>
    private int MatchFrom(byte[] input, int start)
    {
        int limit = Math.Min(_needle.Length, input.Length - start);
        for (int k = 0; k < limit; k++)
        {
            if (input[start + k] != _needle[k])
                return 0;
        }

        return limit;
    }

    private byte[] ReleaseHeld(byte[] data)
    {
        if (_held == 0)
            return data;

        var result = new byte[_held + data.Length];
        Array.Copy(_needle, 0, result, 0, _held);
        Array.Copy(data, 0, result, _held, data.Length);
        _held = 0;
        return result;
    }
}
