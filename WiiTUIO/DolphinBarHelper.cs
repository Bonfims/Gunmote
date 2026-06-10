using System;
using System.Collections.Generic;
using System.Linq;
using HidLibrary;

namespace WiiTUIO
{
    /// <summary>
    /// Helper class to detect Mayflash DolphinBar and work around
    /// the CSR Bluetooth driver issue where WiiPair's Bluetooth radio
    /// scanning disrupts the bar's internal Wii Remote connection.
    /// </summary>
    public static class DolphinBarHelper
    {
        // Mayflash DolphinBar VID/PID
        private const int DOLPHINBAR_VID = 0x0079;
        private const int DOLPHINBAR_PID = 0x1803;  // also 0x1800, 0x1801 on some versions

        // Nintendo Wii Remote VID/PIDs (for direct HID fallback)
        private const int NINTENDO_VID = 0x057E;
        private static readonly int[] WIIMOTE_PIDS = { 0x0306, 0x0330 };

        private static bool? _dolphinBarPresent;

        /// <summary>
        /// Check if a Mayflash DolphinBar is connected via USB.
        /// The DolphinBar must be detected via its USB HID interface.
        /// </summary>
        public static bool IsDolphinBarPresent()
        {
            if (_dolphinBarPresent.HasValue)
                return _dolphinBarPresent.Value;

            try
            {
                var devices = HidDevices.Enumerate(DOLPHINBAR_VID, DOLPHINBAR_PID);
                _dolphinBarPresent = devices.Any();

                // Also check alternate PIDs
                if (!_dolphinBarPresent.Value)
                {
                    devices = HidDevices.Enumerate(DOLPHINBAR_VID, 0x1800);
                    _dolphinBarPresent = devices.Any();
                }
                if (!_dolphinBarPresent.Value)
                {
                    devices = HidDevices.Enumerate(DOLPHINBAR_VID, 0x1801);
                    _dolphinBarPresent = devices.Any();
                }

                Console.WriteLine(_dolphinBarPresent.Value
                    ? "DolphinBarHelper: Mayflash DolphinBar detected via USB HID"
                    : "DolphinBarHelper: No Mayflash DolphinBar detected");
            }
            catch (Exception ex)
            {
                Console.WriteLine("DolphinBarHelper: Error detecting DolphinBar: " + ex.Message);
                _dolphinBarPresent = false;
            }

            return _dolphinBarPresent.Value;
        }

        /// <summary>
        /// Find all Wii Remote HID devices directly via HID enumeration.
        /// This bypasses the Bluetooth stack entirely and works when
        /// the DolphinBar is in Mode 4 but the CSR Bluetooth driver
        /// isn't loaded in Windows.
        ///
        /// The DolphinBar exposes each Wii Remote slot as a separate
        /// HID device with Nintendo VID/PID, even in Mode 4.
        /// </summary>
        public static List<HidDevice> FindWiiRemotesViaHID()
        {
            var wiimotes = new List<HidDevice>();

            try
            {
                foreach (int pid in WIIMOTE_PIDS)
                {
                    var devices = HidDevices.Enumerate(NINTENDO_VID, pid);
                    foreach (var device in devices)
                    {
                        Console.WriteLine("DolphinBarHelper: Found Wii Remote HID: " + device.DevicePath);
                        wiimotes.Add(device);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("DolphinBarHelper: Error enumerating Wii Remotes: " + ex.Message);
            }

            Console.WriteLine("DolphinBarHelper: Found {0} Wii Remote HID devices", wiimotes.Count);
            return wiimotes;
        }

        /// <summary>
        /// Get HID device paths for all detected Wii Remotes.
        /// These can be passed to WiimoteLib if it supports path-based connection.
        /// </summary>
        public static List<string> GetWiimoteDevicePaths()
        {
            return FindWiiRemotesViaHID().Select(d => d.DevicePath).ToList();
        }
    }
}
