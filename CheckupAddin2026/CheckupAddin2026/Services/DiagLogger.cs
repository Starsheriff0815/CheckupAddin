using System;
using System.Diagnostics;
using System.IO;

namespace CheckupAddIn.Services
{
    /// <summary>
    /// Lightweight diagnostic logger for issue investigation sessions.
    ///
    /// Disabled by default (Enabled = false). Set Enabled = true and optionally
    /// override LogFile before use:
    ///
    ///   DiagLogger.Enabled = true;
    ///   DiagLogger.LogFile = @"C:\MyPath\diag.txt";   // optional; default = %LOCALAPPDATA%\CheckupAddin\Logs\diag.txt
    ///
    ///   DiagLogger.Section("resize", "RebuildColumns started");
    ///   DiagLogger.Log("resize", $"found {n} grippers");
    ///   DiagLogger.Clear();   // wipe the file before a new test run
    ///
    /// The area parameter becomes a tag in every log line to identify the source
    /// (e.g. "resize", "catalog", "expertmode").
    /// The Logs folder and file are created automatically on first write.
    /// Output also appears in the VS Debug Output window when the debugger is attached.
    /// </summary>
    internal static class DiagLogger
    {
        public static bool Enabled = false;

        /// <summary>
        /// Full path to the log file. Override before setting Enabled = true to redirect output.
        /// Default: %LOCALAPPDATA%\CheckupAddin\Logs\diag.txt
        /// </summary>
        public static string LogFile { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CheckupAddin", "Logs", "diag.txt");

        internal static string S(string s) => s?.Replace("\r", "").Replace("\n", " ") ?? "";

        // ── Core ──────────────────────────────────────────────────────────────

        public static void Log(string area, string message)
        {
            if (!Enabled) return;
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{area}] {message}";
            Debug.WriteLine(line);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile));
                File.AppendAllText(LogFile, line + "\n");
            }
            catch { }
        }

        /// <summary>Writes a visual separator + title to mark the start of a new test phase.</summary>
        public static void Section(string area, string title)
        {
            if (!Enabled) return;
            string bar = new string('─', 60);
            Log(area, $"\n{bar}\n  {title}  [{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{bar}");
        }

        /// <summary>Deletes the log file so the next test run starts from a clean slate.</summary>
        public static void Clear()
        {
            try
            {
                if (File.Exists(LogFile)) File.Delete(LogFile);
            }
            catch { }
        }
    }
}
