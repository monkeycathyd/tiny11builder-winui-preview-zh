using System.Runtime.Versioning;
using Tiny11Builder.App;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

// Drives the real engine through the existing fake process runner and feeds its progress into the
// same BuildStageBoard the window uses, so these tests cover the shipped grouping and counting code.
[SupportedOSPlatform("windows")]
public sealed class AppChoiceTests
{
    private sealed record Run(BuildStageBoard Board, Exception? Error, List<BuildProgress> Events);

    private static async Task<Run> RunAsync(BuildFixture fixture, BuildRecipe recipe, CancellationToken token = default)
    {
        var board = new BuildStageBoard();
        var events = new List<BuildProgress>();
        var progress = new ImmediateProgress<BuildProgress>(p => { events.Add(p); board.Apply(p); });
        try
        {
            await fixture.Engine.BuildAsync(recipe, progress, token);
            return new(board, null, events);
        }
        catch (Exception error) { return new(board, error, events); }
    }

    private static string[] Ids(BuildStageChoice stage) => stage.Operations.Select(o => o.Id).ToArray();

    [Fact]
    public async Task Items_sit_under_their_own_stage_row_and_a_finished_build_is_complete()
    {
        using var fixture = new BuildFixture();
        var run = await RunAsync(fixture, fixture.Recipe with { SelectedOptions = ["copilot-app", "workfolders"] });
        Assert.Null(run.Error);
        var servicing = run.Board.Stages[2];
        var ids = Ids(servicing);
        var apps = ids.ToList().FindIndex(id => id.StartsWith("app:", StringComparison.Ordinal));
        var feature = ids.ToList().FindIndex(id => id.StartsWith("feature:", StringComparison.Ordinal));
        Assert.Equal("stage:移除应用", ids[apps - 1]);
        Assert.Equal("stage:可选功能", ids[feature - 1]);
        // Every item row follows a stage row with the same engine stage; no row of another stage splits them.
        for (var index = 0; index < servicing.Operations.Count; index++)
        {
            var row = servicing.Operations[index];
            if (row.Id.StartsWith("stage:", StringComparison.Ordinal)) continue;
            var owner = servicing.Operations.Take(index).Last(o => o.Id.StartsWith("stage:", StringComparison.Ordinal));
            Assert.Equal(row.Stage, owner.Stage);
        }
        Assert.All(run.Board.Stages, stage => Assert.Equal("已完成", stage.Status));
    }

    [Fact]
    public async Task One_failed_item_is_one_failure_and_unstarted_work_is_not_run()
    {
        using var fixture = new BuildFixture();
        fixture.Runner.FailPackages = true;
        var action = new ExpertAction(ImageItemKind.Package, "Protected-Package~31bf~amd64~~1.0", ExpertActionMode.Remove);
        var run = await RunAsync(fixture, fixture.Recipe with { ExpertAcknowledged = true, ExpertActions = [action] });
        Assert.IsType<BuildFailureException>(run.Error);
        run.Board.Stop(cancelled: false);
        var servicing = run.Board.Stages[2];
        var failedRows = servicing.Operations.Where(o => o.State == BuildOperationState.Failed).ToArray();
        Assert.Equal(2, failedRows.Length);   // the item and its stage both failed ...
        Assert.StartsWith("失败：", servicing.Status);   // ... but the page reports one failure, naming the item
        Assert.Contains("Protected-Package", servicing.Status);
        Assert.DoesNotContain("2 项失败", servicing.Status);
        Assert.Contains("未运行", servicing.Status);
        Assert.DoesNotContain("还有", servicing.Status);
        Assert.All(servicing.Operations.Where(o => o.State == BuildOperationState.Pending), o => Assert.Equal("未运行", o.Status));
        Assert.Equal("未运行", run.Board.Stages[6].Status);
        Assert.Equal("未运行", run.Board.Stages[5].Status);
    }

