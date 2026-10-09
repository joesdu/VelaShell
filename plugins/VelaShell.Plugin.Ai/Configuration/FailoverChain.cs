namespace VelaShell.Plugin.Ai.Configuration;

/// <summary>
/// 故障转移链上的一站:只存模型 id,顺序即优先顺序(越靠前越先试)。
/// </summary>
/// <remarks>
/// 用户排的链可以跨供应商、跨模型(先走这家的 4o,再走那家的 claude),
/// 所以这一站不假设与当前模型有任何相似之处 —— "像不像、值不值得换"由用户自己在列表里表达。
/// 列表为空或全部条目都已失效时退回自动发现,见 <see cref="AiSettings.FailoverCandidates" />。
/// </remarks>
public sealed class FailoverEntry
{
    /// <summary>目标模型配置 id(见 <see cref="AiModelConfig.Id" />)。</summary>
    public string ModelId { get; set; } = "";
}
