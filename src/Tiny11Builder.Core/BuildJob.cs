using System.Text.Json;

namespace Tiny11Builder.Core;

public sealed class BuildFailureException : Exception
{
    public BuildFailureException(string message, string workDirectory, Exception inner,
        bool? isCancellation = null, bool recoveryRequired = false) : base(message, inner)
    {
        WorkDirectory = workDirectory;
        IsCancellation = isCancellation ?? IsCancellationCause(inner);
        RecoveryRequired = recoveryRequired;
    }

    public string WorkDirectory { get; }

    /// <summary>当主要原因是取消时为 true，即使次要的清理问题也一并被汇总进来。</summary>
    public bool IsCancellation { get; }

    /// <summary>当拥有的映像挂载无法丢弃时为 true；必须先恢复该任务，才能做其他事情。</summary>
    public bool RecoveryRequired { get; }

    // 汇总的失败会把主要原因列在最前面。
    internal static bool IsCancellationCause(Exception error) => error switch
    {
        OperationCanceledException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Count > 0 && IsCancellationCause(aggregate.InnerExceptions[0]),
        _ => false
    };
}

public sealed record JobState(string Id, string Purpose, string Status, string Stage, DateTimeOffset StartedUtc);

internal sealed class BuildJob : IDisposable
{
    private readonly StreamWriter log;
    private readonly FileStream jobLock;
    private JobState state;
    private readonly object gate = new();
    public string Directory { get; }

    private BuildJob(string directory, FileStream jobLock, StreamWriter log, JobState state)
    {
        Directory = directory;
        this.jobLock = jobLock;
        this.log = log;
        this.state = state;
    }

    public static BuildJob Create(string workspace, string purpose, bool bufferedLog = false)
    {
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(PathSafety.Full(workspace), "tiny11-" + id);
        System.IO.Directory.CreateDirectory(directory);
        var jobLock = new FileStream(Path.Combine(directory, "job.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        StreamWriter? log = null;
        try
        {
            log = new StreamWriter(Path.Combine(directory, "build.log"), append: false) { AutoFlush = !bufferedLog };
            var job = new BuildJob(directory, jobLock, log, new(id, purpose, "running", "准备", DateTimeOffset.UtcNow));
            job.State("running");
            return job;
        }
        catch
        {
            log?.Dispose();
            jobLock.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 获取现有任务的独占锁，并追加写入其原始日志。其他内容都不改动：只有在恢复通过所有检查之后，
    /// <see cref="State"/> 才会重写任务标记。
    /// </summary>
    public static BuildJob Open(string directory, bool bufferedLog)
    {
        FileStream jobLock;
        try { jobLock = new FileStream(Path.Combine(directory, "job.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new InvalidDataException("此文件夹不是完整的 tiny11 任务（缺少 job.lock）。", error);
        }
        catch (IOException error)
        {
            throw new InvalidDataException("此任务正被运行中的构建或该应用的另一个实例占用，因此目前无法恢复。", error);
        }
        StreamWriter? log = null;
        try
        {
            var current = JsonSerializer.Deserialize<JobState>(File.ReadAllText(Path.Combine(directory, "job.json")), JsonDefaults.Options)
                ?? throw new InvalidDataException("任务标记无效。");
            log = new StreamWriter(Path.Combine(directory, "build.log"), append: true) { AutoFlush = !bufferedLog };
            return new BuildJob(directory, jobLock, log, current);
        }
        catch (JsonException error)
        {
            log?.Dispose();
            jobLock.Dispose();
            throw new InvalidDataException("任务标记（job.json）已损坏。", error);
        }
        catch
        {
            log?.Dispose();
            jobLock.Dispose();
            throw;
        }
    }

    public JobState Current => state;
    public void Log(string message) { lock (gate) log.WriteLine($"[{DateTimeOffset.UtcNow:O}] {message}"); }
    public void Flush() { lock (gate) log.Flush(); }
    public void State(string status, string? stage = null)
    {
        state = state with { Status = status, Stage = stage ?? state.Stage };
        var file = Path.Combine(Directory, "job.json");
        File.WriteAllText(file + ".new", JsonSerializer.Serialize(state, JsonDefaults.Options));
        File.Move(file + ".new", file, overwrite: true);
    }
    public void Dispose() { log.Dispose(); jobLock.Dispose(); }
}
