using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WakeGlass.Core;

public record LogEntry(DateTimeOffset Time, string Code, string Message);
public sealed partial class Diagnostics : IDiagnostics
{
    public static string LogPath => Path.Combine(SettingsStore.DataDirectory, "events.jsonl");
    public static string Redact(string text)
    {
        foreach (var value in new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName, Environment.MachineName }.Where(v => !string.IsNullOrWhiteSpace(v)).OrderByDescending(v => v.Length)) text = text.Replace(value, "[redacted]", StringComparison.OrdinalIgnoreCase);
        return SidPattern().Replace(text, "[sid]");
    }
    [GeneratedRegex(@"S-1-\d+(?:-\d+)+")] private static partial Regex SidPattern();
    public void Write(string code, string message)
    {
        Directory.CreateDirectory(SettingsStore.DataDirectory);
        using var gate = new Mutex(false, "Local\\WakeGlass-Logs-" + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value);
        try { try { if (!gate.WaitOne(5000)) return; } catch (AbandonedMutexException) { }
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 2_000_000) File.Move(LogPath, LogPath + ".old", true);
            File.AppendAllText(LogPath, JsonSerializer.Serialize(new LogEntry(DateTimeOffset.Now, code, Redact(message))) + Environment.NewLine, Encoding.UTF8);
        } finally { try { gate.ReleaseMutex(); } catch (ApplicationException) { } }
    }
    public IReadOnlyList<LogEntry> Read(int count = 80)
    {
        if (!File.Exists(LogPath)) return [];
        using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var result = new List<LogEntry>();
        while (reader.ReadLine() is { } line) try { if (JsonSerializer.Deserialize<LogEntry>(line) is { } entry) result.Add(entry); } catch (JsonException) { }
        return result.TakeLast(count).Reverse().ToArray();
    }
    public static async Task<string> RunPowerCfgAsync(string args)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "powercfg.exe"), args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.GetEncoding((int)GetOEMCP()), StandardErrorEncoding = Encoding.GetEncoding((int)GetOEMCP()) };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try { await process.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { process.Kill(); return "诊断命令超时。"; }
        return Redact((await output) + (await error));
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetOEMCP();
    public async Task<string> ReportAsync(Capabilities c, IReadOnlyList<ScheduledTaskInfo>? tasks = null)
    {
        var outputs = await Task.WhenAll(new[] { "/a", "/waketimers", "/lastwake" }.Select(RunPowerCfgAsync));
        var registrations = (tasks ?? []).Where(task => task.Spec.Kind != TaskKind.Probe)
            .Select(task => $"{task.Spec.Name} · {task.State} · 下次 {task.NextRun:yyyy-MM-dd HH:mm:ss} · 结果 0x{task.LastResult:X8}");
        return Redact($"WakeGlass diagnostics · {DateTimeOffset.Now:O}\nWindows {Environment.OSVersion.Version}\n{c.Summary}\n{c.BlockReason ?? "静态能力检查通过；硬件唤醒尚未验证。"}\n{c.Details}\n\nS0 的唤醒受固件及 Windows 策略限制。电池供电时，系统可能限制保持运行请求。\n诊断命令需要提权时仅报告限制，不自动提升权限。\n\n" + string.Join("\n\n", new[] { "powercfg /a\n" + outputs[0], "powercfg /waketimers\n" + outputs[1], "powercfg /lastwake\n" + outputs[2] }) + "\n\nWakeGlass 任务\n" + string.Join("\n", registrations) + "\n\nRecent events\n" + string.Join("\n", Read().Select(x => $"{x.Time:O} [{x.Code}] {x.Message}")));
    }
}
