using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 被动健康记录:失败进冷却、成功即解除,只影响 failover 候选的过滤。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class ProviderHealthTests
{
    [TestMethod]
    public void Review_KeyAndModelEvidenceNeverShareAStringNamespace()
    {
        var health = new ProviderHealth();
        const string keyId = "slot";
        const string modelId = "key:slot";
        health.Record(modelId, false);
        Assert.IsTrue(health.IsCooling(modelId));
        Assert.IsFalse(health.IsKeyCooling(keyId));
        health.RecordKey(keyId, false);
        health.Record(modelId, true);
        Assert.IsFalse(health.IsCooling(modelId));
        Assert.IsTrue(health.IsKeyCooling(keyId));
        health.Record(modelId, false);
        health.InvalidateKey(keyId);
        Assert.IsTrue(health.IsCooling(modelId));
        Assert.IsFalse(health.IsKeyCooling(keyId));
    }

    [TestMethod]
    public void FailureStartsCoolingAndSuccessLiftsIt()
    {
        var health = new ProviderHealth();
        Assert.IsFalse(health.IsCooling("a"), "没记过失败就不该冷却");

        health.Record("a", ok: false);
        Assert.IsTrue(health.IsCooling("a"), "刚失败的要进冷却,别马上再撞");

        health.Record("a", ok: true);
        Assert.IsFalse(health.IsCooling("a"), "成功一次就解除");
    }

    [TestMethod]
    public void RecordsArePerModel()
    {
        var health = new ProviderHealth();
        health.Record("a", ok: false);
        health.Record("b", ok: true);

        Assert.IsTrue(health.IsCooling("a"));
        Assert.IsFalse(health.IsCooling("b"), "各记各的,b 成不成功与 a 无关");
        Assert.IsFalse(health.IsCooling("c"), "没记过的 id 不该被误伤");
    }

    [TestMethod]
    public void NullAndEmptyIdsAreIgnored()
    {
        var health = new ProviderHealth();
        health.Record(null, ok: false);
        health.Record("", ok: false);
        Assert.IsFalse(health.IsCooling(null), "空 id 不记也不查,不抛也不误判");
        Assert.IsFalse(health.IsCooling(""));
    }

    [TestMethod]
    public void EvidenceFromBeforeAClear_IsDiscarded()
    {
        // 配置保存会 Clear 一次:地址 / Key 都可能换了,而记录只按模型 id 记 ——
        // 在途的旧请求稍晚回来,带着旧代数的写入必须被丢弃,否则刚修好的
        // 接入立刻被重新冷却(它描述的是改之前的那份配置)。
        var health = new ProviderHealth();
        int evidence = health.Version;
        health.Record("a", ok: false, evidenceVersion: evidence);
        Assert.IsTrue(health.IsCooling("a"), "前提:当前代的证据照常记");

        health.Clear();
        Assert.IsFalse(health.IsCooling("a"), "配置改了,旧冷却不该留着");
        health.Record("a", ok: false, evidenceVersion: evidence);
        Assert.IsFalse(health.IsCooling("a"),
            "在途旧请求带旧代数回来:不许把修正过的新配置重新冷却");

        health.Record("a", ok: false); // 当前代、不校验的写入照常生效
        Assert.IsTrue(health.IsCooling("a"));
    }

    [TestMethod]
    public void LateManualResultCannotUndoNewerChatEvidence()
    {
        var health = new ProviderHealth();
        long manual = health.BeginObservation();
        health.Record("a", ok: false);
        health.Record("a", ok: true); // 聊天成功在手测等待期间完成
        health.Record("a", ok: false, observationId: manual);
        Assert.IsFalse(health.IsCooling("a"), "手测旧失败不能重新冷却聊天已经答通的模型");

        long newerManual = health.BeginObservation();
        health.Record("a", ok: false, observationId: newerManual);
        Assert.IsTrue(health.IsCooling("a"), "后起同代手测失败仍应冷却");
        health.Record("a", ok: true, observationId: manual);
        Assert.IsTrue(health.IsCooling("a"), "旧成功也不能解除更新的失败");
    }

    [TestMethod]
    public void ObservationsAreIndependentPerModelAndConfigurationGeneration()
    {
        var health = new ProviderHealth();
        int oldVersion = health.Version;
        long old = health.BeginObservation();
        long newer = health.BeginObservation();
        health.Record("a", ok: true, evidenceVersion: oldVersion, observationId: newer);
        health.Record("b", ok: false, evidenceVersion: oldVersion, observationId: old);
        Assert.IsTrue(health.IsCooling("b"), "a 的较新证据不能抹掉 b 的旧序号");

        health.Clear();
        Assert.IsFalse(health.IsCooling("b"));
        health.Record("a", ok: false, evidenceVersion: oldVersion, observationId: newer);
        Assert.IsFalse(health.IsCooling("a"), "旧配置证据不能作用到新代");
        health.Record("a", ok: false, evidenceVersion: health.Version, observationId: newer);
        Assert.IsTrue(health.IsCooling("a"), "清空也要清模型最近序号,新代不受旧代挡住");
    }

    [TestMethod]
    public void InvalidateOneModel_RejectsOldEvidenceAndPreservesOtherModels()
    {
        var health = new ProviderHealth();
        int version = health.Version;
        long edited = health.BeginObservation();
        long other = health.BeginObservation();
        health.Record("edited", false, version, edited);
        health.Record("other", false, version, other);

        health.Invalidate("edited");
        Assert.IsFalse(health.IsCooling("edited"));
        Assert.IsTrue(health.IsCooling("other"), "目录修改一个模型不能抹掉其他接入冷却");
        Assert.AreEqual(version, health.Version, "选择性失效不推进全局代数");
        health.Record("edited", false, version, edited);
        Assert.IsFalse(health.IsCooling("edited"), "旧在途失败不能重新冷却已更换配置的模型");
        health.Record("other", true, version, other);
        Assert.IsFalse(health.IsCooling("other"), "未变模型的在途证据仍有效");

        long fresh = health.BeginObservation();
        health.Record("edited", false, version, fresh);
        Assert.IsTrue(health.IsCooling("edited"), "新配置的新失败照常进入冷却");
        health.Record("edited", true, version, edited);
        Assert.IsTrue(health.IsCooling("edited"), "旧成功也不能解除新配置的失败");
        health.Record("edited", true, version, health.BeginObservation());
        Assert.IsFalse(health.IsCooling("edited"));
    }
    [TestMethod]
    public void EarlierChatSuccessCannotUndoLaterStartedFailure()
    {
        var health = new ProviderHealth();
        int version = health.Version;
        long earlierChat = health.BeginObservation();
        long laterChat = health.BeginObservation();
        health.Record("shared-model", false, version, laterChat);
        health.Record("shared-model", true, version, earlierChat);
        Assert.IsTrue(health.IsCooling("shared-model"),
            "先起请求迟到成功不覆盖后起请求已经证明的失败，保持按起跑序号裁决");

        health.Record("other-model", true, version, earlierChat);
        Assert.IsFalse(health.IsCooling("other-model"), "共享模型裁决不影响其他模型");
        health.Record("shared-model", true, version, health.BeginObservation());
        Assert.IsFalse(health.IsCooling("shared-model"), "更晚发起的成功仍应解除冷却");
    }
}
