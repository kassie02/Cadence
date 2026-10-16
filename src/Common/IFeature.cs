namespace Cadence.Common
{
    /// <summary>
    /// Common lifecycle contract for Cadence ambient policing features.
    /// Provides compile-time checked lifecycle operations, eliminating reflection-based dispatch.
    /// </summary>
    public interface IFeature
    {
        /// <summary>
        /// Display and identifier name of the feature.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Whether the feature fiber and background monitoring are currently running.
        /// </summary>
        bool IsRunning { get; }

        /// <summary>
        /// Starts the feature's processing fibers and monitors.
        /// </summary>
        void Start();

        /// <summary>
        /// Signals the feature to stop processing fibers.
        /// </summary>
        void Stop();

        /// <summary>
        /// Purges and cleans up all active entities, blips, and sessions tracked by this feature.
        /// </summary>
        /// <param name="immediateDelete">If true, deletes entities immediately; otherwise performs clean dismissal.</param>
        void PurgeAll(bool immediateDelete);

        /// <summary>
        /// Returns the count of currently active units/sessions managed by this feature.
        /// </summary>
        int GetActiveCount();
    }
}
