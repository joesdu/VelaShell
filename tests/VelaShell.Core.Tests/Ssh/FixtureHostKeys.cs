using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>环回与 Docker 夹具每次启动会生成新钥。只有夹具的本机地址和跳板内的模拟终点可被信任。</summary>
internal static class FixtureHostKeys
{
    public static IHostKeyService Create()
    {
        IHostKeyService store = Substitute.For<IHostKeyService>();
        store.VerifyHostKeyAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<string>(0) is "127.0.0.1" or "target.internal"
                ? HostKeyVerification.Trusted : HostKeyVerification.Changed);
        store.FindKnownHostKeysAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<KnownHost>>([]));
        return store;
    }
}
