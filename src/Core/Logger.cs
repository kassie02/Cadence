using System;
using System.IO;
using Rage;

namespace Cadence.Core
{
    public static class Logger
    {
        private static readonly object _lock = new object();
        private static string _logPath;
        private static bool _initialized;

        public static bool EnableDebugLogging { get; set; } = true;
        public static bool EnableFileLogging { get; set; } = true;

        public static void Initialize(string preferredDir = null)
        {
            lock (_lock)
            {
                if (_initialized) return;

                try
                {
                    string baseDir = preferredDir;
                    if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir))
                    {
                        string lspdfrDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins", "LSPDFR");
                        baseDir = Directory.Exists(lspdfrDir) ? lspdfrDir : AppDomain.CurrentDomain.BaseDirectory;
                    }

                    _logPath = Path.Combine(baseDir, "Cadence.log");

                    // Log file rollover: if exceeds 2MB, move to .old.log
                    if (File.Exists(_logPath))
                    {
                        var fileInfo = new FileInfo(_logPath);
                        if (fileInfo.Length > 2 * 1024 * 1024)
                        {
                            string oldLog = Path.Combine(baseDir, "Cadence.old.log");
                            try
                            {
                                if (File.Exists(oldLog))
                                {
                                    File.Delete(oldLog);
                                }
                                File.Move(_logPath, oldLog);
                            }
                            catch
                            {
                                // Rollover failed; continue appending
                            }
                        }
                    }

                    _initialized = true;
                }
                catch (Exception ex)
                {
                    try
                    {
                        Game.LogTrivial($"[Cadence] [ERROR] Failed initializing logger path: {ex.Message}");
                    }
                    catch { }
                }
            }
        }

        public static void Info(string message)
        {
            Write("INFO", message);
        }

        public static void Warn(string message)
        {
            Write("WARN", message);
        }

        public static void Debug(string message)
        {
            if (!EnableDebugLogging) return;
            Write("DEBUG", message);
        }

        public static void Error(string message, Exception ex = null)
        {
            string full = message;
            if (ex != null)
            {
                full += $" | Exception: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
                if (ex.InnerException != null)
                {
                    full += $"\n  Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}";
                }
            }
            Write("ERROR", full);
        }

        private static void Write(string level, string message)
        {
            string formatted = $"[Cadence] [{level}] {message}";

            // 1. Output to RagePluginHook Game console / log
            try
            {
                Game.LogTrivial(formatted);
            }
            catch
            {
                // Suppressed if Game runtime not active (e.g. unit tests)
            }

            // 2. Output to dedicated log file
            if (!EnableFileLogging) return;

            lock (_lock)
            {
                try
                {
                    if (!_initialized || string.IsNullOrEmpty(_logPath))
                    {
                        Initialize();
                    }

                    if (!string.IsNullOrEmpty(_logPath))
                    {
                        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";
                        File.AppendAllText(_logPath, line);
                    }
                }
                catch
                {
                    // Prevent logger crash from impacting game loop
                }
            }
        }
    }
}
