using System.Diagnostics;
using System.Security.Cryptography;
using VelaShell.Core.Data;

namespace VelaShell.Infrastructure.Persistence;

/// <summary>
/// 开启 / 关闭 / 修改 / 忘记主密码、用恢复码解开后设新主密码、调参后的 rehash。
/// </summary>
/// <remarks>
/// <para>
/// <b>只动包裹,不动密文</b>:每一步都只重写几十字节的包裹与 KDF 头,数据库里的 <c>enc1:</c> 密文一字不改。
/// </para>
/// <para>
/// <b>不丢任何一把密钥</b>:每个位置都是「写新、读回逐字节核对」才算数;任何一步失败,已经写过的位置写回原样的字节
/// (开启的回滚写回明文、关闭的回滚写回包裹)。修改主密码分三步:先给每个位置<b>加上</b>新主密码那一条记录(旧的那条还在)、
/// 再原子地换头、最后删掉旧记录 —— 断在第一步之后旧主密码照样解得开,断在换头之后新主密码解得开,第三步没做完只是多一条用不上的记录
/// (下次解锁时清掉)。开启先落一份标着「开启中」的头再包,关闭先把头标成「关闭中」再写回明文:断在中途,下次启动或解锁时接着做完
/// (见 <see cref="MasterKeyResolver" /> 与 <see cref="AfterPasswordUnlock" />)。
/// </para>
/// <para>
/// 有位置这会儿问不到(系统密钥链用不了)时一律不动手:那个位置是什么样子不知道,动了别的就可能留下一把再也解不开的。
/// </para>
/// </remarks>
internal sealed partial class MasterKeyVault
{
    /// <summary>一个要改写的位置:原样的字节(回滚用)、它装着的那把密钥、它此刻的包裹(明文存着的为 null)。</summary>
    private sealed record SlotPlan(KeySlot Slot, byte[] Original, byte[] Dek, MasterKeyEnvelope? Envelope);

