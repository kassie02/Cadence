using System;
using System.Collections.Generic;
using System.Linq;
using Cadence.Core;
using Cadence.Features;

namespace Cadence.Common
{
    /// <summary>
    /// Thread-safe registry holding all active IFeature implementations.
    /// Manages unified lifecycle dispatch (PurgeAll, StopAll) with exception isolation.
    /// </summary>
    public static class FeatureRegistry
    {
        private static readonly object _syncLock = new object();
        private static readonly Dictionary<string, IFeature> _features = new Dictionary<string, IFeature>(StringComparer.OrdinalIgnoreCase);

        static FeatureRegistry()
        {
            InitializeDefaults();
        }

        /// <summary>
        /// Registers default Cadence features directly via their IFeature implementations.
        /// </summary>
        public static void InitializeDefaults()
        {
            lock (_syncLock)
            {
                if (_features.Count > 0) return;

                Register(PatrolFeature.Instance);
                Register(TrafficStopFeature.Instance);
                Register(StationaryFeature.Instance);
                Register(ResponseFeature.Instance);
                Register(PursuitFeature.Instance);
                Register(AssistFeature.Instance);
                Register(BackupFeature.Instance);
            }
        }

        /// <summary>
        /// Registers a feature with the registry.
        /// </summary>
        public static void Register(IFeature feature)
        {
            if (feature == null) return;

            lock (_syncLock)
            {
                _features[feature.Name] = feature;
                if (!feature.Name.EndsWith("Feature", StringComparison.OrdinalIgnoreCase))
                {
                    _features[feature.Name + "Feature"] = feature;
                }
            }
        }

        /// <summary>
        /// Unregisters a feature by name.
        /// </summary>
        public static bool Unregister(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            lock (_syncLock)
            {
                bool removed = _features.Remove(name);
                if (!name.EndsWith("Feature", StringComparison.OrdinalIgnoreCase))
                {
                    removed |= _features.Remove(name + "Feature");
                }
                else
                {
                    removed |= _features.Remove(name.Substring(0, name.Length - "Feature".Length));
                }
                return removed;
            }
        }

        /// <summary>
        /// Retrieves a feature by name, or null if not registered.
        /// </summary>
        public static IFeature Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            lock (_syncLock)
            {
                if (_features.TryGetValue(name, out var feature))
                {
                    return feature;
                }

                if (!name.EndsWith("Feature", StringComparison.OrdinalIgnoreCase) &&
                    _features.TryGetValue(name + "Feature", out feature))
                {
                    return feature;
                }

                if (name.EndsWith("Feature", StringComparison.OrdinalIgnoreCase) &&
                    _features.TryGetValue(name.Substring(0, name.Length - "Feature".Length), out feature))
                {
                    return feature;
                }

                return null;
            }
        }

        /// <summary>
        /// Returns a snapshot list of all registered features.
        /// </summary>
        public static IReadOnlyList<IFeature> GetAll()
        {
            lock (_syncLock)
            {
                return _features.Values.Distinct().ToList().AsReadOnly();
            }
        }

        /// <summary>
        /// Purges entities across all registered features with per-feature exception isolation.
        /// Per the inverted teardown lifecycle, purge runs BEFORE stop.
        /// </summary>
        /// <param name="immediateDelete">If true, deletes entities immediately; otherwise performs clean dismissal.</param>
        public static void PurgeAll(bool immediateDelete)
        {
            IReadOnlyList<IFeature> features = GetAll();
            foreach (var feature in features)
            {
                try
                {
                    feature.PurgeAll(immediateDelete);
                }
                catch (Exception ex)
                {
                    Logger.Error($"[FeatureRegistry] Error purging feature '{feature.Name}'", ex);
                }
            }
        }

        /// <summary>
        /// Signals all registered features to stop background fibers with per-feature exception isolation.
        /// </summary>
        public static void StopAll()
        {
            IReadOnlyList<IFeature> features = GetAll();
            foreach (var feature in features)
            {
                try
                {
                    feature.Stop();
                }
                catch (Exception ex)
                {
                    Logger.Error($"[FeatureRegistry] Error stopping feature '{feature.Name}'", ex);
                }
            }
        }

        /// <summary>
        /// Clears all feature registrations.
        /// </summary>
        public static void Clear()
        {
            lock (_syncLock)
            {
                _features.Clear();
            }
        }
    }

    /// <summary>
    /// Lightweight adapter delegating IFeature operations to feature delegates.
    /// </summary>
    public sealed class FeatureAdapter : IFeature
    {
        public string Name { get; }
        public bool IsRunning => _isRunningFunc != null ? _isRunningFunc() : false;

        private readonly Action _start;
        private readonly Action _stop;
        private readonly Action<bool> _purge;
        private readonly Func<int> _count;
        private readonly Func<bool> _isRunningFunc;

        public FeatureAdapter(string name, Action start, Action stop, Action<bool> purge, Func<int> count, Func<bool> isRunning = null)
        {
            Name = name;
            _start = start;
            _stop = stop;
            _purge = purge;
            _count = count;
            _isRunningFunc = isRunning;
        }

        public void Start() => _start?.Invoke();
        public void Stop() => _stop?.Invoke();
        public void PurgeAll(bool immediateDelete) => _purge?.Invoke(immediateDelete);
        public int GetActiveCount() => _count?.Invoke() ?? 0;
    }
}
