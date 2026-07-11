using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

internal static class Launcher
{
    private static void Extract(string resourceName, string targetPath)
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        using (Stream input = assembly.GetManifestResourceStream(resourceName))
        using (FileStream output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            if (input == null) throw new InvalidOperationException("Missing resource: " + resourceName);
            input.CopyTo(output);
        }
    }

    [STAThread]
    private static void Main()
    {
        try
        {
            string appDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexTaskMonitor");
            Directory.CreateDirectory(appDir);

            string script = Path.Combine(appDir, "monitor.ps1");
            Extract("MonitorScript", script);
            Extract("PortableSqlite", Path.Combine(appDir, "sqlite3.exe"));
            Extract("CodexIcon", Path.Combine(appDir, "Codex.ico"));

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File \"" + script + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch (Exception error)
        {
            System.Windows.Forms.MessageBox.Show(
                error.Message,
                "Codex Task Monitor",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Error);
        }
    }
}
