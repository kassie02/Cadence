using System;
using System.Windows.Forms;
using Rage;
using Rage.Native;

namespace Cadence.Common
{
    /// <summary>
    /// Centralized, high-reliability input handling and modifier normalization utility for Cadence.
    /// Eliminates Win32 virtual key discrepancies, bidirectional chord timing issues, and native control conflicts.
    /// </summary>
    public static class InputHelper
    {
        /// <summary>
        /// Native GTA V control index for taking cover (INPUT_COVER).
        /// Default keyboard binding is 'Q'.
        /// </summary>
        public const int INPUT_COVER = 44;

        /// <summary>
        /// Checks whether the specified modifier key is currently depressed.
        /// Reliably handles Control, Shift, and Alt modifier variations across RPH and Win32 virtual key tables.
        /// </summary>
        public static bool IsModifierDown(Keys modifier)
        {
            if (modifier == Keys.None) return true;

            // Control modifier normalization (VK_CONTROL vs VK_LCONTROL / VK_RCONTROL)
            if (modifier == Keys.Control || modifier == Keys.LControlKey || modifier == Keys.RControlKey || modifier == Keys.ControlKey)
            {
                return Game.IsControlKeyDownRightNow ||
                       Game.IsKeyDownRightNow(Keys.ControlKey) ||
                       Game.IsKeyDownRightNow(Keys.LControlKey) ||
                       Game.IsKeyDownRightNow(Keys.RControlKey);
            }

            // Shift modifier normalization
            if (modifier == Keys.Shift || modifier == Keys.LShiftKey || modifier == Keys.RShiftKey || modifier == Keys.ShiftKey)
            {
                return Game.IsShiftKeyDownRightNow ||
                       Game.IsKeyDownRightNow(Keys.ShiftKey) ||
                       Game.IsKeyDownRightNow(Keys.LShiftKey) ||
                       Game.IsKeyDownRightNow(Keys.RShiftKey);
            }

            // Alt / Menu modifier normalization
            if (modifier == Keys.Alt || modifier == Keys.Menu || modifier == Keys.LMenu || modifier == Keys.RMenu)
            {
                return Game.IsAltKeyDownRightNow ||
                       Game.IsKeyDownRightNow(Keys.Menu) ||
                       Game.IsKeyDownRightNow(Keys.LMenu) ||
                       Game.IsKeyDownRightNow(Keys.RMenu);
            }

            return Game.IsKeyDownRightNow(modifier);
        }

        /// <summary>
        /// Checks whether the specified modifier key was just pressed down this frame (edge transition).
        /// </summary>
        public static bool IsModifierJustPressed(Keys modifier)
        {
            if (modifier == Keys.None) return false;

            if (modifier == Keys.Control || modifier == Keys.LControlKey || modifier == Keys.RControlKey || modifier == Keys.ControlKey)
            {
                return Game.IsKeyDown(Keys.ControlKey) ||
                       Game.IsKeyDown(Keys.LControlKey) ||
                       Game.IsKeyDown(Keys.RControlKey);
            }

            if (modifier == Keys.Shift || modifier == Keys.LShiftKey || modifier == Keys.RShiftKey || modifier == Keys.ShiftKey)
            {
                return Game.IsKeyDown(Keys.ShiftKey) ||
                       Game.IsKeyDown(Keys.LShiftKey) ||
                       Game.IsKeyDown(Keys.RShiftKey);
            }

            if (modifier == Keys.Alt || modifier == Keys.Menu || modifier == Keys.LMenu || modifier == Keys.RMenu)
            {
                return Game.IsKeyDown(Keys.Menu) ||
                       Game.IsKeyDown(Keys.LMenu) ||
                       Game.IsKeyDown(Keys.RMenu);
            }

            return Game.IsKeyDown(modifier);
        }

        /// <summary>
        /// Bidirectional chord detection: triggers when Key and Modifier are both depressed,
        /// regardless of which key touched down first, with edge-triggered single activation.
        /// </summary>
        public static bool IsKeyComboPressed(Keys key, Keys modifier)
        {
            if (key == Keys.None) return false;

            if (modifier == Keys.None)
            {
                return Game.IsKeyDown(key);
            }

            // Case A: Modifier already held down, Key just pressed
            if (IsModifierDown(modifier) && Game.IsKeyDown(key))
            {
                return true;
            }

            // Case B: Key already held down, Modifier just pressed (prevents order-of-press drops)
            if (Game.IsKeyDownRightNow(key) && IsModifierJustPressed(modifier))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Disables a native GTA V control action for this frame.
        /// </summary>
        public static void SuppressControl(int controlIndex, int controlGroup = 0)
        {
            try
            {
                NativeFunction.Natives.DISABLE_CONTROL_ACTION(controlGroup, controlIndex, true);
            }
            catch { }
        }

        /// <summary>
        /// Suppresses GTA V native cover action (INPUT_COVER, control index 44).
        /// Used when Q is held as a menu modifier or keybind to prevent unwanted snapping to walls.
        /// </summary>
        public static void SuppressCover()
        {
            SuppressControl(INPUT_COVER);
        }

        /// <summary>
        /// Suppresses INPUT_COVER if the specified key or modifier is Q and is currently held down.
        /// </summary>
        public static void SuppressCoverIfQHeld(Keys key, Keys modifier)
        {
            if ((key == Keys.Q || modifier == Keys.Q) && Game.IsKeyDownRightNow(Keys.Q))
            {
                SuppressCover();
            }
        }
    }
}
