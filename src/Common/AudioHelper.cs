using System;
using Rage;
using LSPD_First_Response.Mod.API;
using Cadence.Core;

namespace Cadence.Common
{
    /// <summary>
    /// Centralized scanner audio constants and safe playback helpers.
    /// Wraps all LSPDFR scanner audio invocations in exception guards so missing sound banks,
    /// malformed audio tokens, or LSPDFR audio thread exceptions never throw or block feature execution.
    /// </summary>
    public static class AudioHelper
    {
        // ====================================================================
        // Validated LSPDFR Scanner Audio Constants
        // ====================================================================

        /// <summary>
        /// Standard police assistance required call with positional dispatch token.
        /// </summary>
        public const string PoliceAssistanceRequired = "ASSISTANCE_REQUIRED IN_OR_ON_POSITION";

        /// <summary>
        /// Ambulance medical assistance dispatch with positional token.
        /// </summary>
        public const string AmbulanceAssistanceRequired = "AMBULANCE ASSISTANCE_REQUIRED_01 IN_OR_ON_POSITION";

        /// <summary>
        /// Fire department emergency dispatch with positional token.
        /// </summary>
        public const string FiretruckAssistanceRequired = "FIRETRUCK ASSISTANCE_REQUIRED_01 IN_OR_ON_POSITION";

        /// <summary>
        /// Generic assistance required call without positional trailer.
        /// </summary>
        public const string AssistanceRequired = "ASSISTANCE_REQUIRED";

        /// <summary>
        /// Officer initiating pursuit scanner call.
        /// </summary>
        public const string OfficerInPursuit = "OFFICER_IN_PURSUIT";

        /// <summary>
        /// Code 4 scene cleared broadcast.
        /// </summary>
        public const string Code4 = "CODE_4";

        /// <summary>
        /// Attention all units broadcast chime.
        /// </summary>
        public const string AttentionAllUnits = "ATTENTION_ALL_UNITS";

        /// <summary>
        /// Officer down / panic distress broadcast.
        /// </summary>
        public const string OfficerNeedsAssistance = "OFFICER_NEEDS_ASSISTANCE";

        /// <summary>
        /// Shots fired broadcast.
        /// </summary>
        public const string ShotsFired = "SHOTS_FIRED";

        // ====================================================================
        // Safe Audio Playback Helpers
        // ====================================================================

        /// <summary>
        /// Safely plays a scanner audio line without blocking feature execution or throwing exceptions.
        /// </summary>
        /// <param name="audioString">The audio string token or sequence.</param>
        public static void SafePlayScannerAudio(string audioString)
        {
            if (string.IsNullOrEmpty(audioString)) return;

            try
            {
                Functions.PlayScannerAudio(audioString);
            }
            catch (Exception ex)
            {
                Logger.Debug($"[AudioHelper] SafePlayScannerAudio failed for '{audioString}': {ex.Message}");
            }
        }

        /// <summary>
        /// Safely plays a scanner audio line with the playOverAmbient flag.
        /// </summary>
        /// <param name="audioString">The audio string token or sequence.</param>
        /// <param name="playOverAmbient">Whether to play over existing ambient scanner chatter.</param>
        public static void SafePlayScannerAudio(string audioString, bool playOverAmbient)
        {
            if (string.IsNullOrEmpty(audioString)) return;

            try
            {
                Functions.PlayScannerAudio(audioString, playOverAmbient);
            }
            catch (Exception ex)
            {
                Logger.Debug($"[AudioHelper] SafePlayScannerAudio failed for '{audioString}': {ex.Message}");
            }
        }

        /// <summary>
        /// Safely plays a scanner audio line positioned at the specified world coordinates.
        /// </summary>
        /// <param name="audioString">The audio string token or sequence containing positional tokens.</param>
        /// <param name="position">World position for the scanner dispatch location.</param>
        public static void SafePlayScannerAudioUsingPosition(string audioString, Vector3 position)
        {
            if (string.IsNullOrEmpty(audioString)) return;

            try
            {
                Functions.PlayScannerAudioUsingPosition(audioString, position);
            }
            catch (Exception ex)
            {
                Logger.Debug($"[AudioHelper] SafePlayScannerAudioUsingPosition failed for '{audioString}': {ex.Message}");
            }
        }

        /// <summary>
        /// Convenience method for playing the standard police assistance scanner dispatch at a location.
        /// </summary>
        public static void SafePlayPoliceAssistance(Vector3 position)
        {
            SafePlayScannerAudioUsingPosition(PoliceAssistanceRequired, position);
        }

        /// <summary>
        /// Convenience method for playing the ambulance assistance scanner dispatch at a location.
        /// </summary>
        public static void SafePlayAmbulanceAssistance(Vector3 position)
        {
            SafePlayScannerAudioUsingPosition(AmbulanceAssistanceRequired, position);
        }

        /// <summary>
        /// Convenience method for playing the fire truck assistance scanner dispatch at a location.
        /// </summary>
        public static void SafePlayFiretruckAssistance(Vector3 position)
        {
            SafePlayScannerAudioUsingPosition(FiretruckAssistanceRequired, position);
        }
    }
}
