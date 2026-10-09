namespace VelaShell.Plugin.Ai.Configuration;

/// <summary>
/// 模型接入与 Key 槽的被动健康记录:失败进冷却,成功即解除。
/// </summary>
/// <remarks>
/// <para>
/// 这里记的是"刚才失败过"这个事实,给故障转移挑下一站时当过滤用 ——
/// 一个刚挂的端点不值得再拿用户这一轮去撞。它<b>不拦用户主动选择</b>:
/// 冷却中的接入照样能从下拉里选、能发消息,只是自动切换先绕开它。
/// </para>
/// <para>
/// 只活在内存里:重启即清。它描述的是"此刻",跨会话记住一次陈年失败
/// 只会让一个早已恢复的端点永远排不上号。
/// </para>
/// </remarks>
public sealed class ProviderHealth
{
    /// <summary>
    /// 失败后的冷却时长。一分钟足够跨过一次抖动,又不至于让一个刚恢复的接入
    /// 在整场对话里都轮不上 —— 下一次谁再用它成功了,冷却就地解除。
    /// </summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);

    /// <summary>失败时刻(<see cref="Environment.TickCount64" />,单调毫秒);没记过就没有这一项。</summary>
    /// <remarks>
    /// 用单调时钟而不是墙上时间:墙钟会被 NTP / 手动回拨,回拨多少秒,冷却就白拖多少秒 ——
    /// "刚才失败过"这个事实不因时钟怎么拨而改变。<see cref="Environment.TickCount64" />
    /// 只会随开机时间前进,回拨免疫。
    /// </remarks>
    private readonly Dictionary<(bool IsKey, string Id), long> _failedAt = new();

    /// <summary>模型与 Key 槽分开记录接受的证据序号,成功也保留以挡住迟到旧失败。</summary>
    private readonly Dictionary<(bool IsKey, string Id), long> _latestObservation = new();
    private long _observationId;
    private readonly Lock _gate = new();

    /// <summary>
    /// 证据代数:每次 <see cref="Clear" />(配置保存、接入增删)前进一代。
    /// </summary>
    private int _version;

    /// <summary>
    /// 当前证据代数 —— 发请求之前记下它,收尾回写时一并交给 <see cref="Record" />。
    /// </summary>
    /// <remarks>
    /// 配置一改,旧证据描述的就是<b>另一份</b>配置:地址 / Key 修正之后,在途的旧请求
    /// 仍可能按同一个模型 id 稍晚失败 —— 带着旧代数回来的写入会被直接丢弃,
    /// 否则刚修好的接入立刻被重新冷却(review⑥)。代数只增不回绕到可用范围内。
    /// </remarks>
    public int Version => Volatile.Read(ref _version);

    /// <summary>请求发出前取序号;不带序号的即时结果在记账时取得新序号。</summary>
    public long BeginObservation()
    {
        lock (_gate)
        {
            return ++_observationId;
        }
    }

    /// <summary>记一次结果:成功解除冷却,失败开始计时。</summary>
    /// <param name="modelId">模型配置 ID;空值直接忽略。</param>
    /// <param name="ok">这次是成(解除)还是败(开始冷却)。</param>
    /// <param name="evidenceVersion">
    /// 这份结果所属的证据代数(<see cref="Version" />,请求发出时记下);与当前代对不上
    /// 就丢弃 —— 那是改配置<b>之前</b>发出去的请求,它的成败描述的是旧配置。
    /// null 表示不校验(纯内存即时结果,或调用方明确不在乎)。
    /// </param>
    /// <param name="observationId">请求起跑时取的序号;null 表示结果完成时才排序。</param>
    public void Record(string? modelId, bool ok, int? evidenceVersion = null, long? observationId = null)
        => RecordCore(modelId, false, ok, evidenceVersion, observationId);

    /// <summary>记录固定 Key 槽的证据;与任何模型 ID 都不共用键空间。</summary>
    public void RecordKey(string? keyId, bool ok, int? evidenceVersion = null, long? observationId = null)
        => RecordCore(keyId, true, ok, evidenceVersion, observationId);

    private void RecordCore(string? modelId, bool isKey, bool ok, int? evidenceVersion, long? observationId)
    {
        if (string.IsNullOrEmpty(modelId))
        {
            return;
        }
        var id = (isKey, modelId);
        lock (_gate)
        {
            if (evidenceVersion is { } version && version != _version)
            {
                return; // 陈旧证据:配置已经换过一轮,这次成败不该落在新配置头上
            }
            long observed = observationId ?? ++_observationId;
            if (_latestObservation.TryGetValue(id, out long latest) && observed < latest)
            {
                return; // 同一记录已有更新的证据,旧请求迟到也不能覆盖
            }
            _latestObservation[id] = observed;
            if (ok)
            {
                _failedAt.Remove(id);
            }
            else
            {
                _failedAt[id] = Environment.TickCount64;
            }
        }
    }

    /// <summary>指定模型是否仍在冷却中。</summary>
    public bool IsCooling(string? modelId)
        => IsCoolingCore(modelId, false);

    /// <summary>指定 Key 槽是否仍在冷却中。</summary>
    public bool IsKeyCooling(string? keyId) => IsCoolingCore(keyId, true);

    private bool IsCoolingCore(string? modelId, bool isKey)
    {
        if (string.IsNullOrEmpty(modelId))
        {
            return false;
        }
        lock (_gate)
        {
            return _failedAt.TryGetValue((isKey, modelId), out long at)
                   && Environment.TickCount64 - at < (long)Cooldown.TotalMilliseconds;
        }
    }

    /// <summary>仅使一个模型的旧配置证据失效,保留其他记录及全局代数。</summary>
    public void Invalidate(string? modelId)
        => InvalidateCore(modelId, false);

    /// <summary>仅使一个 Key 槽的旧配置证据失效。</summary>
    public void InvalidateKey(string? keyId) => InvalidateCore(keyId, true);

    private void InvalidateCore(string? modelId, bool isKey)
    {
        if (string.IsNullOrEmpty(modelId)) return;
        lock (_gate)
        {
            var id = (isKey, modelId);
            _failedAt.Remove(id);
            _latestObservation[id] = ++_observationId;
        }
    }

    /// <summary>
    /// 全部解除。供应商 / 模型配置改了之后调用 —— 旧证据描述的是<b>另一份</b>配置:
    /// 地址、Key、协议都可能换了,记录只按模型或 Key 槽 ID 记,换完配置后仍是同一个 ID,
    /// 留着冷却只会让修好的接入在接下来 60 秒里继续被自动切换绕开。
    /// 放心清:挑下一站时还有切换前探测兜底,真坏的会被再探一次拦住。
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _failedAt.Clear();
            _latestObservation.Clear();
            // 代数跟着前进:此刻还没回来的在途请求,回写时会对不上号被丢弃(见 Version)
            Interlocked.Increment(ref _version);
        }
    }
}
