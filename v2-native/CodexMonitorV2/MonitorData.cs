using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace CodexMonitorV2;

internal static class MonitorData
{
    internal sealed record TaskSnapshot(string Title, string Detail, long Tokens);
    internal sealed record WindowLimit(double UsedPercent, long ResetsAt, double WindowMinutes);
    internal sealed record QuotaSnapshot(WindowLimit Primary, WindowLimit Secondary);

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
            string model = string.IsNullOrWhiteSpace(runtime.Model) ? databaseModel : runtime.Model;
            string effort = string.IsNullOrWhiteSpace(runtime.Effort) ? databaseEffort : runtime.Effort;
            string tier = runtime.Tier;
            string detail = $"{(tier == "priority" ? "⚡ " : "")}{FormatModel(model)}  {FormatEffort(effort)}  {(tier == "priority" ? "1.5×" : "标准")}";
            result.Add(new TaskSnapshot(title, detail, tokens));
        }
        return result;
    }

    public static async Task<QuotaSnapshot?> ReadQuotaAsync()
    {
        string? codex = FindCodexExecutable();
        if (codex is null) return null;

        ProcessStartInfo info = new(codex, "app-server --stdio")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false)
        };
        using Process process = Process.Start(info)!;
        try
        {
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"codex-monitor-v2\",\"version\":\"2.0\"},\"capabilities\":null}}");
            await process.StandardInput.FlushAsync();
            if (!await WaitForResponseAsync(process, 1)) return null;

            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"initialized\"}");
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"account/rateLimits/read\",\"params\":null}");
            await process.StandardInput.FlushAsync();

            for (int i = 0; i < 8; i++)
            {
                string? line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
                if (string.IsNullOrWhiteSpace(line)) return null;
                using JsonDocument message = JsonDocument.Parse(line);
                if (!message.RootElement.TryGetProperty("id", out JsonElement id) || id.GetInt32() != 2) continue;
                JsonElement limits = message.RootElement.GetProperty("result").GetProperty("rateLimits");
                return new QuotaSnapshot(ParseLimit(limits.GetProperty("primary")), ParseLimit(limits.GetProperty("secondary")));
            }
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
        }
    }

    private static async Task<bool> WaitForResponseAsync(Process process, int expectedId)
    {
        for (int i = 0; i < 6; i++)
        {
            string? line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8));
            if (string.IsNullOrWhiteSpace(line)) return false;
            using JsonDocument message = JsonDocument.Parse(line);
            if (message.RootElement.TryGetProperty("id", out JsonElement id) && id.GetInt32() == expectedId) return true;
        }
        return false;
    }

    private static WindowLimit ParseLimit(JsonElement item) => new(
        item.TryGetProperty("usedPercent", out JsonElement used) ? used.GetDouble() : 0,
        item.TryGetProperty("resetsAt", out JsonElement reset) && reset.ValueKind == JsonValueKind.Number ? reset.GetInt64() : 0,
        item.TryGetProperty("windowDurationMins", out JsonElement window) ? window.GetDouble() : 0);

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

            if (active is null)
            {
                int start = text.LastIndexOf("\"type\":\"task_started\"", StringComparison.Ordinal);
                int complete = text.LastIndexOf("\"type\":\"task_complete\"", StringComparison.Ordinal);
                int aborted = text.LastIndexOf("\"type\":\"turn_aborted\"", StringComparison.Ordinal);
                if (start >= 0 || complete >= 0 || aborted >= 0) active = start > Math.Max(complete, aborted);
            }

            int settings = text.LastIndexOf("\"type\":\"thread_settings_applied\"", StringComparison.Ordinal);
            if (settings >= 0)
            {
                string snippet = text.Substring(settings, Math.Min(8192, text.Length - settings));
                model = JsonField(snippet, "model");
                effort = JsonField(snippet, "reasoning_effort");
                tier = JsonField(snippet, "service_tier");
                if (tier.Length == 0) tier = "default";
            }
            suffix = text[..Math.Min(8192, text.Length)];
        }
        return new RuntimeState(active == true, model, effort, tier);
    }

    private static string JsonField(string text, string name)
    {
        string marker = $"\"{name}\":\"";
        int start = text.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return "";
        start += marker.Length;
        int end = text.IndexOf('"', start);
        return end > start ? text[start..end] : "";
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
