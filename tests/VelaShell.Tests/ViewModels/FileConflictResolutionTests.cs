using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 批量传输冲突的粘性决定(<see cref="FileBrowserViewModel.DecideConflictAsync" />):
/// “全部覆盖/全部跳过”只问一次即沿用到本批次其余所有冲突,免去逐文件弹窗
/// (拖入上千文件的文件夹与服务端冲突时的关键)。
/// </summary>
[TestClass]
[TestCategory("FileBrowser")]
public class FileConflictResolutionTests
{
    private static Func<string, Task<FileConflictResolution>> Always(
        FileConflictResolution answer,
        Action onCall
    ) =>
        _ =>
        {
            onCall();
            return Task.FromResult(answer);
        };

    [TestMethod]
    public async Task OverwriteAll_AnsweredOnce_AppliesToRemainingWithoutReprompting()
    {
        var decision = new FileBrowserViewModel.BatchConflictDecision();
        int prompts = 0;
        Func<string, Task<FileConflictResolution>> confirm = Always(
            FileConflictResolution.OverwriteAll,
            () => prompts++
        );

        // 首个冲突弹一次窗;其余 999 个直接沿用,不再弹。
        bool first = await FileBrowserViewModel.DecideConflictAsync("/r/f0", confirm, decision);
        Assert.IsTrue(first);
        for (int i = 1; i < 1000; i++)
        {
            Assert.IsTrue(await FileBrowserViewModel.DecideConflictAsync($"/r/f{i}", confirm, decision));
        }
        Assert.AreEqual(1, prompts, "全部覆盖应只弹一次窗");
    }

    [TestMethod]
    public async Task SkipAll_AnsweredOnce_SkipsRemainingWithoutReprompting()
    {
        var decision = new FileBrowserViewModel.BatchConflictDecision();
        int prompts = 0;
        Func<string, Task<FileConflictResolution>> confirm = Always(
            FileConflictResolution.SkipAll,
            () => prompts++
        );

        bool first = await FileBrowserViewModel.DecideConflictAsync("/r/f0", confirm, decision);
        Assert.IsFalse(first);
        for (int i = 1; i < 1000; i++)
        {
            Assert.IsFalse(await FileBrowserViewModel.DecideConflictAsync($"/r/f{i}", confirm, decision));
        }
        Assert.AreEqual(1, prompts, "全部跳过应只弹一次窗");
    }

    [TestMethod]
    public async Task SingleOverwrite_DoesNotStick_RepromptsEachConflict()
    {
        var decision = new FileBrowserViewModel.BatchConflictDecision();
        int prompts = 0;
        Func<string, Task<FileConflictResolution>> confirm = Always(
            FileConflictResolution.Overwrite,
            () => prompts++
        );

        for (int i = 0; i < 5; i++)
        {
            Assert.IsTrue(await FileBrowserViewModel.DecideConflictAsync($"/r/f{i}", confirm, decision));
        }
        Assert.AreEqual(5, prompts, "单个覆盖不应设置粘性,应逐个询问");
        Assert.IsNull(decision.OverwriteAll);
    }

    [TestMethod]
    public async Task SingleSkip_DoesNotStick_RepromptsEachConflict()
    {
        var decision = new FileBrowserViewModel.BatchConflictDecision();
        int prompts = 0;
        Func<string, Task<FileConflictResolution>> confirm = Always(
            FileConflictResolution.Skip,
            () => prompts++
        );

        for (int i = 0; i < 5; i++)
        {
            Assert.IsFalse(await FileBrowserViewModel.DecideConflictAsync($"/r/f{i}", confirm, decision));
        }
        Assert.AreEqual(5, prompts, "单个跳过不应设置粘性,应逐个询问");
        Assert.IsNull(decision.OverwriteAll);
    }

    [TestMethod]
    public async Task NullConfirm_DefaultsToOverwrite_NeverPrompts()
    {
        var decision = new FileBrowserViewModel.BatchConflictDecision();
        // 无 UI 回调(理论边界):保持既有行为——默认覆盖,不阻塞。
        Assert.IsTrue(await FileBrowserViewModel.DecideConflictAsync("/r/f", null, decision));
        Assert.IsNull(decision.OverwriteAll);
    }
}
