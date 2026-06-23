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
                                      bool optOn, long redrawChanged, long redrawTotal,
                                      bool cacheHit = false)
        {
            if (!Enabled) return;
            string line =
                $"[{DateTime.Now:HH:mm:ss.fff}]" +
                $"  OPT={(optOn ? "ON " : "off")}" +
                $"  {(cacheHit ? "CACHE=HIT" : "CACHE=---")}" +
                $"  Total={totalMs,4}ms" +
                $"  DocRes={docResMs,3}ms" +
                $"  Cat={catalogMs,3}ms" +
                $"  Rows({rowCount})={rowsMs,4}ms" +
                $"  Post={postPassMs,3}ms" +
                $"  Redraw={redrawChanged}/{redrawTotal}" +
                $"  | {docName.Replace("\r", "").Replace("\n", " ")}";
            Write(line);
        }

        /// <summary>
        /// EXPERIMENT (kit3+4) diagnostic: logs one line per real catalog build, splitting Cat= cost:
        ///   Assets= — asset-library walks (kit3 cached).
        ///   Struct= — PropertySet + Parameter COM walk (kit4 cached); shows "  HIT" when cache served it.
        ///   Rest=   — everything else (docFields + logic sets + sort).
        /// Fires only on a GetCatalog miss regardless of OPT toggle.
        /// </summary>
        public static void LogCatalogBuild(long assetMs, long structMs, long restMs,
                                           bool structHit, string docName)
        {
            if (!Enabled) return;
            string structField = structHit ? "  HIT" : $"{structMs,3}ms";
            Write($"[{DateTime.Now:HH:mm:ss.fff}]  CATBUILD  Assets={assetMs,3}ms  Struct={structField}" +
                  $"  Rest={restMs,3}ms" +
                  $"  | {(docName ?? "").Replace("\r", "").Replace("\n", " ")}");
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
