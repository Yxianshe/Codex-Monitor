using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace CodexMonitorV2;

internal static class MonitorData
{
    internal sealed record TaskSnapshot(
        string ThreadId,
        string Title,
        string CurrentModel,
        string CurrentEffort,
        string Tier,
        long Tokens);
    internal sealed record ReasoningDescriptor(string Id, string Description);
    internal sealed record ModelDescriptor(
        string Id,
        string DisplayName,
        string DefaultEffort,
        IReadOnlyList<ReasoningDescriptor> Efforts,
        bool IsDefault);
    internal sealed record DefaultSettings(string Model, string Effort);
    internal sealed record SettingsUpdateResult(bool Success, string Message);
    internal sealed record WindowLimit(double UsedPercent, long ResetsAt, double WindowMinutes);
    internal sealed record QuotaSnapshot(WindowLimit? FiveHour, WindowLimit? Weekly);

    private static string _indexStamp = "";
    private static Dictionary<string, string> _titles = new();

    public static async Task<IReadOnlyList<TaskSnapshot>> ReadActiveTasksAsync()
    {
        string? database = FindStateDatabase();
        string sqlite = EnsureSqlite();
        if (database is null) return Array.Empty<TaskSnapshot>();

        string query = "select hex(id), hex(rollout_path), hex(title), hex(coalesce(model, '-')), " +
                       "hex(coalesce(reasoning_effort, '')), coalesce(tokens_used, 0) " +
                       "from threads where archived=0 order by updated_at_ms desc limit 12;";
        ProcessStartInfo info = new(sqlite) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        info.ArgumentList.Add("-tabs");
        info.ArgumentList.Add("-noheader");
        info.ArgumentList.Add(database);
        info.ArgumentList.Add(query);
        using Process process = Process.Start(info)!;
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) return Array.Empty<TaskSnapshot>();

        Dictionary<string, string> titles = ReadTitleIndex();
        List<TaskSnapshot> result = new();
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = line.TrimEnd('\r').Split('\t');
            if (fields.Length < 6) continue;
            string id = HexToString(fields[0]);
            string path = HexToString(fields[1]);
            string databaseTitle = HexToString(fields[2]);
            string databaseModel = HexToString(fields[3]);
            string databaseEffort = HexToString(fields[4]);
            long.TryParse(fields[5], out long tokens);
            RuntimeState runtime = ReadRuntimeState(path);
            if (!runtime.Active) continue;

