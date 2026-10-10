using VelaShell.Splash;

namespace VelaShell.Tests.Splash;

[TestClass]
public sealed class SplashProgressTests
{
    [TestMethod]
    public void NoMarks_FirstStageRunsAndTheRestWait()
    {
        SplashStage[] stages = SplashProgressTracker.Compute(new double?[5], nowMs: 300);

        Assert.AreEqual(SplashStageState.Running, stages[0].State);
        Assert.AreEqual(0.3, stages[0].Seconds, 1e-9);
        Assert.IsTrue(stages.Skip(1).All(s => s.State == SplashStageState.Waiting));
    }

    [TestMethod]
    public void DoneStages_ReportTheirOwnDuration()
    {
        SplashStage[] stages = SplashProgressTracker.Compute([120, 250, 900, null, null], nowMs: 1000);

        Assert.AreEqual(SplashStageState.Done, stages[0].State);
        Assert.AreEqual(0.12, stages[0].Seconds, 1e-9);
        Assert.AreEqual(0.13, stages[1].Seconds, 1e-9);
        Assert.AreEqual(0.65, stages[2].Seconds, 1e-9);
        Assert.AreEqual(SplashStageState.Running, stages[3].State);
        Assert.AreEqual(0.1, stages[3].Seconds, 1e-9, "进行中的阶段从上一段结束时算起。");
        Assert.AreEqual(SplashStageState.Waiting, stages[4].State);
    }

    [TestMethod]
    public void DatabaseFinishingAfterTheInterface_IsClosedByTheLaterMark()
    {
        // 数据库在后台与界面框架并行打开,它的点(1800)可能比界面框架的点(1500)还晚。
        // 后面的阶段都完成了,前面的不可能还没完成:数据库那一段按 1500 结束,耗时不会是负数。
        SplashStage[] stages = SplashProgressTracker.Compute([100, 1800, 1500, null, null], nowMs: 1600);

        Assert.AreEqual(SplashStageState.Done, stages[1].State);
        Assert.AreEqual(1.4, stages[1].Seconds, 1e-9);
        Assert.AreEqual(SplashStageState.Done, stages[2].State);
        Assert.AreEqual(0, stages[2].Seconds, 1e-9);
        Assert.AreEqual(SplashStageState.Running, stages[3].State);
        Assert.IsTrue(stages.All(s => s.Seconds >= 0));
    }

    [TestMethod]
    public void OnlyTheFinalMark_CompletesEverything()
    {
        SplashStage[] stages = SplashProgressTracker.Compute([null, null, null, null, 2000], nowMs: 2100);

        Assert.IsTrue(stages.All(s => s.State == SplashStageState.Done));
        Assert.AreEqual(1, SplashProgressTracker.RawProgress(stages), 1e-9);
    }

    [TestMethod]
    public void Estimate_RisesMonotonicallyButNeverFinishes()
    {
        double previous = -1;
        foreach (double elapsed in new[] { 0.0, 100, 500, 1500, 5000, 60_000 })
        {
            double estimate = SplashProgressTracker.Estimate(elapsed, 500);
            Assert.IsGreaterThanOrEqualTo(previous, estimate);
            Assert.IsLessThanOrEqualTo(0.95, estimate, "进行中的阶段不能报完成:不然进度条走满了画面还停在那儿。");
            previous = estimate;
        }
    }

    [TestMethod]
    public void Tracker_IgnoresUnknownMarksAndKeepsTheEarliest()
    {
        SplashProgressTracker tracker = new();
        tracker.Record("Main", TimeSpan.FromMilliseconds(120));
        tracker.Record("SomethingElse", TimeSpan.FromMilliseconds(130));
        tracker.Record("Settings", TimeSpan.FromMilliseconds(900));
        tracker.Record("DbWarmup", TimeSpan.FromMilliseconds(300));

        SplashFrame frame = tracker.Snapshot(nowMs: 1000, shownAtMs: 150);

        Assert.AreEqual(850, frame.ElapsedMs, 1e-9);
        Assert.AreEqual(0.18, frame.Stages[1].Seconds, 1e-9, "数据库那段取两个点里较早的 DbWarmup。");
        Assert.AreEqual(2, frame.Current);
    }

    [TestMethod]
    public void Simulation_StartsWithRuntimeDoneAndEndsAllDone()
    {
        SplashSimulation simulation = new();

        SplashFrame first = simulation.At(0);
        Assert.AreEqual(SplashStageState.Done, first.Stages[0].State, "画面出现时运行时那一段已经结束。");
        Assert.AreEqual(1, first.Current);

        SplashFrame last = simulation.At(SplashSimulation.CycleMs - 1);
        Assert.IsTrue(last.IsDone);
        Assert.AreEqual(-1, last.Current);
    }

    [TestMethod]
    public void Simulation_ProgressNeverGoesBackwardWithinACycle()
    {
        SplashSimulation simulation = new();
        double previous = 0;
        for (double t = 0; t < SplashSimulation.CycleMs; t += 16)
        {
            double progress = simulation.At(t).Progress;
            Assert.IsGreaterThanOrEqualTo(previous - 1e-9, progress, $"t={t}");
            previous = progress;
        }
        Assert.IsGreaterThan(0.95, previous);
    }

    [TestMethod]
    public void Smoother_EasesTowardTargetAndSnapsBackOnRestart()
    {
        ProgressSmoother smoother = new();
        Assert.AreEqual(0.2, smoother.Next(0.2, 0), 1e-9, "第一帧直接取目标值。");

        double eased = smoother.Next(1, 16);
        Assert.IsTrue(eased is > 0.2 and < 1, $"一帧之内只走一段:{eased}");
        Assert.AreEqual(1, smoother.Next(1, 2000), 1e-3);

        Assert.AreEqual(0.1, smoother.Next(0.1, 2016), 1e-9, "预览循环重播时直接落回去,不做倒退动画。");
    }
}
