using System.Diagnostics;
using System.Text;

namespace Tiny11Builder.Core;

public sealed record ProcessResult(int ExitCode, string Output);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        Action<string>? output, CancellationToken cancellationToken, bool finishOnCancellation = true);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        Action<string>? output, CancellationToken cancellationToken, bool finishOnCancellation = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        var text = new StringBuilder();
        void Receive(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            lock (text) text.AppendLine(e.Data);
            output?.Invoke(e.Data);
        }
        process.OutputDataReceived += Receive;
        process.ErrorDataReceived += Receive;
        if (!process.Start()) throw new IOException($"无法启动 {Path.GetFileName(executable)}。");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var registration = cancellationToken.Register(() =>
        {
            if (finishOnCancellation) return;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* 进程在检查与终止之间已退出。 */ }
        });
        // 在挂载/提交期间终止 DISM 可能会使映像成为孤立状态。取消会在下一个边界处检查。
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        process.WaitForExit();
        if (!finishOnCancellation) cancellationToken.ThrowIfCancellationRequested();
        return new(process.ExitCode, text.ToString());
    }
}
