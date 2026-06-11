using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;
using HidLibrary;

namespace WiiTUIO
{
    /// <summary>
    /// Helper class to detect Mayflash DolphinBar and work around
    /// the disconnect issue. Uses registry for bar detection (zero handles)
    /// and HidLibrary for safe device path discovery (ACCESS_NONE only).
    /// </summary>
    public static class DolphinBarHelper
    {
        private static bool? _dolphinBarPresent;

        /// <summary>
        /// Check if a Mayflash DolphinBar is connected via USB.
        /// Reads the PnP device registry — NO handles, NO WMI, NO HID access.
        /// </summary>
        public static bool IsDolphinBarPresent()
        {
            if (_dolphinBarPresent.HasValue)
                return _dolphinBarPresent.Value;

            try
            {
                Console.WriteLine("[DolphinBarHelper] Scanning registry for Mayflash devices (zero handles)...");

                if (FindMayflashInRegistry(@"SYSTEM\CurrentControlSet\Enum\HID") ||
                    FindMayflashInRegistry(@"SYSTEM\CurrentControlSet\Enum\USB"))
                {
                    Console.WriteLine("[DolphinBarHelper] Mayflash DolphinBar detected via registry");
                    _dolphinBarPresent = true;
                    return true;
                }

                Console.WriteLine("[DolphinBarHelper] No Mayflash found in registry");
                _dolphinBarPresent = false;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[DolphinBarHelper] Registry error: " + ex.Message);
                _dolphinBarPresent = false;
            }

            return _dolphinBarPresent.Value;
        }

        private static bool FindMayflashInRegistry(string basePath)
        {
            try
            {
                using (var baseKey = Registry.LocalMachine.OpenSubKey(basePath))
                {
                    if (baseKey == null) return false;

                    foreach (var subKeyName in baseKey.GetSubKeyNames())
                    {
                        if (subKeyName.IndexOf("VID_0079", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            (subKeyName.IndexOf("PID_1800", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             subKeyName.IndexOf("PID_1801", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             subKeyName.IndexOf("PID_1802", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             subKeyName.IndexOf("PID_1803", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            Console.WriteLine("[DolphinBarHelper] Match: " + subKeyName);
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[DolphinBarHelper] Error scanning " + basePath + ": " + ex.Message);
            }
            return false;
        }

        /// <summary>
        /// Reset cached detection (for re-detection after device changes).
        /// </summary>
        public static void ResetCache()
        {
            _dolphinBarPresent = null;
        }

        /// <summary>
        /// Get HID device paths for Nintendo Wii Remote devices.
        /// Uses HidLibrary.Enumerate() which opens handles with ACCESS_NONE
        /// (read-only metadata, no disruption). Returns full HID paths like
        /// \\?\hid#vid_057e&pid_0306&mi_00#...
        /// </summary>
        public static List<string> GetWiimoteDevicePaths()
        {
            var paths = new List<string>();

            try
            {
                Console.WriteLine("[DolphinBarHelper] Enumerating HID devices for Wii Remotes (ACCESS_NONE)...");

                // Enumerate ALL HID devices, filter by VID/PID.
                // This opens with ACCESS_NONE (0), not ReadWrite — safe.
                var devices = HidDevices.Enumerate(0x057E, 0x0306);
                foreach (var device in devices)
                {
                    Console.WriteLine("[DolphinBarHelper] Wii Remote HID path: " + device.DevicePath);
                    paths.Add(device.DevicePath);

                    // Cleanup: dispose the HidDevice (closes the ACCESS_NONE handle)
                    try { device.CloseDevice(); } catch { }
                }

                // Also check Wii Remote Plus PID (0x0330)
                var devicesTR = HidDevices.Enumerate(0x057E, 0x0330);
                foreach (var device in devicesTR)
                {
                    Console.WriteLine("[DolphinBarHelper] Wii Remote Plus HID path: " + device.DevicePath);
                    if (!paths.Contains(device.DevicePath))
                    {
                        paths.Add(device.DevicePath);
                        try { device.CloseDevice(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[DolphinBarHelper] Error enumerating Wiimotes: " + ex.Message);
            }

            Console.WriteLine("[DolphinBarHelper] Found {0} Wii Remote HID paths", paths.Count);
            return paths;
        }
    }
}
