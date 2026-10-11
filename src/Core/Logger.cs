using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using Rage;

namespace Cadence.Core
{
    /// <summary>
    /// Thread-safe, non-blocking asynchronous file and console logger for Project Cadence.
    /// Writes clean session logs to plugins/LSPDFR/Cadence/Logs/Cadence.log with automatic
    /// session archiving and 10-session retention.
    /// Strictly complies with the Ponytail architecture as a standalone core utility.
    /// </summary>
    public static class Logger
    {
        private const int MaxQueueCapacity = 4000;
        private const int MaxArchiveFiles = 10;
        private const int FlushIntervalMs = 200;

        private static readonly object _initLock = new object();
        private static readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
        private static readonly AutoResetEvent _wakeEvent = new AutoResetEvent(false);

        private static Thread _workerThread;
        private static StreamWriter _fileWriter;
        private static string _logsDirectory;
        private static string _activeLogPath;
        private static volatile bool _isRunning;
        private static volatile bool _initialized;
        private static long _droppedMessagesCount;

        // Settings toggles (synced from Settings.cs / Cadence.ini)
        public static bool EnableDebugLogging { get; set; } = true;
        public static bool EnableFileLogging { get; set; } = true;

        public static string ActiveLogPath => _activeLogPath;
        public static string LogsDirectory => _logsDirectory;

        public static void Initialize(string preferredBaseDir = null)
        {
            lock (_initLock)
            {
                if (_initialized) return;

                try
                {
                    // 1. Resolve logs directory: plugins/LSPDFR/Cadence/Logs/
                    string baseDir = preferredBaseDir;
                    if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir))
                    {
                        string lspdfrDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins", "LSPDFR");
                        baseDir = Directory.Exists(lspdfrDir)
                            ? lspdfrDir
                            : AppDomain.CurrentDomain.BaseDirectory;
                    }

                    _logsDirectory = Path.Combine(baseDir, "Cadence", "Logs");
                    Directory.CreateDirectory(_logsDirectory);

                    _activeLogPath = Path.Combine(_logsDirectory, "Cadence.log");

                    // 2. Perform Session Archival: if Cadence.log exists from previous run, archive it
                    ArchivePreviousSession();

                    // 3. Migrate legacy loose Cadence.log if present in plugins/LSPDFR
                    MigrateLegacyLogs(baseDir);

                    // 4. Enforce 10-session archive retention
                    EnforceArchiveRetention();

                    // 5. Open stream writer for the new session
                    FileStream fs = new FileStream(_activeLogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    _fileWriter = new StreamWriter(fs, Encoding.UTF8) { AutoFlush = true };

                    // 6. Start asynchronous background worker thread (zero fiber latency)
                    _isRunning = true;
                    _workerThread = new Thread(WorkerLoop)
                    {
                        Name = "Cadence_AsyncLoggerWorker",
                        IsBackground = true,
                        Priority = ThreadPriority.BelowNormal
                    };
                    _workerThread.Start();

                    // Hook domain unload for crash-safe graceful flush
                    AppDomain.CurrentDomain.DomainUnload += OnDomainUnload;
                    AppDomain.CurrentDomain.ProcessExit += OnDomainUnload;

                    _initialized = true;

                    // Write clean session header
                    WriteSessionHeader();
                }
                catch (Exception ex)
                {
                    try
                    {
                        Game.LogTrivial($"[Cadence] [ERROR] Failed initializing Logger: {ex.Message}");
                    }
                    catch { }
                }
            }
        }

