using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        private const int MaxQueueCapacity = 5000;
        private const int MaxArchiveFiles = 10;
        private const int FlushIntervalMs = 200;

        private static readonly object _initLock = new object();
        private static readonly object _writerLock = new object();
        private static readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
        private static readonly ManualResetEventSlim _workerStopped = new ManualResetEventSlim(false);
        private static AutoResetEvent _wakeEvent = new AutoResetEvent(false);

        private static Thread _workerThread;
        private static StreamWriter _fileWriter;
        private static string _logsDirectory;
        private static string _activeLogPath;
        private static volatile bool _isRunning;
        private static volatile bool _initialized;
        private static long _droppedDebugCount;
        private static long _droppedNonDebugCount;

        // Settings toggles (synced from Settings.cs / Cadence.ini)
        public static bool EnableDebugLogging { get; set; } = true;
        public static bool EnableFileLogging { get; set; } = true;

        public static string ActiveLogPath => _activeLogPath;
        public static string LogsDirectory => _logsDirectory;

        /// <summary>
        /// Deterministic initialization called during plugin startup [A4].
        /// Heavy operations (directories, archiving, file stream, worker thread) run here exclusively.
        /// </summary>
        public static void Initialize(string preferredBaseDir = null)
        {
            lock (_initLock)
            {
                if (_initialized) return;

                try
                {
                    // Ensure wake event exists and is reset
                    if (_wakeEvent == null)
                    {
                        _wakeEvent = new AutoResetEvent(false);
                    }

                    _workerStopped.Reset();

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

                    // 5. Open stream writer for the new session under lock
                    lock (_writerLock)
                    {
                        FileStream fs = new FileStream(_activeLogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                        _fileWriter = new StreamWriter(fs, Encoding.UTF8) { AutoFlush = true };
                    }

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
                        TryLogToRph($"[Cadence] [ERROR] Failed initializing Logger: {ex.Message}");
                    }
                    catch { }
                }
            }
        }

        /// <summary>
        /// Graceful logger shutdown with bounded worker wait and clean stream disposal [A2, A23].
        /// </summary>
        public static void Shutdown()
        {
            lock (_initLock)
            {
                if (!_isRunning && !_initialized) return;
                _isRunning = false;
                _workerStopped.Reset();

                try
                {
                    _wakeEvent?.Set();
                }
                catch { }
            }

            try
            {
                if (_workerThread != null && _workerThread.IsAlive)
                {
                    // Wait for worker to signal complete stop [A2]
                    if (!_workerStopped.Wait(1500))
                    {
                        _workerThread.Join(500);
                    }
                }
            }
            catch { }
            finally
            {
                _workerThread = null;
            }

            lock (_initLock)
            {
                try
                {
                    SessionMetrics.LogSummary();
                    DrainQueueToFile();

                    lock (_writerLock)
                    {
                        if (_fileWriter != null)
                        {
                            _fileWriter.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [INFO] [Logger] Cadence logging session closed cleanly.");
                            _fileWriter.Flush();
                            _fileWriter.Dispose();
                            _fileWriter = null;
                        }
                    }
                }
                catch { }

                try
                {
                    AppDomain.CurrentDomain.DomainUnload -= OnDomainUnload;
                    AppDomain.CurrentDomain.ProcessExit -= OnDomainUnload;
                }
                catch { }

                try
                {
                    _wakeEvent?.Dispose(); // [A23]
                    _wakeEvent = null;
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

            // 1. Output to RagePluginHook Console / log (RagePluginHook.log) [A14]
            // Mirror only WARN and ERROR to avoid flooding RagePluginHook.log
            if (level == "WARN" || level == "ERROR")
            {
                try
                {
                    TryLogToRph($"[Cadence] [{level}] {fullMessage}");
                }
                catch
                {
                    // Suppressed when RPH Game runtime is not active (e.g. standalone test)
                }
            }

            // 2. Output to Cadence dedicated log file
            if (!EnableFileLogging) return;

            // [A4] Lazy init fallback: if not initialized, do not perform heavy disk I/O on calling fiber
            if (!_initialized)
            {
                return;
            }

            // [A5] Bounded queue watchdog: cap queue at MaxQueueCapacity
            if (_queue.Count >= MaxQueueCapacity)
            {
                if (level == "DEBUG")
                {
                    Interlocked.Increment(ref _droppedDebugCount);
                    return;
                }
                else
                {
                    // Drop oldest non-debug log line to make room for new critical entries
                    if (_queue.TryDequeue(out _))
                    {
                        Interlocked.Increment(ref _droppedNonDebugCount);
                    }
                }
            }

            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {fullMessage}";
            _queue.Enqueue(line);

            try
            {
                _wakeEvent?.Set();
            }
            catch { }
        }

        private static void WorkerLoop()
        {
            try
            {
                while (_isRunning)
                {
                    try
                    {
                        _wakeEvent?.WaitOne(FlushIntervalMs);
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
                            TryLogToRph($"[Cadence] [ERROR] Logger worker thread encountered exception: {ex.Message}");
                        }
                        catch { }
                    }
                }

                // Final drain when loop exits
                DrainQueueToFile();
            }
            finally
            {
                _workerStopped.Set();
            }
        }

        private static void DrainQueueToFile()
        {
            lock (_writerLock)
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

                long droppedDbg = Interlocked.Exchange(ref _droppedDebugCount, 0);
                if (droppedDbg > 0)
                {
                    try
                    {
                        _fileWriter.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [WARN] [Logger] Dropped {droppedDbg} debug messages due to queue congestion.");
                        wroteAny = true;
                    }
                    catch { }
                }

                long droppedNonDbg = Interlocked.Exchange(ref _droppedNonDebugCount, 0);
                if (droppedNonDbg > 0)
                {
                    try
                    {
                        _fileWriter.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [WARN] [Logger] Dropped {droppedNonDbg} older non-debug messages due to queue congestion.");
                        wroteAny = true;
                    }
                    catch { }
                }

                if (wroteAny)
                {
                    try
                    {
                        _fileWriter.Flush();
                        CheckMidSessionRotation();
                    }
                    catch { }
                }
            }
        }

        private static void CheckMidSessionRotation()
        {
            try
            {
                if (_fileWriter != null && _fileWriter.BaseStream != null)
                {
                    // 10 MB maximum log file size before automatic mid-session rotation
                    if (_fileWriter.BaseStream.Length > 10 * 1024 * 1024)
                    {
                        _fileWriter.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [INFO] [Logger] Session log exceeded 10MB limit. Rotating archive...");
                        _fileWriter.Flush();
                        _fileWriter.Dispose();
                        _fileWriter = null;

                        ArchivePreviousSession();
                        EnforceArchiveRetention();

                        FileStream fs = new FileStream(_activeLogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                        _fileWriter = new StreamWriter(fs, Encoding.UTF8) { AutoFlush = true };
                        WriteSessionHeader();
                    }
                }
            }
            catch { }
        }

        // ====================================================================
        // File Archival & Maintenance Helpers
        // ====================================================================

        private static void WriteSessionHeader()
        {
            lock (_writerLock)
            {
                if (_fileWriter == null) return;
                try
                {
                    string bar = new string('=', 78);
                    _fileWriter.WriteLine(bar);
                    _fileWriter.WriteLine($"  Project Cadence - Ambient Policing System for LSPDFR");
                    _fileWriter.WriteLine($"  Session Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (Local)");
                    _fileWriter.WriteLine($"  Log Destination: {_activeLogPath}");
                    _fileWriter.WriteLine(bar);
                    _fileWriter.Flush();
                }
                catch { }
            }
        }

        private static void ArchivePreviousSession()
        {
            try
            {
                if (File.Exists(_activeLogPath))
                {
                    FileInfo fi = new FileInfo(_activeLogPath);
                    if (fi.Length > 0)
                    {
                        string timestamp = fi.LastWriteTime.ToString("yyyyMMdd_HHmmss");
                        string archiveName = $"Cadence_{timestamp}.log";
                        string archivePath = Path.Combine(_logsDirectory, archiveName);

                        if (File.Exists(archivePath))
                        {
                            archivePath = Path.Combine(_logsDirectory, $"Cadence_{timestamp}_{Guid.NewGuid().ToString().Substring(0, 4)}.log");
                        }

                        File.Move(_activeLogPath, archivePath);
                    }
                    else
                    {
                        File.Delete(_activeLogPath);
                    }
                }
            }
            catch { }
        }

        private static void MigrateLegacyLogs(string baseDir)
        {
            try
            {
                string legacyPath = Path.Combine(baseDir, "Cadence.log");
                if (File.Exists(legacyPath))
                {
                    FileInfo fi = new FileInfo(legacyPath);
                    string timestamp = fi.LastWriteTime.ToString("yyyyMMdd_HHmmss");
                    string dest = Path.Combine(_logsDirectory, $"Cadence_legacy_{timestamp}.log");
                    File.Move(legacyPath, dest);
                }
            }
            catch { }
        }

        private static void EnforceArchiveRetention()
        {
            try
            {
                if (!Directory.Exists(_logsDirectory)) return;

                DirectoryInfo di = new DirectoryInfo(_logsDirectory);
                FileInfo[] allLogs = di.GetFiles("Cadence_*.log");

                List<FileInfo> archives = new List<FileInfo>();
                foreach (FileInfo f in allLogs)
                {
                    if (!f.Name.Equals("Cadence.log", StringComparison.OrdinalIgnoreCase))
                    {
                        archives.Add(f);
                    }
                }

                if (archives.Count > MaxArchiveFiles)
                {
                    // Sort by LastWriteTimeUtc [A23]
                    archives.Sort((a, b) => DateTime.Compare(a.LastWriteTimeUtc, b.LastWriteTimeUtc));

                    int toDelete = archives.Count - MaxArchiveFiles;
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

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void TryLogToRph(string message)
        {
            Game.LogTrivial(message);
        }
    }

    /// <summary>
    /// Thread-safe lifetime session telemetry counter.
    /// Emits structured session summaries upon plugin unload or duty transitions.
    /// </summary>
    public static class SessionMetrics
    {
        private static long _totalPatrol;
        private static long _totalStops;
        private static long _totalStationary;
        private static long _totalEmergency;
        private static long _totalPursuit;
        private static long _totalThreat;
        private static readonly DateTime _sessionStart = DateTime.Now;

        public static void IncrementPatrol() => Interlocked.Increment(ref _totalPatrol);
        public static void IncrementTrafficStop() => Interlocked.Increment(ref _totalStops);
        public static void IncrementStationary() => Interlocked.Increment(ref _totalStationary);
        public static void IncrementEmergencyRun() => Interlocked.Increment(ref _totalEmergency);
        public static void IncrementPursuit() => Interlocked.Increment(ref _totalPursuit);
        public static void IncrementThreat() => Interlocked.Increment(ref _totalThreat);

        public static void LogSummary()
        {
            TimeSpan uptime = DateTime.Now - _sessionStart;
            string bar = new string('=', 78);
            Logger.Info("Session", bar);
            Logger.Info("Session", "  Cadence Session Telemetry & Statistics Summary");
            Logger.Info("Session", $"  Session Uptime            : {uptime.Hours:D2}h {uptime.Minutes:D2}m {uptime.Seconds:D2}s");
            Logger.Info("Session", $"  Patrol Units Deployed     : {_totalPatrol}");
            Logger.Info("Session", $"  Traffic Stops Executed    : {_totalStops}");
            Logger.Info("Session", $"  Speed Traps Deployed      : {_totalStationary}");
            Logger.Info("Session", $"  Emergency 911 Dispatches  : {_totalEmergency}");
            Logger.Info("Session", $"  Pursuits Assisted         : {_totalPursuit}");
            Logger.Info("Session", $"  Threat Defenses Engaged   : {_totalThreat}");
            Logger.Info("Session", bar);
        }
    }
}
