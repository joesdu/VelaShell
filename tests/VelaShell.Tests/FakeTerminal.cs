using System.Text;
using NSubstitute;
using VelaShell.Terminal;

namespace VelaShell.Tests;

/// <summary><see cref="ITerminalEmulator" /> 的测试替身工厂。</summary>
/// <remarks>
/// 裸替身的每个属性都返回默认值,<see cref="ITerminalEmulator.SessionEncoding" /> 于是是 null。
/// 而宿主的输入路径(快捷命令下发、同步频道转发、命令行跟踪)现在都按会话字符集编码 ——
/// 让每个用例各配一遍迟早会漏,漏掉的那条会以一句
/// <c>Value cannot be null (Parameter 'encoding')</c> 停在生产代码深处,离真正的原因很远。
/// 因此替身一律从这里造,默认就是一台 UTF-8 终端。
/// </remarks>
internal static class FakeTerminal
{
    /// <summary>造一个行为像 UTF-8 终端的替身。</summary>
    /// <returns>已配好会话字符集的替身。</returns>
    public static ITerminalEmulator Emulator()
    {
        ITerminalEmulator emulator = Substitute.For<ITerminalEmulator>();
        emulator.SessionEncoding.Returns(Encoding.UTF8);
        return emulator;
    }
}
