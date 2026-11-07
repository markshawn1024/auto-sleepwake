using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;

namespace WakeGlass.Core;

public sealed class WakeScheduler(string executable) : IWakeScheduler
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    public static string FolderPath => "\\WakeGlass-" + WindowsIdentity.GetCurrent().User!.Value;
    private readonly string executable = Path.GetFullPath(executable);
    private static void CheckName(string name)
    {
        if (name is not (ScheduleManager.SleepName or ScheduleManager.WakeName or ScheduleManager.InstantName) && !StageScheduleManager.IsStageTask(name) && !(name.StartsWith("Probe-", StringComparison.Ordinal) && Guid.TryParseExact(name[6..], "N", out _))) throw new ArgumentException("拒绝访问非 WakeGlass 任务。");
    }
    private static T WithFolder<T>(bool create, Func<dynamic, T> action)
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        object? folder = null;
        try
        {
            service.Connect();
            try { folder = service.GetFolder(FolderPath); }
            catch (Exception e) when ((uint)e.HResult is 0x80070002 or 0x80070003)
            {
                if (!create) return action(null!);
                dynamic root = service.GetFolder("\\");
                try { folder = root.CreateFolder(FolderPath[1..], null); } finally { Marshal.FinalReleaseComObject(root); }
            }
            return action(folder!);
        }
        finally { if (folder != null) Marshal.FinalReleaseComObject(folder); Marshal.FinalReleaseComObject(service); }
    }
    public static string BuildXml(TaskSpec s, string exe, string user)
    {
        var worker = s.Kind == TaskKind.StageWorker;
        var trigger = s.Days == 0
            ? new XElement(Ns + "TimeTrigger", new XElement(Ns + "StartBoundary", s.Start.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)), new XElement(Ns + "Enabled", true))
            : new XElement(Ns + "CalendarTrigger", new XElement(Ns + "StartBoundary", s.Start.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)), new XElement(Ns + "Enabled", true), new XElement(Ns + "ScheduleByWeek", new XElement(Ns + "WeeksInterval", 1), new XElement(Ns + "DaysOfWeek", Enumerable.Range(0, 7).Where(i => (s.Days & (1 << i)) != 0).Select(i => new XElement(Ns + ((DayOfWeek)i).ToString())))));
        var triggers = new XElement(Ns + "Triggers", trigger);
        if (worker)
        {
            trigger.AddFirst(new XElement(Ns + "Repetition", new XElement(Ns + "Interval", "PT1M")));
            triggers.Add(new XElement(Ns + "LogonTrigger", new XElement(Ns + "UserId", user)));
        }
        return new XDocument(new XElement(Ns + "Task", new XAttribute("version", "1.4"),
            new XElement(Ns + "RegistrationInfo", new XElement(Ns + "Author", "WakeGlass"), new XElement(Ns + "Description", $"WakeGlass/v1|{s.Kind}|{s.Id}")),
            triggers,
            new XElement(Ns + "Principals", new XElement(Ns + "Principal", new XAttribute("id", "User"), new XElement(Ns + "UserId", user), new XElement(Ns + "LogonType", "InteractiveToken"), new XElement(Ns + "RunLevel", "LeastPrivilege"))),
            new XElement(Ns + "Settings", new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"), new XElement(Ns + "DisallowStartIfOnBatteries", false), new XElement(Ns + "StopIfGoingOnBatteries", false), new XElement(Ns + "AllowHardTerminate", worker), new XElement(Ns + "StartWhenAvailable", s.Kind == TaskKind.Wake || worker), new XElement(Ns + "RunOnlyIfNetworkAvailable", false), new XElement(Ns + "Enabled", s.Enabled), new XElement(Ns + "Hidden", false), new XElement(Ns + "RunOnlyIfIdle", false), new XElement(Ns + "WakeToRun", s.Kind is TaskKind.Wake or TaskKind.StageWake), new XElement(Ns + "ExecutionTimeLimit", worker ? "PT0S" : "PT5M"), new XElement(Ns + "Priority", 7)),
            new XElement(Ns + "Actions", new XAttribute("Context", "User"), new XElement(Ns + "Exec", new XElement(Ns + "Command", exe), new XElement(Ns + "Arguments", Arguments(s)), new XElement(Ns + "WorkingDirectory", Path.GetDirectoryName(exe)))))).ToString();
    }
    private static string Arguments(TaskSpec s) => s.Kind switch
    {
        TaskKind.Wake => "--wake " + s.Id,
        TaskKind.Sleep => "--scheduled-sleep " + s.Id,
        TaskKind.StageWake => "--stage-wake " + s.Id,
        TaskKind.StageWorker => "--stage-worker",
        _ => "--probe"
    };

    public void StartStageWorker() => WithFolder(false, folder =>
    {
        if (folder == null) throw new IOException("找不到多阶段后台任务。");
        dynamic task = folder.GetTask(StageScheduleManager.WorkerName);
        try
        {
            if ((int)task.State != 4)
            {
                object? running = task.Run(null);
                if (running != null) Marshal.FinalReleaseComObject(running);
            }
        }
        finally { Marshal.FinalReleaseComObject(task); }
        return 0;
    });
    public void Register(TaskSpec spec)
    {
        CheckName(spec.Name);
        WithFolder(true, folder =>
        {
            dynamic task = folder.RegisterTask(spec.Name, BuildXml(spec, executable, WindowsIdentity.GetCurrent().User!.Value), 6, null, null, 3, null);
            Marshal.FinalReleaseComObject(task); return 0;
        });
    }
    public bool Verify(TaskSpec spec)
    {
        CheckName(spec.Name);
        return WithFolder(false, folder =>
        {
            if (folder == null) return false;
            object? task = null;
            try
            {
                task = folder.GetTask(spec.Name);
                dynamic t = task;
                var xml = XDocument.Parse((string)t.Xml);
                var expected = XDocument.Parse(BuildXml(spec, executable, WindowsIdentity.GetCurrent().User!.Value));
                string? Value(XDocument doc, string name)
                {
                    if (name == "UserId") return doc.Descendants(Ns + "Principal").Single().Element(Ns + name)?.Value;
                    return doc.Descendants(Ns + name).SingleOrDefault()?.Value ?? name switch { "RunLevel" => "LeastPrivilege", "AllowHardTerminate" => "true", "WakeToRun" or "StartWhenAvailable" or "RunOnlyIfNetworkAvailable" or "RunOnlyIfIdle" => "false", _ => null };
                }
                // Compare all safety-relevant definition fields; weekly boundary dates naturally advance.
                foreach (var name in new[] { "Description", "Command", "Arguments", "WorkingDirectory", "UserId", "LogonType", "RunLevel", "WakeToRun", "DisallowStartIfOnBatteries", "StopIfGoingOnBatteries", "StartWhenAvailable", "RunOnlyIfNetworkAvailable", "RunOnlyIfIdle", "AllowHardTerminate", "ExecutionTimeLimit", "MultipleInstancesPolicy" })
                    if (Value(xml, name) != Value(expected, name)) return false;
                if ((bool)t.Enabled != spec.Enabled) return false;
                if (new[] { "RandomDelay", "Delay", "EndBoundary" }.Any(name => xml.Descendants(Ns + name).Any())) return false;
                if (xml.Descendants(Ns + "Actions").Single().Elements().Count() != 1 || xml.Descendants(Ns + "Exec").Count() != 1) return false;
                var triggers = xml.Descendants(Ns + "Triggers").Single().Elements().ToArray();
                if (triggers.Any(trigger => (trigger.Element(Ns + "Enabled")?.Value ?? "true") != "true")) return false;
                if (spec.Kind == TaskKind.StageWorker)
                {
                    var logon = triggers.SingleOrDefault(trigger => trigger.Name == Ns + "LogonTrigger");
                    var repetition = triggers.SingleOrDefault(trigger => trigger.Name == Ns + "TimeTrigger")?.Element(Ns + "Repetition");
                    return triggers.Length == 2 && SameUser(logon?.Element(Ns + "UserId")?.Value, Value(expected, "UserId")!) &&
                        repetition?.Element(Ns + "Interval")?.Value == "PT1M" && repetition.Element(Ns + "Duration") == null;
                }
                if (triggers.Length != 1 || xml.Descendants(Ns + "Repetition").Any()) return false;
                var start = DateTime.Parse(xml.Descendants(Ns + "StartBoundary").Single().Value, CultureInfo.InvariantCulture);
                if (spec.Days == 0)
                {
                    DateTime next = t.NextRunTime;
                    return Math.Abs((start - spec.Start).TotalSeconds) < 1 && !xml.Descendants(Ns + "ScheduleByWeek").Any() && (!spec.Enabled || Math.Abs((next - spec.Start).TotalSeconds) < 2);
                }
                if (spec.Kind == TaskKind.StageWake && spec.Enabled && Math.Abs(((DateTime)t.NextRunTime - spec.Start).TotalSeconds) >= 2) return false;
                return triggers[0].Name == Ns + "CalendarTrigger" && Math.Abs((start.TimeOfDay - spec.Start.TimeOfDay).TotalSeconds) < 1 && ReadDays(xml) == spec.Days && (xml.Descendants(Ns + "WeeksInterval").SingleOrDefault()?.Value ?? "1") == "1";
            }
            catch (Exception e) when ((uint)e.HResult is 0x80070002 or 0x80070003) { return false; }
            finally { if (task != null) Marshal.FinalReleaseComObject(task); }
        });
    }
    private static bool SameUser(string? actual, string expected)
    {
        if (actual == expected) return true;
        if (string.IsNullOrWhiteSpace(actual)) return false;
        try { return new NTAccount(actual).Translate(typeof(SecurityIdentifier)).Value == expected; }
        catch (IdentityNotMappedException) { return false; }
    }
    private static int ReadDays(XDocument xml) => xml.Descendants(Ns + "DaysOfWeek").Elements().Aggregate(0, (mask, el) => mask | (1 << (int)Enum.Parse<DayOfWeek>(el.Name.LocalName)));
    public IReadOnlyList<ScheduledTaskInfo> List() => WithFolder<IReadOnlyList<ScheduledTaskInfo>>(false, folder =>
    {
        var result = new List<ScheduledTaskInfo>();
        if (folder == null) return result;
        dynamic tasks = folder.GetTasks(1);
        try
        {
            for (int i = 1; i <= (int)tasks.Count; i++)
            {
                dynamic t = tasks.Item(i);
                try
                {
                    string name = t.Name;
                    try { CheckName(name); } catch (ArgumentException) { continue; }
                    var xml = XDocument.Parse((string)t.Xml);
                    var marker = xml.Descendants(Ns + "Description").FirstOrDefault()?.Value.Split('|');
                    if (marker is not { Length: 3 } || marker[0] != "WakeGlass/v1") continue;
                    var s = new TaskSpec(name, Enum.Parse<TaskKind>(marker[1]), DateTime.Parse(xml.Descendants(Ns + "StartBoundary").First().Value, CultureInfo.InvariantCulture), ReadDays(xml), (bool)t.Enabled, marker[2]);
                    DateTime next = t.NextRunTime, last = t.LastRunTime;
                    result.Add(new(s, next.Year > 2000 ? next : null, last.Year > 2000 ? last : null, (int)t.LastTaskResult, (int)t.State switch { 1 => "已暂停", 2 => "排队中", 3 => "就绪", 4 => "运行中", _ => "未知" }));
                }
                finally { Marshal.FinalReleaseComObject(t); }
            }
        }
        finally { Marshal.FinalReleaseComObject(tasks); }
        return result;
    });
    public void Delete(string name)
    {
        CheckName(name);
        WithFolder(false, folder => { if (folder != null) try { folder.DeleteTask(name, 0); } catch (Exception e) when ((uint)e.HResult is 0x80070002 or 0x80070003) { } return 0; });
    }
    public bool ProbeAccess()
    {
        string name = "Probe-" + Guid.NewGuid().ToString("N");
        try { var s = new TaskSpec(name, TaskKind.Probe, DateTime.Now.AddDays(2), Enabled: false); Register(s); return Verify(s); }
        finally { Delete(name); }
    }
}
