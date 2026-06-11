using System;
using System.Management;

namespace WiiTUIO
{
    /// <summary>
    /// Helper class to detect Mayflash DolphinBar and work around
    /// the CSR Bluetooth driver issue. Uses WMI queries (read-only,
    /// no device handles opened) to detect the bar without disrupting
    /// existing Wii Remote connections.
    /// </summary>
    public static class DolphinBarHelper
    {
        // Mayflash DolphinBar VID/PID
        private const int DOLPHINBAR_VID = 0x0079;
        private const int DOLPHINBAR_PID = 0x1803;

        private static bool? _dolphinBarPresent;

        /// <summary>
        /// Check if a Mayflash DolphinBar is connected via USB.
        /// Uses WMI to query the device tree WITHOUT opening any HID handles.
        /// </summary>
        public static bool IsDolphinBarPresent()
        {
            if (_dolphinBarPresent.HasValue)
                return _dolphinBarPresent.Value;

            try
            {
                Console.WriteLine("[DolphinBarHelper] Querying WMI for Mayflash devices (no handles opened)...");

                // Query PnP entities for Mayflash VID/PID. This reads from the registry
                // and does NOT open any device handles.
                string vidStr = DOLPHINBAR_VID.ToString("X4");
                string pidStr = DOLPHINBAR_PID.ToString("X4");

                // Check primary PID
                using (var searcher = new ManagementObjectSearcher(
                    @"SELECT Name, DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE '%VID_" + vidStr + "%' AND DeviceID LIKE '%PID_" + pidStr + "%'"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        Console.WriteLine("[DolphinBarHelper] Found: " + (obj["Name"] ?? "unknown"));
                        _dolphinBarPresent = true;
                        return true;
                    }
                }

                // Check alternate PIDs (0x1800, 0x1801, 0x1802)
                using (var searcher = new ManagementObjectSearcher(
                    @"SELECT Name, DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE '%VID_" + vidStr + "%' AND (DeviceID LIKE '%PID_1800%' OR DeviceID LIKE '%PID_1801%' OR DeviceID LIKE '%PID_1802%')"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        Console.WriteLine("[DolphinBarHelper] Found (alt PID): " + (obj["Name"] ?? "unknown"));
                        _dolphinBarPresent = true;
                        return true;
                    }
                }

                Console.WriteLine("[DolphinBarHelper] No Mayflash DolphinBar detected via WMI");
                _dolphinBarPresent = false;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[DolphinBarHelper] WMI error: " + ex.Message);
                _dolphinBarPresent = false;
            }

            return _dolphinBarPresent.Value;
        }

        /// <summary>
        /// Reset cached detection (for re-detection after device changes).
        /// </summary>
        public static void ResetCache()
        {
            _dolphinBarPresent = null;
        }

        /// <summary>
        /// Get HID device paths for all Nintendo Wii Remote devices
        /// using WMI (no handles opened). Returns device instance IDs
        /// that can be converted to HID paths.
        /// </summary>
        public static List<string> GetWiimoteDevicePaths()
        {
            var paths = new List<string>();

            try
            {
                // Query for Nintendo HID devices via WMI
                using (var searcher = new ManagementObjectSearcher(
                    @"SELECT Name, DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE '%VID_057E%' AND (DeviceID LIKE '%PID_0306%' OR DeviceID LIKE '%PID_0330%')"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        string deviceId = obj["DeviceID"]?.ToString() ?? "";
                        Console.WriteLine("[DolphinBarHelper] Wii Remote found via WMI: " + (obj["Name"] ?? "unknown") + " ID=" + deviceId);
                        paths.Add(deviceId);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[DolphinBarHelper] WMI Wiimote enum error: " + ex.Message);
            }

            Console.WriteLine("[DolphinBarHelper] Found {0} Wii Remote device(s) via WMI", paths.Count);
            return paths;
        }
    }
}
