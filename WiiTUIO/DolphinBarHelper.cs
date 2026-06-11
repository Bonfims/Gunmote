using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace WiiTUIO
{
    /// <summary>
    /// Helper class to detect Mayflash DolphinBar WITHOUT opening any
    /// device handles. Uses direct registry enumeration only.
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

                // Check HID device registry: HKLM\SYSTEM\CurrentControlSet\Enum\HID
                if (FindInRegistry(@"SYSTEM\CurrentControlSet\Enum\HID", "VID_0079", "PID_1803"))
                {
                    Console.WriteLine("[DolphinBarHelper] Found Mayflash in HID registry");
                    _dolphinBarPresent = true;
                    return true;
                }

                // Check USB device registry: HKLM\SYSTEM\CurrentControlSet\Enum\USB
                if (FindInRegistry(@"SYSTEM\CurrentControlSet\Enum\USB", "VID_0079", "PID_1803"))
                {
                    Console.WriteLine("[DolphinBarHelper] Found Mayflash in USB registry");
                    _dolphinBarPresent = true;
                    return true;
                }

                // Check alternate PIDs
                string[] altPids = { "PID_1800", "PID_1801", "PID_1802" };
                foreach (var pid in altPids)
                {
                    if (FindInRegistry(@"SYSTEM\CurrentControlSet\Enum\HID", "VID_0079", pid) ||
                        FindInRegistry(@"SYSTEM\CurrentControlSet\Enum\USB", "VID_0079", pid))
                    {
                        Console.WriteLine("[DolphinBarHelper] Found Mayflash with " + pid);
                        _dolphinBarPresent = true;
                        return true;
                    }
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

        private static bool FindInRegistry(string basePath, string vidPattern, string pidPattern)
        {
            try
            {
                using (var baseKey = Registry.LocalMachine.OpenSubKey(basePath))
                {
                    if (baseKey == null)
                    {
                        Console.WriteLine("[DolphinBarHelper] Registry key not found: " + basePath);
                        return false;
                    }

                    foreach (var subKeyName in baseKey.GetSubKeyNames())
                    {
                        if (subKeyName.IndexOf(vidPattern, StringComparison.OrdinalIgnoreCase) >= 0 &&
                            subKeyName.IndexOf(pidPattern, StringComparison.OrdinalIgnoreCase) >= 0)
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
        /// Get Wii Remote device paths from registry (no handles opened).
        /// </summary>
        public static List<string> GetWiimoteDevicePaths()
        {
            var paths = new List<string>();
            try
            {
                using (var baseKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\HID"))
                {
                    if (baseKey == null) return paths;

                    foreach (var subKeyName in baseKey.GetSubKeyNames())
                    {
                        if (subKeyName.IndexOf("VID_057E", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            (subKeyName.IndexOf("PID_0306", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             subKeyName.IndexOf("PID_0330", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            Console.WriteLine("[DolphinBarHelper] Wii Remote registry entry: " + subKeyName);
                            paths.Add(subKeyName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[DolphinBarHelper] Error finding Wiimotes in registry: " + ex.Message);
            }
            Console.WriteLine("[DolphinBarHelper] Found {0} Wii Remote entries in registry", paths.Count);
            return paths;
        }
    }
}