    [Fact]
    public async Task Failure_outside_any_item_is_still_counted_once()
    {
        using var fixture = new BuildFixture();
        fixture.Runner.FailIso = true;
        var run = await RunAsync(fixture, fixture.Recipe);
        Assert.IsType<BuildFailureException>(run.Error);
        run.Board.Stop(cancelled: false);
        Assert.StartsWith("失败：创建可启动 ISO", run.Board.Stages[5].Status);
        Assert.Equal("已完成", run.Board.Stages[4].Status);
        Assert.Equal("未运行", run.Board.Stages[6].Status);
    }

    [Fact]
    public async Task Cancelled_build_is_reported_as_cancelled_and_stop_leaves_nothing_pending()
    {
        using var fixture = new BuildFixture();
        using var cancel = new CancellationTokenSource();
        fixture.Runner.OnCommand = args => { if (args.Contains("/Mount-Image")) cancel.Cancel(); };
        var run = await RunAsync(fixture, fixture.Recipe, cancel.Token);
        var failure = Assert.IsType<BuildFailureException>(run.Error);
        Assert.True(OperationOutcomes.IsCancellation(OperationKind.Build, failure));
        run.Board.Stop(cancelled: true);
        // The mount had finished when the cancel was seen, so it is complete rather than relabelled as cancelled.
        Assert.Equal("已完成", run.Board.Stages[1].Status);
        Assert.Equal("未运行", run.Board.Stages[2].Status);
        Assert.DoesNotContain(run.Board.Stages.SelectMany(s => s.Operations), o => o.State == BuildOperationState.Failed);
        Assert.DoesNotContain(run.Board.Stages.SelectMany(s => s.Operations), o => o.State == BuildOperationState.Running);
        Assert.All(run.Board.Stages.SelectMany(s => s.Operations).Where(o => o.State == BuildOperationState.Pending),
            o => Assert.True(o.NotRun));
        Assert.Equal("未运行", run.Board.Stages[5].Status);
    }

