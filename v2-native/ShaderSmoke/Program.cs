using SkiaSharp;
using System.Collections;
using System.Reflection;

string shaderRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LiquidGlassAvaloniaUI", "Assets", "Shaders"));
string[] files = { "LiquidGlassShader.sksl", "LiquidGlassHighlight.sksl" };

foreach (string file in files)
{
    string source = File.ReadAllText(Path.Combine(shaderRoot, file));
    using SKRuntimeEffect? effect = SKRuntimeEffect.CreateShader(source, out string? error);
    if (effect is null) throw new InvalidOperationException($"{file}: {error}");
    Console.WriteLine($"OK {file}");
}

Type monitorData = typeof(CodexMonitorV2.App).Assembly.GetType("CodexMonitorV2.MonitorData")
    ?? throw new InvalidOperationException("MonitorData type missing");
MethodInfo readModels = monitorData.GetMethod("ReadModelsAsync", BindingFlags.Static | BindingFlags.Public)
    ?? throw new InvalidOperationException("ReadModelsAsync missing");
object modelTask = readModels.Invoke(null, null) ?? throw new InvalidOperationException("Model task missing");
await (Task)modelTask;
IEnumerable models = (IEnumerable)(modelTask.GetType().GetProperty("Result")?.GetValue(modelTask)
    ?? throw new InvalidOperationException("Model result missing"));
int modelCount = models.Cast<object>().Count();
if (modelCount == 0) throw new InvalidOperationException("Codex model catalog is empty");
Console.WriteLine($"OK model/list ({modelCount} models)");

MethodInfo readTasks = monitorData.GetMethod("ReadActiveTasksAsync", BindingFlags.Static | BindingFlags.Public)
    ?? throw new InvalidOperationException("ReadActiveTasksAsync missing");
object taskRead = readTasks.Invoke(null, null) ?? throw new InvalidOperationException("Task read missing");
await (Task)taskRead;
object[] activeTasks = ((IEnumerable)(taskRead.GetType().GetProperty("Result")?.GetValue(taskRead)
    ?? throw new InvalidOperationException("Task result missing"))).Cast<object>().ToArray();
foreach (object activeTask in activeTasks)
{
    string model = (string)(activeTask.GetType().GetProperty("CurrentModel")?.GetValue(activeTask) ?? "");
    string effort = (string)(activeTask.GetType().GetProperty("CurrentEffort")?.GetValue(activeTask) ?? "");
    if (model.Length == 0 || effort.Length == 0)
        throw new InvalidOperationException("Active task model or reasoning effort is empty");
}
Console.WriteLine($"OK active tasks ({activeTasks.Length})");

MethodInfo readQuota = monitorData.GetMethod("ReadQuotaAsync", BindingFlags.Static | BindingFlags.Public)
    ?? throw new InvalidOperationException("ReadQuotaAsync missing");
object quotaTask = readQuota.Invoke(null, null) ?? throw new InvalidOperationException("Quota read missing");
await (Task)quotaTask;
object? quota = quotaTask.GetType().GetProperty("Result")?.GetValue(quotaTask);
if (quota is null) throw new InvalidOperationException("Quota result is empty");
Console.WriteLine("OK account/rateLimits/read");

MethodInfo readDefaults = monitorData.GetMethod("ReadDefaultSettingsAsync", BindingFlags.Static | BindingFlags.Public)
    ?? throw new InvalidOperationException("ReadDefaultSettingsAsync missing");
object defaultsTask = readDefaults.Invoke(null, null) ?? throw new InvalidOperationException("Default settings task missing");
await (Task)defaultsTask;
object defaults = defaultsTask.GetType().GetProperty("Result")?.GetValue(defaultsTask)
    ?? throw new InvalidOperationException("Default settings result missing");
string defaultModel = (string)(defaults.GetType().GetProperty("Model")?.GetValue(defaults) ?? "");
string defaultEffort = (string)(defaults.GetType().GetProperty("Effort")?.GetValue(defaults) ?? "");
Console.WriteLine("OK config/read");

if (Environment.GetEnvironmentVariable("CODEX_MONITOR_PROBE_SETTINGS") == "1")
{
    if (defaultModel.Length == 0 || defaultEffort.Length == 0)
    {
        Console.WriteLine("SKIP config/batchWrite (default model or effort is empty)");
    }
    else
    {
        MethodInfo update = monitorData.GetMethod("UpdateDefaultSettingsAsync", BindingFlags.Static | BindingFlags.Public)
            ?? throw new InvalidOperationException("UpdateDefaultSettingsAsync missing");
        object updateTask = update.Invoke(null, new object[] { defaultModel, defaultEffort })
            ?? throw new InvalidOperationException("Settings task missing");
        await (Task)updateTask;
        object updateResult = updateTask.GetType().GetProperty("Result")?.GetValue(updateTask)
            ?? throw new InvalidOperationException("Settings result missing");
        bool success = (bool)(updateResult.GetType().GetProperty("Success")?.GetValue(updateResult) ?? false);
        string message = (string)(updateResult.GetType().GetProperty("Message")?.GetValue(updateResult) ?? "");
        if (!success) throw new InvalidOperationException(message);
        Console.WriteLine("OK config/batchWrite (same values)");
    }
}
