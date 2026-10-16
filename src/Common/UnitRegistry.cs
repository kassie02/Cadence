using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Rage;

namespace Cadence.Common
{
    /// <summary>
    /// Thread-safe central registry tracking entity ownership across features
    /// (Backup, Pursuit, Patrol, Assist, TrafficStop, Stationary, Response).
    /// Prevents cross-feature dual ownership and race conditions when commandeering or drafting units.
    /// Supports atomic claims, release, querying, and verification under high concurrency.
    /// </summary>
    public static class UnitRegistry
    {
        private static readonly object _syncLock = new object();
        private static readonly Dictionary<uint, string> _claims = new Dictionary<uint, string>();

        // Canonical owner constants
        public const string OwnerBackup = "Backup";
        public const string OwnerPursuit = "Pursuit";
        public const string OwnerPatrol = "Patrol";
        public const string OwnerAssist = "Assist";
        public const string OwnerTrafficStop = "TrafficStop";
        public const string OwnerStationary = "Stationary";
        public const string OwnerResponse = "Response";

        // ====================================================================
        // Core UInt Handle Operations (Atomic & Standalone Testable)
        // ====================================================================

        /// <summary>
        /// Attempts to claim ownership of an entity handle on behalf of the specified owner.
        /// Returns true if the handle was successfully claimed, or if it is already claimed by the same owner (idempotent).
        /// Returns false if the handle is invalid (0) or is already claimed by another owner.
        /// </summary>
        public static bool TryClaim(uint handle, string owner)
        {
            if (handle == 0 || string.IsNullOrEmpty(owner))
            {
                return false;
            }

            lock (_syncLock)
            {
                if (_claims.TryGetValue(handle, out string existingOwner))
                {
                    // Already claimed by this owner is treated as success (re-entrant / idempotent)
                    return string.Equals(existingOwner, owner, StringComparison.OrdinalIgnoreCase);
                }

                _claims[handle] = owner;
                return true;
            }
        }

        /// <summary>
        /// Releases ownership of an entity handle if it is currently claimed by the specified owner.
        /// </summary>
        public static bool Release(uint handle, string owner)
        {
            if (handle == 0 || string.IsNullOrEmpty(owner))
            {
                return false;
            }

            lock (_syncLock)
            {
                if (_claims.TryGetValue(handle, out string existingOwner) &&
                    string.Equals(existingOwner, owner, StringComparison.OrdinalIgnoreCase))
                {
                    return _claims.Remove(handle);
                }

                return false;
            }
        }

        /// <summary>
        /// Releases ownership of an entity handle unconditionally, regardless of current owner.
        /// </summary>
        public static bool Release(uint handle)
        {
            if (handle == 0) return false;

            lock (_syncLock)
            {
                return _claims.Remove(handle);
            }
        }

        /// <summary>
        /// Checks whether the entity handle is claimed by any owner.
        /// </summary>
        public static bool IsClaimed(uint handle)
        {
            if (handle == 0) return false;

            lock (_syncLock)
            {
                return _claims.ContainsKey(handle);
            }
        }

        /// <summary>
        /// Checks whether the entity handle is claimed by the specified owner.
        /// </summary>
        public static bool IsClaimedBy(uint handle, string owner)
        {
            if (handle == 0 || string.IsNullOrEmpty(owner)) return false;

            lock (_syncLock)
            {
                if (_claims.TryGetValue(handle, out string existingOwner))
                {
                    return string.Equals(existingOwner, owner, StringComparison.OrdinalIgnoreCase);
                }

                return false;
            }
        }

        /// <summary>
        /// Checks whether the entity handle is claimed by an owner other than the specified owner.
        /// </summary>
        public static bool IsClaimedByOthers(uint handle, string owner)
        {
            if (handle == 0) return false;

            lock (_syncLock)
            {
                if (_claims.TryGetValue(handle, out string existingOwner))
                {
                    return !string.Equals(existingOwner, owner, StringComparison.OrdinalIgnoreCase);
                }

                return false;
            }
        }

        /// <summary>
        /// Returns the name of the owner currently claiming the entity handle, or null if unclaimed.
        /// </summary>
        public static string GetOwner(uint handle)
        {
            if (handle == 0) return null;

            lock (_syncLock)
            {
                if (_claims.TryGetValue(handle, out string owner))
                {
                    return owner;
                }

                return null;
            }
        }

        // ====================================================================
        // PoolHandle Overloads
        // ====================================================================

