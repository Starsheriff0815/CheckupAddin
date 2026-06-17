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

        private static string LogPath
        {
            get
            {
                if (_logPath == null)
                {
                    string dir = Path.GetDirectoryName(
                        Assembly.GetExecutingAssembly().Location) ?? ".";
                    _logPath = Path.Combine(dir, "refresh_timing.txt");
                }
                return _logPath;
            }
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
                                      int rowCount, string docName)
        {
            if (!Enabled) return;
            string line =
                $"[{DateTime.Now:HH:mm:ss.fff}]" +
                $"  Total={totalMs,4}ms" +
                $"  DocRes={docResMs,3}ms" +
                $"  Cat={catalogMs,3}ms" +
                $"  Rows({rowCount})={rowsMs,4}ms" +
                $"  Post={postPassMs,3}ms" +
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
