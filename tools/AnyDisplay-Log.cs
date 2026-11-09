using System;
using System.Diagnostics;
using System.IO;

internal static class DiagnosticLog
{
    private static readonly object Sync = new object();
    private static readonly string FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AnyDisplay", "logs", "launcher-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Process.GetCurrentProcess().Id + ".log");
    internal static void Note(string message)
    {
        try { lock (Sync) {
            Directory.CreateDirectory(Path.GetDirectoryName(FileName));
            if (File.Exists(FileName) && new FileInfo(FileName).Length > 1048576) return;
            File.AppendAllText(FileName, DateTimeOffset.Now.ToString("o") + " " + message + Environment.NewLine);
        }} catch (Exception) { }
    }
}