    [Fact]
    public async Task Cancelling_between_items_marks_one_item_cancelled_and_the_rest_not_run()
    {
        using var fixture = new BuildFixture();
        using var cancel = new CancellationTokenSource();
        fixture.Runner.OnCommand = args => { if (args.Contains("/Disable-Feature")) cancel.Cancel(); };
        var run = await RunAsync(fixture, fixture.Recipe with { SelectedOptions = ["xps", "workfolders"] }, cancel.Token);
        Assert.True(OperationOutcomes.IsCancellation(OperationKind.Build, Assert.IsType<BuildFailureException>(run.Error)));
        run.Board.Stop(cancelled: true);
        var servicing = run.Board.Stages[2];
        var features = servicing.Operations.Where(o => o.Id.StartsWith("feature:", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, features.Length);
        Assert.Single(features, o => o.State == BuildOperationState.Completed);
        Assert.Single(features, o => o.State == BuildOperationState.Cancelled);
        Assert.StartsWith("已取消", servicing.Status);   // the item and its stage were both cancelled, but it is one cancellation
        Assert.Contains("未运行", servicing.Status);
        Assert.DoesNotContain("失败", servicing.Status);
        Assert.DoesNotContain(run.Board.Stages.SelectMany(s => s.Operations), o => o.State == BuildOperationState.Failed);
    }

    [Fact]
    public async Task Failed_item_detail_is_short_on_the_row_but_kept_in_full()
    {
        using var fixture = new BuildFixture();
        fixture.Runner.FailPackages = true;
        var action = new ExpertAction(ImageItemKind.Package, "Protected-Package~31bf~amd64~~1.0", ExpertActionMode.Remove);
        var run = await RunAsync(fixture, fixture.Recipe with { ExpertAcknowledged = true, ExpertActions = [action] });
        var row = run.Board.Stages[2].Operations.Single(o => o.Id.StartsWith("expert:", StringComparison.Ordinal));
        Assert.Equal(BuildOperationState.Failed, row.State);
        Assert.True(row.HasDetail);
        Assert.Contains("Protected package removal refused", row.Detail);
        Assert.DoesNotContain("   at ", row.Detail);
        Assert.True(row.Detail.Length <= 361);
        Assert.Contains("   at ", row.FullDetail);   // the stack trace is retained, just not shown on the page
    }

    [Fact]
    public void Long_detail_is_bounded_on_the_row_and_retained_in_full()
    {
        var board = new BuildStageBoard();
        var text = "DISM failed. " + string.Join(" ", Enumerable.Repeat("The payload is protected.", 200));
        board.Apply(new("移除应用", "Remove X", OperationId: "app:X", State: BuildOperationState.Failed, Detail: text));
        var row = Assert.Single(board.Stages[2].Operations);
        Assert.InRange(row.Detail.Length, 1, 361);
        Assert.EndsWith("…", row.Detail);
        Assert.Equal(text, row.FullDetail);
    }

    [Fact]
    public void Counts_use_items_and_an_item_less_stage_is_one_unit()
    {
        var board = new BuildStageBoard();
        board.Apply(new("移除应用", "移除所选应用", OperationId: "stage:移除应用", State: BuildOperationState.Pending));
        board.Apply(new("可选功能", "移除可选功能", OperationId: "stage:可选功能", State: BuildOperationState.Pending));
        board.Apply(new("移除应用", "移除所选应用", OperationId: "stage:移除应用", State: BuildOperationState.Running));
        foreach (var app in new[] { "A", "B" })
            board.Apply(new("移除应用", "移除 " + app, OperationId: "app:" + app, State: BuildOperationState.Pending));
        board.Apply(new("移除应用", "移除 A", OperationId: "app:A", State: BuildOperationState.Completed));
        // 1 done; next = app B plus the still-empty "可选功能" stage. The running stage row is not a unit.
        Assert.Equal("进行中 · 已完成 1 · 还有 2 项", board.Stages[2].Status);
        Assert.Equal(["stage:移除应用", "app:A", "app:B", "stage:可选功能"], Ids(board.Stages[2]));
    }

    [Fact]
    public void A_feature_batch_failure_counts_each_feature()
    {
        var board = new BuildStageBoard();
        board.Apply(new("可选功能", "移除可选功能", OperationId: "stage:可选功能", State: BuildOperationState.Running));
        foreach (var feature in new[] { "X", "Y" })
        {
            board.Apply(new("可选功能", "移除功能 " + feature, OperationId: "feature:" + feature, State: BuildOperationState.Pending));
            board.Apply(new("可选功能", "移除功能 " + feature, OperationId: "feature:" + feature,
                State: BuildOperationState.Failed, Detail: "Feature batch failed."));
        }
        board.Apply(new("可选功能", "移除可选功能", OperationId: "stage:可选功能", State: BuildOperationState.Failed));
        Assert.StartsWith("2 项失败", board.Stages[2].Status);
    }

    [Fact]
    public void Progress_fraction_counts_finished_items_plus_the_running_share()
    {
        var board = new BuildStageBoard();
        board.Apply(new("移除应用", "移除所选应用", OperationId: "stage:移除应用", State: BuildOperationState.Running));
        foreach (var app in new[] { "A", "B" })
            board.Apply(new("移除应用", "移除 " + app, OperationId: "app:" + app, State: BuildOperationState.Pending));
        board.Apply(new("移除应用", "移除 A", OperationId: "app:A", State: BuildOperationState.Completed));
        board.Apply(new("移除应用", "移除 B", OperationId: "app:B", State: BuildOperationState.Running));
        Assert.Equal(0.75, board.Stages[2].Fraction(new("移除应用", "移除 B", 50, "app:B")));
        Assert.Equal(0.5, new BuildStageBoard().Stages[0].Fraction(new("准备", "x", 50)));
    }

    [Fact]
    public void A_resumed_run_marks_the_phases_it_never_planned_as_done_earlier()
    {
        var board = new BuildStageBoard();
        foreach (var stage in new[] { "创建 ISO", "校验输出" })
            board.Apply(new(stage, stage, OperationId: "stage:" + stage, State: BuildOperationState.Pending));
        board.MarkEarlierPhasesDone();
        Assert.All(board.Stages.Take(5), stage => Assert.Equal("先前已完成", stage.Status));
        Assert.Equal("已完成 0 · 还有 1 项", board.Stages[5].Status);
        board.Stop(cancelled: false);
        Assert.Equal("先前已完成", board.Stages[0].Status);   // stopping never relabels finished work
        board.Complete();
        Assert.Equal("先前已完成", board.Stages[2].Status);
        Assert.Equal("已完成", board.Stages[6].Status);
        board.Reset();
        Assert.All(board.Stages, stage => Assert.Equal("等待中", stage.Status));
    }

    [Fact]
    public async Task A_resumed_run_drives_only_the_tail_and_reads_as_done_earlier_for_the_rest()
    {
        using var fixture = new BuildFixture();
        fixture.Runner.FailIso = true;
        var first = await RunAsync(fixture, fixture.Recipe);
        var stopped = Assert.IsType<BuildFailureException>(first.Error);
        Assert.True(JobCheckpoint.Exists(stopped.WorkDirectory));   // what makes the page offer Resume
        var outcome = OperationOutcomes.Classify(OperationKind.Build, stopped, canResume: true);
        Assert.Contains("已保存的进度可以继续。", outcome.Message);

        var job = Path.Combine(stopped.WorkDirectory, "job.json");
        var candidate = ResumeSupport.Inspect(job, JobCheckpoint.Exists, JobCheckpoint.RecipeFileName);
        Assert.Null(candidate.Problem);
        fixture.Runner.FailIso = false;
        var board = new BuildStageBoard();
        var events = new List<BuildProgress>();
        var built = await fixture.Engine.ResumeAsync(candidate.JobFile, candidate.Recipe!,
            new ImmediateProgress<BuildProgress>(p => { events.Add(p); board.Apply(p); board.MarkEarlierPhasesDone(); }), CancellationToken.None);

        Assert.True(File.Exists(built.IsoPath));
        Assert.DoesNotContain(events, e => BuildProgressText.Phase(e.Stage) is >= 0 and <= 2 && e.OperationId is not null);
        Assert.Contains(board.Stages[3].Operations, o => o.Id == "stage:" + BuildProgressText.ResumeCheckStage);
        board.Complete();
        // The first run had already compressed the image when the ISO step failed, so the resume never plans Compress either.
        Assert.All(new[] { 0, 1, 2, 4 }.Select(index => board.Stages[index]), stage => Assert.Equal("先前已完成", stage.Status));
        Assert.All(new[] { 3, 5, 6 }.Select(index => board.Stages[index]), stage => Assert.Equal("已完成", stage.Status));
        Assert.Empty(built.Warnings);
    }

    [Fact]
    public async Task A_resume_from_the_committed_image_also_drives_compression()
    {
        using var fixture = new BuildFixture();
        fixture.Runner.OnCommand = args =>
        {
            if (args.Any(a => a.StartsWith("/DestinationImageFile:", StringComparison.Ordinal) && a.EndsWith("install.esd", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("There is not enough space on the disk.");
        };
        var first = await RunAsync(fixture, fixture.Recipe);
        var stopped = Assert.IsType<BuildFailureException>(first.Error);
        first.Board.Stop(cancelled: false);
        Assert.StartsWith("失败：压缩最终映像", first.Board.Stages[4].Status);
        Assert.True(JobCheckpoint.Exists(stopped.WorkDirectory));

        fixture.Runner.OnCommand = null;
        var candidate = ResumeSupport.Inspect(Path.Combine(stopped.WorkDirectory, "job.json"), JobCheckpoint.Exists, JobCheckpoint.RecipeFileName);
        var board = new BuildStageBoard();
        var built = await fixture.Engine.ResumeAsync(candidate.JobFile, candidate.Recipe!,
            new ImmediateProgress<BuildProgress>(p => { board.Apply(p); board.MarkEarlierPhasesDone(); }), CancellationToken.None);
        board.Complete();
        Assert.True(File.Exists(built.IsoPath));
        Assert.All(board.Stages.Take(3), stage => Assert.Equal("先前已完成", stage.Status));
        Assert.All(board.Stages.Skip(3), stage => Assert.Equal("已完成", stage.Status));
        Assert.Contains(board.Stages[4].Operations, o => o.Id == "stage:压缩");
    }

    [Fact]
    public async Task Recover_is_refused_for_saved_progress_with_a_message_the_page_can_show_as_is()
    {
        using var fixture = new BuildFixture();
        fixture.Runner.FailIso = true;
        var first = await RunAsync(fixture, fixture.Recipe);
        var stopped = Assert.IsType<BuildFailureException>(first.Error);
        var refusal = await Assert.ThrowsAsync<CheckpointedJobException>(() => fixture.Engine.RecoverAsync(
            Path.Combine(stopped.WorkDirectory, "job.json"), fixture.Recipe.DismPath, CancellationToken.None));
        var outcome = OperationOutcomes.Classify(OperationKind.Recover, refusal);
        Assert.False(outcome.Cancelled);
        Assert.Equal("恢复未完成", outcome.Title);
        Assert.Contains("继续", outcome.Message);
        Assert.True(Directory.Exists(Path.Combine(stopped.WorkDirectory, "media")));   // nothing was discarded
    }

    [Fact]
    public void Inventory_rows_expose_modes_only_for_services_and_features_and_a_readable_label()
    {
        var service = new InventoryChoice(new(ImageItemKind.Service, "WSearch", "Auto", false));
        var app = new InventoryChoice(new(ImageItemKind.ProvisionedApp, "Contoso.App", "1.0", false));
        Assert.True(service.HasModes);
        Assert.False(app.HasModes);
        Assert.Equal("WSearch 的处理方式", service.ModeLabel);
        Assert.Equal(ExpertActionMode.Disable, service.Action.Mode);
        Assert.Equal(ExpertActionMode.Remove, app.Action.Mode);
    }

    [Fact]
    public async Task The_history_heading_counts_the_stages_its_rows_show_as_done()
    {
        Assert.Equal("已完成 7 个阶段中的 0 个", StageSummary.Describe(new BuildStageBoard()));

        using var finishing = new BuildFixture();
        var finished = await RunAsync(finishing, finishing.Recipe);
        Assert.Null(finished.Error);
        Assert.Equal("全部 7 个阶段已完成", StageSummary.Describe(finished.Board));

        using var failing = new BuildFixture();
        failing.Runner.FailIso = true;
        var failed = await RunAsync(failing, failing.Recipe);
        failed.Board.Stop(cancelled: false);
        Assert.Equal("已完成 7 个阶段中的 5 个 · 1 个失败", StageSummary.Describe(failed.Board));

        using var cancelling = new BuildFixture();
        using var cancel = new CancellationTokenSource();
        cancelling.Runner.OnCommand = args => { if (args.Contains("/Mount-Image")) cancel.Cancel(); };
        var cancelled = await RunAsync(cancelling, cancelling.Recipe, cancel.Token);
        cancelled.Board.Stop(cancelled: true);
        Assert.Equal("已完成 7 个阶段中的 2 个", StageSummary.Describe(cancelled.Board));   // the rest were not run
    }

    [Fact]
    public void Stages_finished_by_an_earlier_run_count_as_done_in_the_history_heading()
    {
        var board = new BuildStageBoard();
        foreach (var stage in new[] { "创建 ISO", "校验输出" })
            board.Apply(new(stage, stage, OperationId: "stage:" + stage, State: BuildOperationState.Pending));
        board.MarkEarlierPhasesDone();
        Assert.Equal("已完成 7 个阶段中的 5 个", StageSummary.Describe(board));
        board.Complete();
        Assert.Equal("全部 7 个阶段已完成", StageSummary.Describe(board));
        board.Reset();
        Assert.Equal("已完成 7 个阶段中的 0 个", StageSummary.Describe(board));
    }
}
