using System.Text;
using VelaShell.Core.Data;
using VelaShell.Infrastructure.Persistence;

namespace VelaShell.Infrastructure.Tests;

/// <summary>主密码用例共用的几样东西:内存里的假密钥链、手调的时钟、把数据目录照「开启」的样子包起来的帮手。</summary>
internal static class MasterPasswordTestKit
{
    /// <summary>主密码的 UTF-8 字节(每次给一份新的:被测代码用完会清零)。</summary>
    public static byte[] Utf8(string password) => Encoding.UTF8.GetBytes(password);

    /// <summary>
    /// 不经开启流程,直接把数据目录里每个位置上的明文密钥包起来、写上头 —— 只为让「格式与解包」那几条用例
    /// 不依赖开启的实现。返回生成的恢复码(<paramref name="withRecovery" /> 为 false 时为 null)。
    /// </summary>
    public static string? WrapEverything(MasterKeyResolver resolver, string password, bool withRecovery,
        MasterPasswordHeaderState state = MasterPasswordHeaderState.Active)
    {
        (string Display, byte[] Entropy)? recovery = withRecovery ? RecoveryCode.Generate() : null;
        MasterPasswordHeader header = MasterPasswordHeader.CreateNew(
            withRecovery ? System.Security.Cryptography.RandomNumberGenerator.GetBytes(MasterPasswordHeader.SaltSize) : null, state);
        Assert.IsTrue(resolver.TryWriteHeader(header));
        byte[] kek = MasterPasswordKdf.DerivePasswordKey(Utf8(password), header);
        byte[]? rk = recovery is { } r ? MasterPasswordKdf.DeriveRecoveryKey(r.Entropy, header.RecoverySalt!) : null;
        foreach (StoredKeySlot slot in resolver.ScanSlots(out _))
        {
            if (slot.Value.Length != MasterKeyResolver.KeySize)
            {
                continue;
            }
            List<WrapRecord> records = [MasterKeyEnvelope.Wrap(WrapKind.Password, header.PasswordWrapId, kek, slot.Value)];
            if (rk is not null)
            {
                records.Add(MasterKeyEnvelope.Wrap(WrapKind.Recovery, header.RecoveryWrapId!, rk, slot.Value));
            }
            Assert.IsTrue(resolver.TryWriteSlot(slot.Slot, new MasterKeyEnvelope(records).ToBytes()));
        }
        return recovery?.Display;
    }

    /// <summary>内存里的假密钥链:可以让它「用不了」、拒绝写入、或者从第几次写入开始拒绝。</summary>
    internal sealed class MemoryKeychain : IMasterKeyStore
    {
        public Dictionary<string, byte[]> Entries { get; } = [];

        /// <summary>非 null 时整个密钥链用不了(读写都是)。</summary>
        public string? Unavailable { get; set; }

        /// <summary>大于等于 0 时:再成功写这么多次之后,之后的写入一律拒绝(模拟写到一半断掉)。</summary>
        public int FailSavesAfter { get; set; } = -1;

        /// <summary>大于等于 0 时:再成功写这么多次之后,只拒绝下一次写入(之后照常)—— 模拟写到一半失败、回滚写得进去。</summary>
        public int FailOneSaveAfter { get; set; } = -1;

        public int SaveCount { get; private set; }

        public string Name => "Memory Keychain";

        public MasterKeyStoreResult Load(string keyId)
        {
            if (Unavailable is { } error)
            {
                return MasterKeyStoreResult.Unavailable(error);
            }
            return Entries.TryGetValue(keyId, out byte[]? key) ? MasterKeyStoreResult.Found([.. key]) : MasterKeyStoreResult.Missing;
        }

        public MasterKeyStoreResult Save(string keyId, ReadOnlySpan<byte> key)
        {
            if (Unavailable is { } error)
            {
                return MasterKeyStoreResult.Unavailable(error);
            }
            if (FailOneSaveAfter == 0)
            {
                FailOneSaveAfter = -1;
                return MasterKeyStoreResult.Unavailable("refused once");
            }
            if (FailOneSaveAfter > 0)
            {
                FailOneSaveAfter--;
            }
            if (FailSavesAfter == 0)
            {
                return MasterKeyStoreResult.Unavailable("refused");
            }
            if (FailSavesAfter > 0)
            {
                FailSavesAfter--;
            }
            SaveCount++;
            Entries[keyId] = key.ToArray();
            return MasterKeyStoreResult.Saved;
        }
    }

    /// <summary>手调的时钟(输错的退避按它算)。</summary>
    internal sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;

        public void Advance(TimeSpan by) => Now += by;
    }
}