        public static void Shutdown()
        {
            lock (_initLock)
            {
                if (!_isRunning) return;
                _isRunning = false;
                _wakeEvent.Set();
            }

            try
            {
                if (_workerThread != null && _workerThread.IsAlive)
                {
                    _workerThread.Join(1000);
                }
            }
            catch { }

            lock (_initLock)
            {
                try
                {
                    DrainQueueToFile();
                    if (_fileWriter != null)
                    {
                        _fileWriter.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [INFO] [Logger] Cadence logging session closed cleanly.");
                        _fileWriter.Flush();
                        _fileWriter.Dispose();
                        _fileWriter = null;
                    }
                }
                catch { }

                try
                {
                    AppDomain.CurrentDomain.DomainUnload -= OnDomainUnload;
                    AppDomain.CurrentDomain.ProcessExit -= OnDomainUnload;
                }
                catch { }

                _initialized = false;
            }
        }

        private static void OnDomainUnload(object sender, EventArgs e)
        {
            Shutdown();
        }

        // ====================================================================
        // Public Logging Methods
        // ====================================================================

        public static void Info(string message) => Write("INFO", null, message);
        public static void Info(string category, string message) => Write("INFO", category, message);

        public static void Warn(string message) => Write("WARN", null, message);
        public static void Warn(string category, string message) => Write("WARN", category, message);

        public static void Debug(string message)
        {
            if (!EnableDebugLogging) return;
            Write("DEBUG", null, message);
        }

        public static void Debug(string category, string message)
        {
            if (!EnableDebugLogging) return;
            Write("DEBUG", category, message);
        }

        public static void Error(string message, Exception ex = null) => Write("ERROR", null, FormatExceptionMessage(message, ex));
        public static void Error(string category, string message, Exception ex = null) => Write("ERROR", category, FormatExceptionMessage(message, ex));

        // ====================================================================
        // Core Write & Dispatch
        // ====================================================================

        private static void Write(string level, string category, string message)
        {
            if (string.IsNullOrEmpty(message)) return;

            string categoryPrefix = string.IsNullOrEmpty(category) ? string.Empty : $"[{category}] ";
            string fullMessage = $"{categoryPrefix}{message}";

            // 1. Output to RagePluginHook Console / log (RagePluginHook.log)
            // Debug messages only echo to RPH log if EnableDebugLogging is active
            if (level != "DEBUG" || EnableDebugLogging)
            {
                try
                {
                    Game.LogTrivial($"[Cadence] [{level}] {fullMessage}");
                }
                catch
                {
                    // Suppressed when RPH Game runtime is not active (e.g. standalone test)
                }
            }

            // 2. Output to Cadence dedicated log file
            if (!EnableFileLogging) return;

            if (!_initialized)
            {
                Initialize();
            }

            // Memory watchdog: drop debug messages if queue capacity is reached
            if (_queue.Count >= MaxQueueCapacity)
            {
                if (level == "DEBUG")
                {
                    Interlocked.Increment(ref _droppedMessagesCount);
                    return;
                }
            }

            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {fullMessage}";
            _queue.Enqueue(line);
            _wakeEvent.Set();
        }

        private static void WorkerLoop()
        {
            while (_isRunning)
            {
                try
                {
                    _wakeEvent.WaitOne(FlushIntervalMs);
                    DrainQueueToFile();
                }
                catch (ThreadAbortException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    try
                    {
                        Game.LogTrivial($"[Cadence] [ERROR] Logger worker thread encountered exception: {ex.Message}");
                    }
                    catch { }
                }
            }

            // Final drain when loop exits
            DrainQueueToFile();
        }

        private static void DrainQueueToFile()
        {
            if (_fileWriter == null) return;

            bool wroteAny = false;
            while (_queue.TryDequeue(out string line))
            {
                try
                {
                    _fileWriter.WriteLine(line);
                    wroteAny = true;
                }
                catch
                {
                    break;
                }
            }

            if (_droppedMessagesCount > 0)
            {
                long dropped = Interlocked.Exchange(ref _droppedMessagesCount, 0);
                if (dropped > 0)
                {
                    try
                    {
                        _fileWriter.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [WARN] [Logger] Dropped {dropped} debug messages due to queue congestion.");
                        wroteAny = true;
                    }
                    catch { }
                }
            }

            if (wroteAny)
            {
                try
                {
                    _fileWriter.Flush();
                }
                catch { }
            }
        }

