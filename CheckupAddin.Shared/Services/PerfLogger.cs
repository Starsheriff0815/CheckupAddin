using System;
using System.IO;
using System.Reflection;

namespace CheckupAddIn.Services
{
    /// <summary>
    /// Lightweight performance logger for refresh timing analysis.
    /// Writes one compact line per refresh to refresh_timing.txt in the add-in directory.
    /// Rotates to refresh_timing.bak when the file exceeds 2 MB.
    /// Set Enabled = false to silence all output.
    /// </summary>
    internal static class PerfLogger
    {
        public static bool Enabled = false;

        private const long MaxFileSizeBytes = 2 * 1024 * 1024;

        private static string _logPath;
        private static string _dir;

        private static string Dir =>
            _dir ?? (_dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".");

        private static string LogPath =>
            _logPath ?? (_logPath = Path.Combine(Dir, "refresh_timing.txt"));

        /// <summary>
        /// EXPERIMENT toggle (branch experiment/optimize): optimizations are ON when a file
        /// named "perf_opt.on" exists next to the DLL. Lets the tester capture before/after
        /// in a single Inventor session by creating/deleting that file (effect on next refresh).
        /// </summary>
        public static bool OptimizationsOn()
        {
            try { return File.Exists(Path.Combine(Dir, "perf_opt.on")); }
            catch { return false; }
        }

        /// <summary>Logs window-open latency (ribbon click → window shown), in milliseconds.</summary>
        public static void LogOpen(long ms, bool optOn)
        {
            if (!Enabled) return;
            Write($"[{DateTime.Now:HH:mm:ss.fff}]  OPEN={ms,4}ms  OPT={(optOn ? "ON " : "off")}");
        }

        /// <summary>Writes a session-start marker. Call once when the add-in activates.</summary>
        public static void LogSession(string label)
        {
            if (!Enabled) return;
            Write($"═══ {label}  [{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ═══");
        }

        /// <summary>
        /// Writes one timing line for a completed DoRefreshCore call.
        /// Times in milliseconds; rowCount = number of configured rows; docName = primary document filename.
        /// </summary>
        public static void LogRefresh(long totalMs, long docResMs, long catalogMs,
                                      long rowsMs, long postPassMs,
                                      int rowCount, string docName,
                                      bool optOn, long redrawChanged, long redrawTotal)
        {
            if (!Enabled) return;
            string line =
                $"[{DateTime.Now:HH:mm:ss.fff}]" +
                $"  OPT={(optOn ? "ON " : "off")}" +
                $"  Total={totalMs,4}ms" +
                $"  DocRes={docResMs,3}ms" +
                $"  Cat={catalogMs,3}ms" +
                $"  Rows({rowCount})={rowsMs,4}ms" +
                $"  Post={postPassMs,3}ms" +
                $"  Redraw={redrawChanged}/{redrawTotal}" +
                $"  | {docName.Replace("\r", "").Replace("\n", " ")}";
            Write(line);
        }

        private static void Write(string line)
        {
            try
            {
                string path = LogPath;
                RotateIfNeeded(path);
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch { }
        }

        private static void RotateIfNeeded(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                if (new FileInfo(path).Length < MaxFileSizeBytes) return;
                string bak = path + ".bak";
                if (File.Exists(bak)) File.Delete(bak);
                File.Move(path, bak);
            }
            catch { }
        }
    }
}
