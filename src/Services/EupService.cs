using System;
using Rage;
using Rage.Native;
using Cadence.Core;

namespace Cadence.Services
{
    public static class EupService
    {
        /// <summary>
        /// Applies custom EUP clothing components and props strings to an officer ped.
        /// Expected component format: "compId:drawableId:textureId, compId:drawableId:textureId"
        /// Expected prop format: "propId:drawableId:textureId, propId:drawableId:textureId"
        /// </summary>
        public static void Apply(Ped ped, string componentString, string propString)
        {
            if (!Settings.EnableEupOutfits || ped == null || !ped.Exists()) return;

            try
            {
                if (!string.IsNullOrWhiteSpace(componentString))
                {
                    ApplyComponents(ped, componentString);
                }

                if (!string.IsNullOrWhiteSpace(propString))
                {
                    ApplyProps(ped, propString);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("EupService.Apply error", ex);
            }
        }

        private static void ApplyComponents(Ped ped, string componentString)
        {
            if (ped == null || !ped.Exists()) return;
            string[] items = componentString.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string rawItem in items)
            {
                string item = rawItem.Trim();
                if (string.IsNullOrWhiteSpace(item)) continue;

                string[] parts = item.Split(new[] { ':', '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    if (int.TryParse(parts[0].Trim(), out int compId) && int.TryParse(parts[1].Trim(), out int drawableId))
                    {
                        if (compId < 0 || drawableId < 0) continue;
                        int textureId = 0;
                        if (parts.Length >= 3)
                        {
                            if (!int.TryParse(parts[2].Trim(), out textureId) || textureId < 0)
                            {
                                textureId = 0;
                            }
                        }

                        try
                        {
                            NativeFunction.Natives.SET_PED_COMPONENT_VARIATION(ped, compId, drawableId, textureId, 0);
                        }
                        catch (Exception ex)
                        {
                            Logger.Debug($"EupService component error ({compId}:{drawableId}:{textureId}): {ex.Message}");
                        }
                    }
                }
            }
        }

        private static void ApplyProps(Ped ped, string propString)
        {
            if (ped == null || !ped.Exists()) return;
            string[] items = propString.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string rawItem in items)
            {
                string item = rawItem.Trim();
                if (string.IsNullOrWhiteSpace(item)) continue;

                string[] parts = item.Split(new[] { ':', '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    if (int.TryParse(parts[0].Trim(), out int propId) && int.TryParse(parts[1].Trim(), out int drawableId))
                    {
                        if (propId < 0) continue;
                        int textureId = 0;
                        if (parts.Length >= 3)
                        {
                            if (!int.TryParse(parts[2].Trim(), out textureId) || textureId < 0)
                            {
                                textureId = 0;
                            }
                        }

                        try
                        {
                            if (drawableId < 0)
                            {
                                NativeFunction.Natives.CLEAR_PED_PROP(ped, propId, 0);
                            }
                            else
                            {
                                NativeFunction.Natives.SET_PED_PROP_INDEX(ped, propId, drawableId, textureId, true, 0);
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Debug($"EupService prop error ({propId}:{drawableId}:{textureId}): {ex.Message}");
                        }
                    }
                }
            }
        }

        // ====================================================================
        // Native GTA Uniform Fallback Presets
        // ====================================================================

        /// <summary>
        /// Applies body armor vest variation (Component 9) to an officer ped.
        /// Gated on Settings.EnableEupOutfits [E26].
        /// </summary>
        public static void ApplyBodyArmor(Ped ped, int armorDrawable = 1)
        {
            if (!Settings.EnableEupOutfits || ped == null || !ped.Exists()) return;
            if (armorDrawable < 0) return;
            try
            {
                NativeFunction.Natives.SET_PED_COMPONENT_VARIATION(ped, 9, armorDrawable, 0, 0);
            }
            catch { }
        }

        /// <summary>
        /// Equips a high-visibility traffic vest or safety gear if present on model.
        /// Gated on Settings.EnableEupOutfits [E26].
        /// </summary>
        public static void ApplyTrafficVest(Ped ped)
        {
            if (!Settings.EnableEupOutfits || ped == null || !ped.Exists()) return;
            try
            {
                // Component 8 / 9 traffic vest textures on standard cop models
                NativeFunction.Natives.SET_PED_COMPONENT_VARIATION(ped, 8, 1, 0, 0);
            }
            catch { }
        }
    }
}