        private static void WriteSessionHeader()
        {
            string bar = new string('=', 78);
            Write("INFO", "Session", bar);
            Write("INFO", "Session", "  Cadence - Ambient Policing System for LSPDFR");
            Write("INFO", "Session", $"  Session Started : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Write("INFO", "Session", $"  Logs Directory  : {_logsDirectory}");
            Write("INFO", "Session", $"  Active Log File : {_activeLogPath}");
            Write("INFO", "Session", $"  Debug Logging   : {EnableDebugLogging}");
            Write("INFO", "Session", bar);
        }

        // ====================================================================
        // File Archival & Retention Management
        // ====================================================================

        private static void ArchivePreviousSession()
        {
            try
            {
                if (File.Exists(_activeLogPath))
                {
                    FileInfo fi = new FileInfo(_activeLogPath);
                    if (fi.Length > 0)
                    {
                        string timeStamp = fi.LastWriteTime.ToString("yyyyMMdd_HHmmss");
                        string archiveName = $"Cadence_Archive_{timeStamp}.log";
                        string archivePath = Path.Combine(_logsDirectory, archiveName);

                        if (File.Exists(archivePath))
                        {
                            archivePath = Path.Combine(_logsDirectory, $"Cadence_Archive_{timeStamp}_{Guid.NewGuid().ToString().Substring(0, 4)}.log");
                        }

                        File.Move(_activeLogPath, archivePath);
                    }
                    else
                    {
                        File.Delete(_activeLogPath);
                    }
                }
            }
            catch (Exception ex)
            {
                try { Game.LogTrivial($"[Cadence] [WARN] Could not archive previous log: {ex.Message}"); } catch { }
            }
        }

        private static void MigrateLegacyLogs(string baseDir)
        {
            try
            {
                // Check if old Cadence.log exists in plugins/LSPDFR directly
                string legacyPath = Path.Combine(baseDir, "Cadence.log");
                if (File.Exists(legacyPath))
                {
                    FileInfo fi = new FileInfo(legacyPath);
                    string target = Path.Combine(_logsDirectory, $"Cadence_Legacy_{fi.LastWriteTime:yyyyMMdd_HHmmss}.log");
                    try
                    {
                        File.Move(legacyPath, target);
                    }
                    catch
                    {
                        try { File.Delete(legacyPath); } catch { }
                    }
                }

                // Also check old Cadence.old.log
                string oldLegacyPath = Path.Combine(baseDir, "Cadence.old.log");
                if (File.Exists(oldLegacyPath))
                {
                    try { File.Delete(oldLegacyPath); } catch { }
                }
            }
            catch { }
        }

        private static void EnforceArchiveRetention()
        {
            try
            {
                if (!Directory.Exists(_logsDirectory)) return;

                DirectoryInfo dir = new DirectoryInfo(_logsDirectory);
                FileInfo[] archives = dir.GetFiles("Cadence_Archive_*.log");

                if (archives.Length > MaxArchiveFiles)
                {
                    Array.Sort(archives, (a, b) => DateTime.Compare(a.CreationTimeUtc, b.CreationTimeUtc));

                    int toDelete = archives.Length - MaxArchiveFiles;
                    for (int i = 0; i < toDelete; i++)
                    {
                        try
                        {
                            archives[i].Delete();
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private static string FormatExceptionMessage(string message, Exception ex)
        {
            if (ex == null) return message;

            StringBuilder sb = new StringBuilder();
            sb.Append(message);
            sb.Append($" | Exception: {ex.GetType().FullName}: {ex.Message}");

            if (ex.InnerException != null)
            {
                sb.Append($" (Inner: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message})");
            }

            if (!string.IsNullOrEmpty(ex.StackTrace))
            {
                sb.AppendLine();
                sb.Append("   Stack Trace: ");
                sb.Append(ex.StackTrace.Trim());
            }

            return sb.ToString();
        }
    }
}