        public static bool TryClaim(PoolHandle handle, string owner) => TryClaim(handle.Value, owner);
        public static bool Release(PoolHandle handle, string owner) => Release(handle.Value, owner);
        public static bool Release(PoolHandle handle) => Release(handle.Value);
        public static bool IsClaimed(PoolHandle handle) => IsClaimed(handle.Value);
        public static bool IsClaimedBy(PoolHandle handle, string owner) => IsClaimedBy(handle.Value, owner);
        public static bool IsClaimedByOthers(PoolHandle handle, string owner) => IsClaimedByOthers(handle.Value, owner);
        public static string GetOwner(PoolHandle handle) => GetOwner(handle.Value);

        // ====================================================================
        // Entity Overloads
        // ====================================================================

        /// <summary>
        /// Attempts to claim ownership of an entity on behalf of the specified owner.
        /// Returns true if the entity was successfully claimed, or if it is already claimed by the same owner (idempotent).
        /// Returns false if the entity is invalid, does not exist, or is already claimed by another owner.
        /// </summary>
        public static bool TryClaim(Entity entity, string owner)
        {
            if (!TryGetHandle(entity, out uint handle) || string.IsNullOrEmpty(owner))
            {
                return false;
            }

            try
            {
                if (!entity.Exists())
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }

            return TryClaim(handle, owner);
        }

        /// <summary>
        /// Releases ownership of an entity if it is currently claimed by the specified owner.
        /// </summary>
        public static bool Release(Entity entity, string owner)
        {
            if (!TryGetHandle(entity, out uint handle)) return false;
            return Release(handle, owner);
        }

        /// <summary>
        /// Releases ownership of an entity unconditionally, regardless of current owner.
        /// </summary>
        public static bool Release(Entity entity)
        {
            if (!TryGetHandle(entity, out uint handle)) return false;
            return Release(handle);
        }

        /// <summary>
        /// Checks whether the entity is claimed by any owner.
        /// </summary>
        public static bool IsClaimed(Entity entity)
        {
            if (!TryGetHandle(entity, out uint handle)) return false;
            return IsClaimed(handle);
        }

        /// <summary>
        /// Checks whether the entity is claimed by the specified owner.
        /// </summary>
        public static bool IsClaimedBy(Entity entity, string owner)
        {
            if (!TryGetHandle(entity, out uint handle)) return false;
            return IsClaimedBy(handle, owner);
        }

        /// <summary>
        /// Checks whether the entity is claimed by an owner other than the specified owner.
        /// </summary>
        public static bool IsClaimedByOthers(Entity entity, string owner)
        {
            if (!TryGetHandle(entity, out uint handle)) return false;
            return IsClaimedByOthers(handle, owner);
        }

        /// <summary>
        /// Returns the name of the owner currently claiming the entity, or null if unclaimed.
        /// </summary>
        public static string GetOwner(Entity entity)
        {
            if (!TryGetHandle(entity, out uint handle)) return null;
            return GetOwner(handle);
        }

        // ====================================================================
        // Bulk & Maintenance Operations
        // ====================================================================

        /// <summary>
        /// Releases all entities claimed by the specified owner.
        /// </summary>
        public static void ReleaseAll(string owner)
        {
            if (string.IsNullOrEmpty(owner)) return;

            lock (_syncLock)
            {
                var keysToRemove = new List<uint>();
                foreach (var kvp in _claims)
                {
                    if (string.Equals(kvp.Value, owner, StringComparison.OrdinalIgnoreCase))
                    {
                        keysToRemove.Add(kvp.Key);
                    }
                }

                for (int i = 0; i < keysToRemove.Count; i++)
                {
                    _claims.Remove(keysToRemove[i]);
                }
            }
        }

        /// <summary>
        /// Returns the total number of currently claimed entities.
        /// </summary>
        public static int Count
        {
            get
            {
                lock (_syncLock)
                {
                    return _claims.Count;
                }
            }
        }

        /// <summary>
        /// Clears all entity claims in the registry.
        /// </summary>
        public static void Clear()
        {
            lock (_syncLock)
            {
                _claims.Clear();
            }
        }

        /// <summary>
        /// Safely extracts the uint handle of an entity, guarding against nulls or disposed wrappers.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryGetHandle(Entity entity, out uint handle)
        {
            handle = 0;
            if (entity == null) return false;

            try
            {
                handle = entity.Handle.Value;
                return handle != 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