    /// <summary>开启主密码。</summary>
    /// <param name="passwordUtf8">新主密码的 UTF-8 字节(调用方负责清零)。</param>
    /// <param name="withRecoveryCode">同时生成恢复码。</param>
    public MasterPasswordChangeResult Enable(byte[] passwordUtf8, bool withRecoveryCode)
    {
        ArgumentNullException.ThrowIfNull(passwordUtf8);
        lock (_operationGate)
        {
            HashSet<string> ringIds;
            lock (_keysGate)
            {
                if (_status.MasterPasswordEnabled)
                {
                    return new(MasterPasswordChangeStatus.AlreadyEnabled);
                }
                if (_ring is null)
                {
                    return new(MasterPasswordChangeStatus.KeysUnavailable);
                }
                ringIds = [.. _ring.Keys.Select(static k => MasterKeyResolver.KeyIdOf(k))];
            }
            List<StoredKeySlot> stored = _resolver.ScanSlots(out bool complete);
            if (!complete)
            {
                return new(MasterPasswordChangeStatus.KeysUnavailable, Detail: _status.KeychainError);
            }
            List<SlotPlan> plans = [];
            foreach (StoredKeySlot slot in stored)
            {
                if (slot.Value.Length != MasterKeyResolver.KeySize)
                {
                    continue;
                }
                string id = MasterKeyResolver.KeyIdOf(slot.Value);
                if (slot.Slot.KeychainId is { } entryId && entryId != id)
                {
                    continue; // 内容对不上编号:不是我们的,不动
                }
                plans.Add(new(slot.Slot, slot.Value, slot.Value, null));
            }
            // 内存里用着的每一把都得在某个位置上包起来,否则重启之后用它加密的密码就解不开了。
            HashSet<string> planIds = [.. plans.Select(static p => MasterKeyResolver.KeyIdOf(p.Dek))];
            if (plans.Count == 0 || !ringIds.IsSubsetOf(planIds))
            {
                return new(MasterPasswordChangeStatus.KeysUnavailable, Detail: "a key in use is not stored in any slot right now");
            }

            (string Display, byte[] Entropy)? recovery = withRecoveryCode ? RecoveryCode.Generate() : null;
            var header = MasterPasswordHeader.CreateNew(
                withRecoveryCode ? RandomNumberGenerator.GetBytes(MasterPasswordHeader.SaltSize) : null, MasterPasswordHeaderState.Enabling);
            if (!_resolver.TryWriteHeader(header))
            {
                return new(MasterPasswordChangeStatus.StorageFailed, Detail: _resolver.HeaderFilePath);
            }
            byte[] kek = MasterPasswordKdf.DerivePasswordKey(passwordUtf8, header);
            byte[]? recoveryKey = recovery is { } r ? MasterPasswordKdf.DeriveRecoveryKey(r.Entropy, header.RecoverySalt!) : null;
            try
            {
                List<(SlotPlan Plan, MasterKeyEnvelope Envelope, byte[] Bytes)> written = [];
                foreach (SlotPlan plan in plans)
                {
                    List<WrapRecord> records = [MasterKeyEnvelope.Wrap(WrapKind.Password, header.PasswordWrapId, kek, plan.Dek)];
                    if (recoveryKey is not null)
                    {
                        records.Add(MasterKeyEnvelope.Wrap(WrapKind.Recovery, header.RecoveryWrapId!, recoveryKey, plan.Dek));
                    }
                    MasterKeyEnvelope envelope = new(records);
                    byte[] bytes = envelope.ToBytes();
                    if (!_resolver.TryWriteSlot(plan.Slot, bytes))
                    {
                        return RollBackEnable(header, written, plans, failedAt: plan.Slot);
                    }
                    written.Add((plan, envelope, bytes));
                }
                MasterPasswordHeader active = header.WithState(MasterPasswordHeaderState.Active);
                // 标不成「开着」也不要紧:头里写着「开启中」、包裹都在,下次解锁时会标上。
                MasterPasswordHeader final = _resolver.TryWriteHeader(active) ? active : header;
                lock (_keysGate)
                {
                    _sealed = new()
                    {
                        Header = final,
                        Wrapped = [.. written.Select(static w => new WrappedKey(w.Plan.Slot, w.Envelope, w.Bytes))],
                        Plain = []
                    };
                    _status = WithMasterPassword(_status, enabled: true, MasterPasswordProblem.None);
                    _failures = 0;
                    _notBefore = default;
                }
                Trace.WriteLine($"[Secrets] Master password enabled: wrapped {written.Count} key slot(s){(recovery is null ? "" : " with a recovery code")}");
                return new(MasterPasswordChangeStatus.Done, recovery?.Display);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kek);
                if (recoveryKey is not null)
                {
                    CryptographicOperations.ZeroMemory(recoveryKey);
                }
                if (recovery is { } code)
                {
                    CryptographicOperations.ZeroMemory(code.Entropy);
                }
            }
        }
    }

    /// <summary>开启写到一半失败:已经包上的写回明文;都写回去了就删头(回到没开),写不回去就照「开着」交代。</summary>
    private MasterPasswordChangeResult RollBackEnable(MasterPasswordHeader header,
        List<(SlotPlan Plan, MasterKeyEnvelope Envelope, byte[] Bytes)> written, List<SlotPlan> plans, KeySlot failedAt)
    {
        List<(SlotPlan Plan, MasterKeyEnvelope Envelope, byte[] Bytes)> stuck = [];
        foreach ((SlotPlan Plan, MasterKeyEnvelope Envelope, byte[] Bytes) w in written)
        {
            if (!_resolver.TryWriteSlot(w.Plan.Slot, w.Plan.Original))
            {
                stuck.Add(w);
            }
        }
        if (stuck.Count == 0)
        {
            _resolver.TryDeleteHeader();
            Trace.WriteLine($"[Secrets] Enabling the master password failed at {failedAt}; rolled back");
            return new(MasterPasswordChangeStatus.StorageFailed, Detail: failedAt.ToString());
        }
        // 有位置写不回明文:它只能靠刚输入的主密码解开,头(「开启中」)必须留着。按开着处理,下次解锁时把明文那几个也包上。
        lock (_keysGate)
        {
            _sealed = new()
            {
                Header = header,
                Wrapped = [.. stuck.Select(static w => new WrappedKey(w.Plan.Slot, w.Envelope, w.Bytes))],
                Plain = [.. plans.Where(p => !stuck.Exists(s => s.Plan.Slot == p.Slot))
                             .Select(static p => new PlainKey(p.Slot, MasterKeyResolver.KeyIdOf(p.Dek), p.Dek))]
            };
            _status = WithMasterPassword(_status, enabled: true, MasterPasswordProblem.None);
        }
        Trace.WriteLine($"[Secrets] Enabling the master password failed at {failedAt} and {stuck.Count} slot(s) could not be rolled back; it stays on");
        return new(MasterPasswordChangeStatus.StorageFailedPartially, Detail: string.Join(", ", stuck.Select(static s => s.Plan.Slot)));
    }

    /// <summary>关闭主密码:核对当前主密码,写回明文,删头。</summary>
    /// <param name="passwordUtf8">当前主密码(调用方负责清零)。</param>
    public MasterPasswordChangeResult Disable(byte[] passwordUtf8)
    {
        ArgumentNullException.ThrowIfNull(passwordUtf8);
        lock (_operationGate)
        {
            if (PrecheckChange() is { } early)
            {
                return early;
            }
            MasterPasswordHeader header = _sealed!.Header!;
            byte[] kek = MasterPasswordKdf.DerivePasswordKey(passwordUtf8, header);
            List<SlotPlan> plans;
            try
            {
                if (CollectWithPassword(kek, header, out plans) is { } failure)
                {
                    return failure;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kek);
            }
            if (!_resolver.TryWriteHeader(header.WithState(MasterPasswordHeaderState.Disabling)))
            {
                return new(MasterPasswordChangeStatus.StorageFailed, Detail: _resolver.HeaderFilePath);
            }
            List<SlotPlan> written = [];
            foreach (SlotPlan plan in plans.Where(static p => p.Envelope is not null))
            {
                if (!_resolver.TryWriteSlot(plan.Slot, [.. plan.Dek]))
                {
                    bool restored = written.All(w => _resolver.TryWriteSlot(w.Slot, w.Original));
                    if (restored && _resolver.TryWriteHeader(header))
                    {
                        return new(MasterPasswordChangeStatus.StorageFailed, Detail: plan.Slot.ToString());
                    }
                    // 写回包裹也失败了:头留在「关闭中」,下次解锁时接着关完 —— 主密码此刻仍然开着。
                    return new(MasterPasswordChangeStatus.StorageFailedPartially, Detail: plan.Slot.ToString());
                }
                written.Add(plan);
            }
            // 删不掉头也不要紧:头写着「关闭中」而一份包裹都没有了,下次启动就当没开、顺手删掉。
            _resolver.TryDeleteHeader();
            lock (_keysGate)
            {
                ReplaceRing(plans.Select(static p => p.Dek));
                _sealed = null;
                _status = WithMasterPassword(_status, enabled: false, MasterPasswordProblem.None);
                _failures = 0;
                _notBefore = default;
                _unlockedWithRecoveryCode = false;
            }
            Trace.WriteLine($"[Secrets] Master password disabled: {written.Count} key slot(s) back to the step-one form");
            return new(MasterPasswordChangeStatus.Done);
        }
    }

    /// <summary>修改主密码。</summary>
    /// <param name="currentUtf8">当前主密码(调用方负责清零)。</param>
    /// <param name="nextUtf8">新主密码(调用方负责清零)。</param>
    public MasterPasswordChangeResult Change(byte[] currentUtf8, byte[] nextUtf8)
    {
        ArgumentNullException.ThrowIfNull(currentUtf8);
        ArgumentNullException.ThrowIfNull(nextUtf8);
        lock (_operationGate)
        {
            if (PrecheckChange() is { } early)
            {
                return early;
            }
            MasterPasswordHeader header = _sealed!.Header!;
            byte[] kek = MasterPasswordKdf.DerivePasswordKey(currentUtf8, header);
            List<SlotPlan> plans;
            try
            {
                if (CollectWithPassword(kek, header, out plans) is { } failure)
                {
                    return failure;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kek);
            }
            MasterPasswordChangeResult result = RewrapUnderNewHeader(header, nextUtf8, plans, supersededHeaderKept: null);
            if (result.Status == MasterPasswordChangeStatus.Done)
            {
                Trace.WriteLine($"[Secrets] Master password changed ({plans.Count} key slot(s))");
            }
            return result;
        }
    }

    /// <summary>用恢复码解开之后设一个新的主密码(恢复码那一份不动,照样有效)。</summary>
    /// <param name="nextUtf8">新主密码(调用方负责清零)。</param>
    public MasterPasswordChangeResult SetPasswordAfterRecovery(byte[] nextUtf8)
    {
        ArgumentNullException.ThrowIfNull(nextUtf8);
        lock (_operationGate)
        {
            Dictionary<string, byte[]> ringById;
            lock (_keysGate)
            {
                if (!_status.MasterPasswordEnabled)
                {
                    return new(MasterPasswordChangeStatus.NotEnabled);
                }
                if (!_unlockedWithRecoveryCode || _ring is null || _sealed?.Header is null)
                {
                    return new(MasterPasswordChangeStatus.NotRecovered);
                }
                ringById = _ring.Keys.ToDictionary(static k => MasterKeyResolver.KeyIdOf(k), static k => k.ToArray());
            }
            MasterPasswordHeader header = _sealed.Header;
            List<StoredKeySlot> stored = _resolver.ScanSlots(out bool complete);
            if (!complete)
            {
                return new(MasterPasswordChangeStatus.KeysUnavailable, Detail: _status.KeychainError);
            }
            List<SlotPlan> plans = [];
            bool leftBehind = false;
            foreach (StoredKeySlot slot in stored)
            {
                if (MasterKeyEnvelope.TryParse(slot.Value, out MasterKeyEnvelope? envelope))
                {
                    string? id = slot.Slot.KeychainId ?? _openedSlotIds.GetValueOrDefault(slot.Slot);
                    if (id is not null && ringById.TryGetValue(id, out byte[]? dek))
                    {
                        plans.Add(new(slot.Slot, slot.Value, dek, envelope));
                    }
                    else
                    {
                        leftBehind = true; // 恢复码解不开的那一份(没有恢复码那条记录):这次包不了
                    }
                }
                else if (slot.Value.Length == MasterKeyResolver.KeySize
                         && (slot.Slot.KeychainId is null || slot.Slot.KeychainId == MasterKeyResolver.KeyIdOf(slot.Value)))
                {
                    plans.Add(new(slot.Slot, slot.Value, slot.Value, null));
                }
            }
            if (plans.Count == 0)
            {
                return new(MasterPasswordChangeStatus.KeysUnavailable);
            }
            // 有恢复码解不开的那一份:换头之后旧主密码就再也解不开它了 —— 旧头改名保留,想起旧主密码还有得救。
            string? superseded = leftBehind ? KeepSupersededHeader() : null;
            MasterPasswordChangeResult result = RewrapUnderNewHeader(header, nextUtf8, plans, superseded);
            if (result.Status == MasterPasswordChangeStatus.Done)
            {
                lock (_keysGate)
                {
                    _unlockedWithRecoveryCode = false;
                }
                Trace.WriteLine($"[Secrets] A new master password was set after unlocking with the recovery code ({plans.Count} key slot(s))");
            }
            foreach (byte[] dek in ringById.Values)
            {
                CryptographicOperations.ZeroMemory(dek);
            }
            return result;
        }
    }

    /// <summary>
    /// 忘记主密码:头、清单、密钥文件改名保留,另起新的本机加密密钥(主密码随之关掉)。密钥链条目原样不动。
    /// </summary>
    public MasterPasswordChangeResult Forget()
    {
        lock (_operationGate)
        {
            IReadOnlyList<string> kept;
            try
            {
                kept = _resolver.SetAsideForForgottenPassword();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.WriteLine($"[Secrets] Could not set the forgotten master password aside: {ex.Message}");
                return new(MasterPasswordChangeStatus.StorageFailed, Detail: ex.Message);
            }
            MasterKeyResolution resolution = _resolver.Resolve();
            lock (_keysGate)
            {
                _ring?.Zero();
                _ring = resolution.Ring;
                _sealed = resolution.Sealed;
                _status = resolution.Status;
                _failures = 0;
                _notBefore = default;
                _unlockedWithRecoveryCode = false;
                _openedSlotIds.Clear();
            }
            string detail = string.Join(", ", kept.Select(Path.GetFileName));
            Trace.WriteLine($"[Secrets] Forgot the master password: kept {detail}; started a new local encryption key");
            return resolution.Ring is null
                ? new(MasterPasswordChangeStatus.StorageFailed, Detail: detail)
                : new(MasterPasswordChangeStatus.Done, Detail: detail);
        }
    }

    /// <summary>关闭、修改之前的早退:没开、要等、没有头。</summary>
    private MasterPasswordChangeResult? PrecheckChange()
    {
        lock (_keysGate)
        {
            if (!_status.MasterPasswordEnabled)
            {
                return new(MasterPasswordChangeStatus.NotEnabled);
            }
            TimeSpan left = _notBefore - _time.GetUtcNow();
            if (left > TimeSpan.Zero)
            {
                return new(MasterPasswordChangeStatus.Throttled, RetryAfter: left);
            }
            return _sealed?.Header is null ? new(MasterPasswordChangeStatus.KeysUnavailable) : null;
        }
    }

    /// <summary>
    /// 现读一遍每个位置,用主密码的 KEK 解开每一份包裹(每一份都得解得开),明文存着的那几把一并带上。
    /// </summary>
    /// <returns>都解开了为 null;否则是交给调用方的失败(主密码不对按一次输错计)。</returns>
    private MasterPasswordChangeResult? CollectWithPassword(byte[] kek, MasterPasswordHeader header, out List<SlotPlan> plans)
    {
        plans = [];
        List<StoredKeySlot> stored = _resolver.ScanSlots(out bool complete);
        if (!complete)
        {
            return new(MasterPasswordChangeStatus.KeysUnavailable, Detail: _status.KeychainError);
        }
        int wrapped = 0;
        int opened = 0;
        foreach (StoredKeySlot slot in stored)
        {
            if (MasterKeyEnvelope.TryParse(slot.Value, out MasterKeyEnvelope? envelope))
            {
                wrapped++;
                if (envelope!.Find(WrapKind.Password, header.PasswordWrapId) is not { } record
                    || !MasterKeyEnvelope.TryUnwrap(record, kek, out byte[] dek))
                {
                    continue;
                }
                if (slot.Slot.KeychainId is { } entryId && entryId != MasterKeyResolver.KeyIdOf(dek))
                {
                    CryptographicOperations.ZeroMemory(dek);
                    continue;
                }
                opened++;
                plans.Add(new(slot.Slot, slot.Value, dek, envelope));
            }
            else if (slot.Value.Length == MasterKeyResolver.KeySize
                     && (slot.Slot.KeychainId is null || slot.Slot.KeychainId == MasterKeyResolver.KeyIdOf(slot.Value)))
            {
                plans.Add(new(slot.Slot, slot.Value, slot.Value, null)); // 主密码开着却明文存着的那把:一并处理
            }
        }
        if (wrapped == 0)
        {
            return new(MasterPasswordChangeStatus.KeysUnavailable);
        }
        if (opened == 0)
        {
            MasterPasswordUnlockResult failure = RecordFailure();
            return new(MasterPasswordChangeStatus.WrongPassword, RetryAfter: failure.RetryAfter);
        }
        if (opened < wrapped)
        {
            // 主密码对,却有一份包裹解不开(条目被换过、修改主密码留下的残局):动手的话那一份就再也解不开了。
            return new(MasterPasswordChangeStatus.StorageFailed, Detail: "a wrapped key cannot be opened with this master password");
        }
        lock (_keysGate)
        {
            _failures = 0;
            _notBefore = default;
        }
        return null;
    }

    /// <summary>
    /// 换一份新头(新盐、出厂参数、恢复码盐沿用)重新包裹:①每个位置加上新主密码那一条 ②原子换头 ③删掉旧的那条。
    /// ①② 任一处失败都写回原样的字节、头不动(旧主密码照样有效)。
    /// </summary>
    private MasterPasswordChangeResult RewrapUnderNewHeader(MasterPasswordHeader current, byte[] nextUtf8, List<SlotPlan> plans,
        string? supersededHeaderKept)
    {
        var next = MasterPasswordHeader.CreateNew(current.RecoverySalt, MasterPasswordHeaderState.Active);
        byte[] kek = MasterPasswordKdf.DerivePasswordKey(nextUtf8, next);
        try
        {
            // ① 加上新的一条(旧的那条、恢复码那条都留着)。
            List<(SlotPlan Plan, MasterKeyEnvelope Envelope)> step1 = [];
            foreach (SlotPlan plan in plans)
            {
                List<WrapRecord> records = [.. plan.Envelope?.Records ?? []];
                records.RemoveAll(r => r.Kind == WrapKind.Password && r.WrapId.AsSpan().SequenceEqual(next.PasswordWrapId));
                records.Add(MasterKeyEnvelope.Wrap(WrapKind.Password, next.PasswordWrapId, kek, plan.Dek));
                MasterKeyEnvelope envelope = new(records.Count > 8 ? records[^8..] : records);
                if (!_resolver.TryWriteSlot(plan.Slot, envelope.ToBytes()))
                {
                    RestoreOriginals(step1.Select(static s => s.Plan));
                    return new(MasterPasswordChangeStatus.StorageFailed, Detail: plan.Slot.ToString());
                }
                step1.Add((plan, envelope));
            }
            // ② 换头:这一刻起新主密码生效。
            if (!_resolver.TryWriteHeader(next))
            {
                RestoreOriginals(step1.Select(static s => s.Plan));
                return new(MasterPasswordChangeStatus.StorageFailed, Detail: _resolver.HeaderFilePath);
            }
            // ③ 删掉编号对不上新头的旧记录(做不完也不要紧:下次解锁时清)。
            List<WrappedKey> final = [];
            foreach ((SlotPlan plan, MasterKeyEnvelope envelope) in step1)
            {
                MasterKeyEnvelope cleaned = new([.. envelope.Records.Where(r => IsCurrent(r, next))]);
                byte[] bytes = cleaned.ToBytes();
                final.Add(_resolver.TryWriteSlot(plan.Slot, bytes)
                    ? new(plan.Slot, cleaned, bytes)
                    : new(plan.Slot, envelope, envelope.ToBytes()));
            }
            lock (_keysGate)
            {
                ReplaceRing(plans.Select(static p => p.Dek));
                _sealed = new() { Header = next, Wrapped = final, Plain = [] };
                _status = WithMasterPassword(_status, enabled: true, MasterPasswordProblem.None);
                _failures = 0;
                _notBefore = default;
            }
            return new(MasterPasswordChangeStatus.Done, Detail: supersededHeaderKept);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    private void RestoreOriginals(IEnumerable<SlotPlan> written)
    {
        foreach (SlotPlan plan in written)
        {
            if (!_resolver.TryWriteSlot(plan.Slot, plan.Original))
            {
                // 写不回去也不丢:那个位置多了一条新记录,旧的那条还在,旧主密码照样解得开。
                Trace.WriteLine($"[Secrets] {plan.Slot} keeps an extra record after a failed change; the current master password still opens it");
            }
        }
    }

    /// <summary>把当前的头复制一份改名保留(<c>secret.key.mp.superseded-时间戳</c>),返回文件名;复制不了为 null。</summary>
    private string? KeepSupersededHeader()
    {
        string aside = $"{_resolver.HeaderFilePath}.superseded-{DateTime.UtcNow:yyyyMMddHHmmss}";
        try
        {
            File.Copy(_resolver.HeaderFilePath, aside, overwrite: false);
            return aside;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[Secrets] Could not keep the superseded master password header: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 主密码解开之后,头的参数不是出厂参数(以后调了参):用同一个主密码、新盐、新参数重新包裹。尽力而为,失败只记日志。
    /// </summary>
    private void TryRehash(byte[] passwordUtf8, MasterPasswordHeader header, byte[] kek)
    {
        if (header.UsesCurrentParameters || header.State != MasterPasswordHeaderState.Active)
        {
            return;
        }
        if (CollectWithPassword(kek, header, out List<SlotPlan> plans) is null)
        {
            MasterPasswordChangeResult result = RewrapUnderNewHeader(header, passwordUtf8, plans, supersededHeaderKept: null);
            Trace.WriteLine($"[Secrets] Re-wrapped the local encryption keys with the current key-derivation parameters: {result.Status}");
        }
    }

    /// <summary>把内存里的密钥串换成 <paramref name="keys" />(去重、复制;旧的清零)。调用方持有 <see cref="_keysGate" />。</summary>
    private void ReplaceRing(IEnumerable<byte[]> keys)
    {
        List<byte[]> distinct = [];
        HashSet<string> ids = [];
        foreach (byte[] key in keys)
        {
            if (ids.Add(MasterKeyResolver.KeyIdOf(key)))
            {
                distinct.Add([.. key]);
            }
        }
        if (_ring is { } old)
        {
            // 内存里那串解着的密钥(含这次没在任何位置上看到的)排在后面,一把都不少。
            foreach (byte[] key in old.Keys)
            {
                if (ids.Add(MasterKeyResolver.KeyIdOf(key)))
                {
                    distinct.Add([.. key]);
                }
            }
            old.Zero();
        }
        if (distinct.Count > 0)
        {
            _ring = new(distinct, _status);
        }
    }
}