            string fallback = databaseTitle.Split('\r', '\n')[0].Trim();
            string title = titles.GetValueOrDefault(id, fallback);
            if (title.Length > 80) title = title[..80] + "…";
            string currentModel = string.IsNullOrWhiteSpace(runtime.Model) ? databaseModel : runtime.Model;
            string currentEffort = string.IsNullOrWhiteSpace(runtime.Effort) ? databaseEffort : runtime.Effort;
            result.Add(new TaskSnapshot(
                id,
                title,
                currentModel,
                currentEffort,
                runtime.Tier,
                tokens));
        }
        return result;
    }

    public static async Task<IReadOnlyList<ModelDescriptor>> ReadModelsAsync()
    {
        Process? process = null;
        try
        {
            process = StartAppServer();
            await InitializeAppServerAsync(process, experimental: false);
            JsonElement result = await SendRequestAsync(
                process,
                2,
                "model/list",
                new { limit = 100, includeHidden = false });

            List<ModelDescriptor> models = new();
            foreach (JsonElement item in result.GetProperty("data").EnumerateArray())
            {
                string id = GetString(item, "model");
                if (id.Length == 0) id = GetString(item, "id");
                if (id.Length == 0) continue;

                List<ReasoningDescriptor> efforts = new();
                if (item.TryGetProperty("supportedReasoningEfforts", out JsonElement options))
                {
                    foreach (JsonElement option in options.EnumerateArray())
                    {
                        string effort = GetString(option, "reasoningEffort");
                        if (effort.Length > 0)
                            efforts.Add(new ReasoningDescriptor(effort, GetString(option, "description")));
                    }
                }

                models.Add(new ModelDescriptor(
                    id,
                    FormatModel(id),
                    GetString(item, "defaultReasoningEffort"),
                    efforts,
                    item.TryGetProperty("isDefault", out JsonElement isDefault) && isDefault.GetBoolean()));
            }
            return models;
        }
        catch
        {
            return Array.Empty<ModelDescriptor>();
        }
        finally
        {
            StopAppServer(process);
        }
    }

    public static async Task<DefaultSettings> ReadDefaultSettingsAsync()
    {
        Process? process = null;
        try
        {
            process = StartAppServer();
            await InitializeAppServerAsync(process, experimental: false);
            JsonElement result = await SendRequestAsync(
                process,
                2,
                "config/read",
                new { includeLayers = false });
            JsonElement config = result.GetProperty("config");
            return new DefaultSettings(
                GetString(config, "model"),
                GetString(config, "model_reasoning_effort"));
        }
        catch
        {
            return new DefaultSettings("", "");
        }
        finally
        {
            StopAppServer(process);
        }
    }

    public static async Task<SettingsUpdateResult> UpdateDefaultSettingsAsync(string model, string effort)
    {
        Process? process = null;
        try
        {
            process = StartAppServer();
            await InitializeAppServerAsync(process, experimental: false);
            await SendRequestAsync(
                process,
                2,
                "config/batchWrite",
                new
                {
                    edits = new object[]
                    {
                        new { keyPath = "model", value = model, mergeStrategy = "upsert" },
                        new { keyPath = "model_reasoning_effort", value = effort, mergeStrategy = "upsert" }
                    },
                    reloadUserConfig = true
                });
            return new SettingsUpdateResult(true, "");
        }
        catch (Exception error)
        {
            return new SettingsUpdateResult(false, error.Message);
        }
        finally
        {
            StopAppServer(process);
        }
    }

    public static async Task<QuotaSnapshot?> ReadQuotaAsync()
    {
        using Process process = StartAppServer();
        try
        {
            await InitializeAppServerAsync(process, experimental: false);
            JsonElement result = await SendRequestAsync(process, 2, "account/rateLimits/read", null);
            if (!result.TryGetProperty("rateLimits", out JsonElement limits)
                || limits.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"rateLimits unavailable: {result}");

            List<WindowLimit> windows = new();
            AddLimitWindow(limits, "primary", 300, windows);
            AddLimitWindow(limits, "secondary", 10080, windows);
            WindowLimit? fiveHour = windows
                .Where(item => item.WindowMinutes > 0 && item.WindowMinutes < 1440)
                .OrderBy(item => Math.Abs(item.WindowMinutes - 300))
                .FirstOrDefault();
            WindowLimit? weekly = windows
                .Where(item => item.WindowMinutes >= 1440)
                .OrderBy(item => Math.Abs(item.WindowMinutes - 10080))
                .FirstOrDefault();
            return new QuotaSnapshot(fiveHour, weekly);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[CodexMonitor] rate-limit read failed: {error.Message}");
            return null;
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
        }
    }

    private static Process StartAppServer()
    {
        string codex = FindCodexExecutable()
            ?? throw new FileNotFoundException("找不到 Codex 可执行文件");
        ProcessStartInfo info = new(codex, "app-server --stdio")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        return Process.Start(info) ?? throw new InvalidOperationException("无法启动 Codex app-server");
    }

    private static async Task InitializeAppServerAsync(Process process, bool experimental)
    {
        object capabilities = experimental ? new { experimentalApi = true } : new { };
        await SendRequestAsync(
            process,
            1,
            "initialize",
            new
            {
                clientInfo = new { name = "codex-monitor", title = "Codex Monitor", version = "2.1.2" },
                capabilities
            });
        await WriteMessageAsync(process, new { jsonrpc = "2.0", method = "initialized", @params = new { } });
    }

    private static async Task<JsonElement> SendRequestAsync(
        Process process,
        int id,
        string method,
        object? parameters)
    {
        await WriteMessageAsync(process, new { jsonrpc = "2.0", id, method, @params = parameters });
        DateTime deadline = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            string? line = await process.StandardOutput.ReadLineAsync().WaitAsync(remaining);
            if (string.IsNullOrWhiteSpace(line)) continue;
            using JsonDocument message = JsonDocument.Parse(line);
            if (!message.RootElement.TryGetProperty("id", out JsonElement responseId)
                || responseId.ValueKind != JsonValueKind.Number
                || responseId.GetInt32() != id)
                continue;

            if (message.RootElement.TryGetProperty("error", out JsonElement error))
                throw new InvalidOperationException(GetString(error, "message") is { Length: > 0 } text ? text : error.ToString());
            return message.RootElement.GetProperty("result").Clone();
        }
        throw new TimeoutException($"Codex 请求超时：{method}");
    }

    private static async Task WriteMessageAsync(Process process, object message)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
        await process.StandardInput.FlushAsync();
    }

    private static void StopAppServer(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(true);
        }
        catch { }
        process.Dispose();
    }

    private static WindowLimit ParseLimit(JsonElement item) => new(
        item.TryGetProperty("usedPercent", out JsonElement used) ? used.GetDouble() : 0,
        item.TryGetProperty("resetsAt", out JsonElement reset) && reset.ValueKind == JsonValueKind.Number ? reset.GetInt64() : 0,
        item.TryGetProperty("windowDurationMins", out JsonElement window) && window.ValueKind == JsonValueKind.Number ? window.GetDouble() : 0);

    private static void AddLimitWindow(
        JsonElement snapshot,
        string name,
        double fallbackWindowMinutes,
        List<WindowLimit> output)
    {
        if (snapshot.TryGetProperty(name, out JsonElement item) && item.ValueKind == JsonValueKind.Object)
        {
            WindowLimit limit = ParseLimit(item);
            output.Add(limit.WindowMinutes > 0
                ? limit
                : limit with { WindowMinutes = fallbackWindowMinutes });
        }
    }

    private static RuntimeState ReadRuntimeState(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return new(false, "", "", "default");
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long position = stream.Length;
        string suffix = "";
        string model = "", effort = "", tier = "default";
        bool? active = null;
        const int chunkSize = 1024 * 1024;
        long scanned = 0;

        while (position > 0 && scanned < 128L * 1024 * 1024 && (active is null || (active.Value && model.Length == 0)))
        {
            int count = (int)Math.Min(chunkSize, position);
            position -= count;
            stream.Seek(position, SeekOrigin.Begin);
            byte[] bytes = new byte[count];
            int read = stream.Read(bytes, 0, count);
            string text = Encoding.UTF8.GetString(bytes, 0, read) + suffix;
            scanned += read;

            string[] lines = text.Split('\n');
            int firstCompleteLine = position > 0 ? 1 : 0;
            for (int index = lines.Length - 1; index >= firstCompleteLine; index--)
            {
                ReadRuntimeLine(lines[index], ref active, ref model, ref effort, ref tier);
                if (active == false || active == true && model.Length > 0 && effort.Length > 0) break;
            }

            suffix = position > 0 && lines.Length > 0 && lines[0].Length <= 8 * 1024 * 1024
                ? lines[0]
                : "";
        }
        return new RuntimeState(active == true, model, effort, tier);
    }

    private static void ReadRuntimeLine(
        string line,
        ref bool? active,
        ref string model,
        ref string effort,
        ref string tier)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        try
        {
            using JsonDocument document = JsonDocument.Parse(line.TrimEnd('\r'));
            JsonElement root = document.RootElement;
            string rootType = GetString(root, "type");
            JsonElement payload = root.TryGetProperty("payload", out JsonElement value) ? value : root;
            string eventType = rootType == "event_msg" ? GetString(payload, "type") : rootType;

            if (active is null)
            {
                if (eventType == "task_started") active = true;
                else if (eventType is "task_complete" or "turn_aborted") active = false;
            }

            if (rootType == "turn_context" && model.Length == 0)
            {
                model = GetString(payload, "model");
                effort = GetString(payload, "effort");
                tier = GetString(payload, "service_tier");
                if (effort.Length == 0
                    && payload.TryGetProperty("collaboration_mode", out JsonElement collaboration)
                    && collaboration.TryGetProperty("settings", out JsonElement settings))
                    effort = GetString(settings, "reasoning_effort");
            }
            else if (eventType == "thread_settings_applied" && model.Length == 0)
            {
                model = GetString(payload, "model");
                effort = GetString(payload, "reasoning_effort");
                tier = GetString(payload, "service_tier");
            }

            if (tier.Length == 0) tier = "default";
        }
        catch (JsonException)
        {
            // Ignore a partially written final JSONL record and retry on the next refresh.
        }
    }

    private static string HexToString(string hex)
    {
        if (hex.Length == 0) return "";
        byte[] bytes = Convert.FromHexString(hex);
        return Encoding.UTF8.GetString(bytes);
    }

    private static Dictionary<string, string> ReadTitleIndex()
    {
        string path = Path.Combine(CodexHome, "session_index.jsonl");
        if (!File.Exists(path)) return _titles;
        FileInfo file = new(path);
        string stamp = $"{file.Length}:{file.LastWriteTimeUtc.Ticks}";
        if (stamp == _indexStamp) return _titles;

        Dictionary<string, string> next = new();
        foreach (string line in File.ReadLines(path))
        {
            try
            {
                using JsonDocument item = JsonDocument.Parse(line);
                string id = GetString(item.RootElement, "id");
                string title = GetString(item.RootElement, "thread_name");
                if (id.Length > 0 && title.Length > 0) next[id] = title;
            }
            catch { }
        }
        _indexStamp = stamp;
        return _titles = next;
    }

    private static string? FindStateDatabase() => Directory.Exists(CodexHome)
        ? Directory.GetFiles(CodexHome, "state_*.sqlite").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
        : null;

    private static string EnsureSqlite()
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexMonitorV2");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "sqlite3.exe");
        using Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream("PortableSqlite")
            ?? throw new InvalidOperationException("Embedded sqlite3.exe is missing.");
        if (!File.Exists(path) || new FileInfo(path).Length != input.Length)
        {
            using FileStream output = new(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            input.CopyTo(output);
        }
        return path;
    }

    private static string? FindCodexExecutable()
    {
        IEnumerable<string> paths = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => Path.Combine(path.Trim('"'), "codex.exe"));
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        paths = paths.Append(Path.Combine(local, "Programs", "OpenAI", "Codex", "bin", "codex.exe"));
        string root = Path.Combine(local, "OpenAI", "Codex", "bin");
        if (Directory.Exists(root)) paths = paths.Concat(Directory.GetFiles(root, "codex.exe", SearchOption.AllDirectories));
        return paths.FirstOrDefault(File.Exists);
    }

    private static string FormatModel(string model)
    {
        string value = model.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) ? model[4..] : model;
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Replace('-', ' '));
    }

    private static string FormatEffort(string effort) => effort switch
    {
        "minimal" => "最低", "low" => "低", "medium" => "中", "high" => "高",
        "xhigh" => "极高", "max" => "最高", "ultra" => "超高", _ => effort
    };

    private static string GetString(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : "";
    private static string CodexHome => Environment.GetEnvironmentVariable("CODEX_HOME") ??
                                       Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    private sealed record RuntimeState(bool Active, string Model, string Effort, string Tier);
}
