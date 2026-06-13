using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using WiimoteLib;

namespace WiiTUIO.Provider
{
    /// <summary>
    /// Direct port of Wii Mote Hooks' GClass12 Wiimote communication logic.
    /// Uses direct kernel32 WriteFile with short HID reports (matching
    /// Wii Mote Hooks "New" method) for parallel/3rd-party Wii Remote support.
    /// </summary>
    public class WiiMoteHookConnection : IDisposable
    {
        // HID P/Invoke (subset of what we need)
        [DllImport("hid.dll", SetLastError = true)]
        private static extern void HidD_GetHidGuid(out Guid guid);

        [DllImport("hid.dll")]
        private static extern bool HidD_GetAttributes(IntPtr handle, ref HIDAttributes attrib);

        [DllImport("hid.dll")]
        private static extern bool HidD_SetOutputReport(IntPtr handle, byte[] buffer, uint length);

        [DllImport("hid.dll")]
        private static extern bool HidD_GetSerialNumberString(IntPtr handle, byte[] buffer, uint length);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string enumerator, IntPtr parent, uint flags);

        [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid guid, int index, ref SP_DEVICE_INTERFACE_DATA data);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint size, out uint requiredSize, IntPtr deviceInfo);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetail", SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail2(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA data, ref SP_DEVICE_INTERFACE_DETAIL_DATA detail, uint size, out uint requiredSize, IntPtr deviceInfo);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, FileAccess access, FileShare share, IntPtr security, FileMode mode, EFileAttributes flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(IntPtr handle, byte[] buffer, uint count, out uint written, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern bool DeviceIoControl(IntPtr handle, int code, byte[] inBuf, int inLen, byte[] outBuf, int outLen, out int returned, IntPtr overlapped);

        private const int IOCTL_HID_SET_OUTPUT_REPORT = 0xB0195;
        private const int DIGCF_DEVICEINTERFACE = 0x10;
        private const int DIGCF_PRESENT = 0x02;
        private const int REPORT_LENGTH = 22;
        private const int VID_NINTENDO = 0x057E;
        private const int PID_WIIMOTE = 0x0306;
        private const int PID_WIIMOTE_PLUS = 0x0330;

        [Flags]
        private enum EFileAttributes : uint { Overlapped = 0x40000000 }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct SP_DEVICE_INTERFACE_DETAIL_DATA
        {
            public uint cbSize;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string DevicePath;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDAttributes
        {
            public int Size;
            public short VendorID;
            public short ProductID;
            public short VersionNumber;
        }

        private enum SendMethod { WriteFile, SetOutputReport, WriteStream }
        private enum WriteSize { Short, Full }

        // Connection state
        private SafeFileHandle mHandle;
        private FileStream mStream;
        private SendMethod mSendMethod = SendMethod.WriteFile;
        private bool mShortWrites = true; // Like Wii Mote Hooks Boolean_1=false for Win8+
        private byte[] mReadBuffer = new byte[REPORT_LENGTH];
        private byte[] mResponseBuffer;
        private int mExpectedAddress;
        private short mExpectedSize;
        private readonly AutoResetEvent mReadEvent = new AutoResetEvent(false);
        private readonly AutoResetEvent mWriteEvent = new AutoResetEvent(false);
        private readonly AutoResetEvent mStatusEvent = new AutoResetEvent(false);
        private bool mStatusRequested;
        private bool mIsTR;
        private bool mFirstBoot = true;

        // The state we fill
        private readonly WiimoteState mState = new WiimoteState();

        public string DevicePath { get; private set; }
        public string SerialNumber { get; private set; }
        public bool IsConnected { get; private set; }
        public WiimoteState State => mState;

        public event Action<WiiMoteHookConnection> DataReceived;

        public WiiMoteHookConnection(string devicePath = null)
        {
            DevicePath = devicePath;
        }

        /// <summary>
        /// Scan for Wii Remotes via HID and invoke callback for each found device path.
        /// Port of GClass12.smethod_1 (HID enumeration).
        /// </summary>
        public static void FindWiimotes(Func<string, bool> callback)
        {
            Guid hidGuid;
            HidD_GetHidGuid(out hidGuid);
            IntPtr deviceInfoSet = SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, DIGCF_DEVICEINTERFACE | DIGCF_PRESENT);
            if (deviceInfoSet == IntPtr.Zero || deviceInfoSet.ToInt64() == -1) return;

            var diData = new SP_DEVICE_INTERFACE_DATA();
            diData.cbSize = Marshal.SizeOf(diData);
            int index = 0;
            bool found = false;

            while (SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref hidGuid, index, ref diData))
            {
                uint requiredSize;
                SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref diData, IntPtr.Zero, 0, out requiredSize, IntPtr.Zero);

                var detail = new SP_DEVICE_INTERFACE_DETAIL_DATA();
                detail.cbSize = (uint)(IntPtr.Size == 8 ? 8 : 5);

                if (SetupDiGetDeviceInterfaceDetail2(deviceInfoSet, ref diData, ref detail, requiredSize, out requiredSize, IntPtr.Zero))
                {
                    // Open briefly to check VID/PID (ACCESS_NONE for minimal disruption)
                    using (var h = CreateFile(detail.DevicePath, 0, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open, EFileAttributes.Overlapped, IntPtr.Zero))
                    {
                        if (!h.IsInvalid)
                        {
                            var attr = new HIDAttributes { Size = Marshal.SizeOf(typeof(HIDAttributes)) };
                            if (HidD_GetAttributes(h.DangerousGetHandle(), ref attr))
                            {
                                if (attr.VendorID == VID_NINTENDO && (attr.ProductID == PID_WIIMOTE || attr.ProductID == PID_WIIMOTE_PLUS))
                                {
                                    found = true;
                                    if (!callback(detail.DevicePath))
                                        break;
                                }
                            }
                        }
                    }
                }
                index++;
            }
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
            if (!found)
                throw new Exception("No Wii Remotes found in HID device list.");
        }

        /// <summary>
        /// Connect to the Wii Remote at DevicePath.
        /// Port of GClass12.method_4.
        /// </summary>
        public void Connect()
        {
            if (string.IsNullOrEmpty(DevicePath))
                throw new InvalidOperationException("DevicePath not set");

            // Open handle with FileShare.ReadWrite (matches Wii Mote Hooks Boolean_2=false for Win8+)
            mHandle = CreateFile(DevicePath, FileAccess.ReadWrite, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open, EFileAttributes.Overlapped, IntPtr.Zero);

            // Get serial
            byte[] serialBuf = new byte[126];
            HidD_GetSerialNumberString(mHandle.DangerousGetHandle(), serialBuf, (uint)serialBuf.Length);
            SerialNumber = System.Text.Encoding.Unicode.GetString(serialBuf).Replace("\0", "").ToUpper();

            // Verify VID/PID
            var attr = new HIDAttributes { Size = Marshal.SizeOf(typeof(HIDAttributes)) };
            if (!HidD_GetAttributes(mHandle.DangerousGetHandle(), ref attr))
            {
                Console.WriteLine("[WiiMoteHook] Error getting HID attributes");
                return;
            }
            if (attr.VendorID != VID_NINTENDO || (attr.ProductID != PID_WIIMOTE && attr.ProductID != PID_WIIMOTE_PLUS))
            {
                mHandle.Close();
                throw new Exception("Not a Wii Remote device");
            }

            mIsTR = (attr.ProductID == PID_WIIMOTE_PLUS);
            Console.WriteLine("[WiiMoteHook] Device ID: 0x{0:X4} ({1})", attr.ProductID, mIsTR ? "RVL-CNT-01-TR" : "RVL-CNT-01");

            // Create FileStream and start async read
            mStream = new FileStream(mHandle, FileAccess.ReadWrite, REPORT_LENGTH, true);
            BeginAsyncRead();

            // Read calibration with retry logic
            if (!ReadCalibrationWithRetry())
            {
                Console.WriteLine("[WiiMoteHook] Still failing to read from this remote; disconnecting");
                Disconnect();
                return;
            }

            // Read full calibration
            ReadFullCalibration();

            // Get status
            RequestStatus();

            // Set report type for IR+Accel
            SetReportType();

            IsConnected = true;
        }

        private bool ReadCalibrationWithRetry()
        {
            // Try up to 3 send methods (matching Wii Mote Hooks method_3)
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    ReadData(0x16, 7); // Calibration register
                    return true;
                }
                catch
                {
                    Console.WriteLine("[WiiMoteHook] Send-data method {0} failed. Trying another.", mSendMethod);
                    CycleMethod();
                }
            }
            return false;
        }

        private void CycleMethod()
        {
            // Match Wii Mote Hooks method_3 cycling logic exactly
            switch (mSendMethod)
            {
                case SendMethod.WriteFile:
                    mSendMethod = SendMethod.SetOutputReport;
                    break;
                case SendMethod.SetOutputReport:
                    mSendMethod = SendMethod.WriteStream;
                    break;
                case SendMethod.WriteStream:
                    mSendMethod = SendMethod.WriteFile;
                    break;
            }
        }

        private void SetReportType()
        {
            // Set to IRExtensionAccel for maximum data (IR + buttons + accel + extension)
            byte[] buf = mShortWrites ? new byte[3] : new byte[22];
            buf[0] = 0x12; // Type
            buf[1] = 0x04; // Continuous
            buf[2] = 0x37; // IRExtensionAccel report type
            WriteToDevice(buf);
        }

        private void ReadFullCalibration()
        {
            byte[] data = ReadData(0x16, 32);
            // Parse calibration (simplified - just store raw for now)
            mState.AccelCalibrationInfo = new AccelCalibrationInfo();
        }

        private void RequestStatus()
        {
            Console.WriteLine("[WiiMoteHook] Requesting status manually");
            byte[] buf = mShortWrites ? new byte[2] : new byte[22];
            buf[0] = 0x15; // Status
            buf[1] = 0; // No rumble
            mStatusRequested = true;
            WriteToDevice(buf);

            if (!mStatusEvent.WaitOne(6000))
                throw new Exception("Timed out waiting for status report");
        }

        private void SetLEDs(bool led1, bool led2, bool led3, bool led4)
        {
            byte[] buf = mShortWrites ? new byte[2] : new byte[22];
            buf[0] = 0x11; // LEDs
            buf[1] = (byte)((led1 ? 0x10 : 0) | (led2 ? 0x20 : 0) | (led3 ? 0x40 : 0) | (led4 ? 0x80 : 0));
            WriteToDevice(buf);
        }

        public byte[] ReadData(int address, short size)
        {
            mResponseBuffer = new byte[size];
            mExpectedAddress = address & 0xFFFF;
            mExpectedSize = size;

            byte[] buf = mShortWrites ? new byte[7] : new byte[22];
            buf[0] = 0x17; // ReadMemory
            buf[1] = (byte)(((address & 0xFF000000) >> 24) | 0); // Rumble bit off
            buf[2] = (byte)((address & 0xFF0000) >> 16);
            buf[3] = (byte)((address & 0xFF00) >> 8);
            buf[4] = (byte)(address & 0xFF);
            buf[5] = (byte)((size & 0xFF00) >> 8);
            buf[6] = (byte)(size & 0xFF);
            WriteToDevice(buf);

            if (!mReadEvent.WaitOne(3000))
                throw new Exception("Error reading data from the Wiimote...is it connected?");

            return mResponseBuffer;
        }

        private void WriteToDevice(byte[] data)
        {
            bool written = false;
            for (int method = 0; method < 3 && !written; method++)
            {
                try
                {
                    switch ((SendMethod)(((int)mSendMethod + method) % 3))
                    {
                        case SendMethod.WriteFile:
                            uint bytesWritten;
                            written = WriteFile(mHandle.DangerousGetHandle(), data, (uint)data.Length, out bytesWritten, IntPtr.Zero);
                            if (written) mSendMethod = SendMethod.WriteFile;
                            break;
                        case SendMethod.SetOutputReport:
                            written = HidD_SetOutputReport(mHandle.DangerousGetHandle(), data, (uint)data.Length);
                            if (written) mSendMethod = SendMethod.SetOutputReport;
                            break;
                        case SendMethod.WriteStream:
                            if (mStream != null)
                            {
                                mStream.Write(data, 0, REPORT_LENGTH);
                                written = true;
                                mSendMethod = SendMethod.WriteStream;
                            }
                            break;
                    }
                }
                catch { }
                if (!written && method < 2)
                    Thread.Sleep(50);
            }
        }

        private void BeginAsyncRead()
        {
            if (mStream != null && mStream.CanRead)
            {
                try
                {
                    mStream.BeginRead(mReadBuffer, 0, REPORT_LENGTH, OnReadComplete, null);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[WiiMoteHook] Read failure: " + ex.Message);
                    Disconnect();
                }
            }
        }

        private void OnReadComplete(IAsyncResult result)
        {
            try
            {
                mStream.EndRead(result);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[WiiMoteHook] EndRead failure: " + ex.Message);
                Disconnect();
                return;
            }

            // Parse the report
            ParseReport(mReadBuffer);

            // Start next read
            BeginAsyncRead();
        }

        private void ParseReport(byte[] data)
        {
            byte reportType = data[0];
            bool stateChanged = false;

            switch (reportType)
            {
                case 0x20: // Status
                    ParseButtons(data);
                    ParseBattery(data);
                    ParseLEDs(data);
                    mStatusEvent.Set();
                    break;

                case 0x21: // ReadData
                    ParseButtons(data);
                    ParseReadResponse(data);
                    break;

                case 0x22: // OutputReportAck
                    mWriteEvent.Set();
                    break;

                case 0x30: // Buttons only
                    ParseButtons(data);
                    stateChanged = true;
                    break;

                case 0x31: // Buttons + Accel
                    ParseButtons(data);
                    ParseAccel(data, 3);
                    stateChanged = true;
                    break;

                case 0x33: // Buttons + Accel + IR (Extended)
                    ParseButtons(data);
                    ParseAccel(data, 3);
                    ParseIR(data, 3, true);
                    stateChanged = true;
                    break;

                case 0x35: // Buttons + Accel + IR + Extension (Basic)
                    ParseButtons(data);
                    ParseAccel(data, 3);
                    ParseIR(data, 3, false);
                    stateChanged = true;
                    break;

                case 0x37: // Buttons + Accel + IR + Extension (Extended)
                    ParseButtons(data);
                    ParseAccel(data, 3);
                    ParseIR(data, 6, true);
                    stateChanged = true;
                    break;
            }

            if (stateChanged)
            {
                // Fire event
                DataReceived?.Invoke(this);
            }
        }

        private void ParseButtons(byte[] data)
        {
            mState.ButtonState.A = (data[2] & 0x08) != 0;
            mState.ButtonState.B = (data[2] & 0x04) != 0;
            mState.ButtonState.Minus = (data[2] & 0x10) != 0;
            mState.ButtonState.Home = (data[2] & 0x80) != 0;
            mState.ButtonState.Plus = (data[1] & 0x10) != 0;
            mState.ButtonState.One = (data[2] & 0x02) != 0;
            mState.ButtonState.Two = (data[2] & 0x01) != 0;
            mState.ButtonState.Up = (data[1] & 0x08) != 0;
            mState.ButtonState.Down = (data[1] & 0x04) != 0;
            mState.ButtonState.Left = (data[1] & 0x01) != 0;
            mState.ButtonState.Right = (data[1] & 0x02) != 0;
        }

        private void ParseAccel(byte[] data, int offset)
        {
            mState.AccelState.RawValues.X = data[offset];
            mState.AccelState.RawValues.Y = data[offset + 1];
            mState.AccelState.RawValues.Z = data[offset + 2];
        }

        private void ParseBattery(byte[] data)
        {
            mState.BatteryRaw = data[6];
            mState.Battery = 4800f * (data[6] / 48f) / 192f;
        }

        private void ParseLEDs(byte[] data)
        {
            mState.LEDState.LED1 = (data[3] & 0x10) != 0;
            mState.LEDState.LED2 = (data[3] & 0x20) != 0;
            mState.LEDState.LED3 = (data[3] & 0x40) != 0;
            mState.LEDState.LED4 = (data[3] & 0x80) != 0;
        }

        private void ParseReadResponse(byte[] data)
        {
            if ((data[3] & 0x08) != 0)
                throw new Exception("Error reading data from Wiimote: Bytes do not exist.");
            if ((data[3] & 0x07) != 0)
                throw new Exception("Error reading data from Wiimote: write-only registers.");

            int len = (data[3] >> 4) + 1;
            int addr = (data[4] << 8) | data[5];
            Array.Copy(data, 6, mResponseBuffer, addr - mExpectedAddress, len);

            if (mExpectedAddress + mExpectedSize == addr + len)
                mReadEvent.Set();
        }

        private void ParseIR(byte[] data, int offset, bool extended)
        {
            if (extended)
            {
                for (int i = 0; i < 4; i++)
                {
                    int o = offset + i * 3;
                    mState.IRState.IRSensors[i].RawPosition.X = data[o] | (((data[o + 2] >> 4) & 0x03) << 8);
                    mState.IRState.IRSensors[i].RawPosition.Y = data[o + 1] | (((data[o + 2] >> 6) & 0x03) << 8);
                    mState.IRState.IRSensors[i].Size = data[o + 2] & 0x0F;
                    mState.IRState.IRSensors[i].Found = data[o] != 0xFF || data[o + 1] != 0xFF || data[o + 2] != 0xFF;
                }
            }
            else
            {
                mState.IRState.IRSensors[0].RawPosition.X = data[offset] | (((data[offset + 2] >> 4) & 0x03) << 8);
                mState.IRState.IRSensors[0].RawPosition.Y = data[offset + 1] | (((data[offset + 2] >> 6) & 0x03) << 8);

                mState.IRState.IRSensors[1].RawPosition.X = data[offset + 3] | ((data[offset + 2] & 0x03) << 8);
                mState.IRState.IRSensors[1].RawPosition.Y = data[offset + 4] | (((data[offset + 2] >> 2) & 0x03) << 8);

                mState.IRState.IRSensors[2].RawPosition.X = data[offset + 5] | (((data[offset + 5 + 2] >> 4) & 0x03) << 8);
                mState.IRState.IRSensors[2].RawPosition.Y = data[offset + 6] | (((data[offset + 5 + 2] >> 6) & 0x03) << 8);

                mState.IRState.IRSensors[3].RawPosition.X = data[offset + 8] | ((data[offset + 5 + 2] & 0x03) << 8);
                mState.IRState.IRSensors[3].RawPosition.Y = data[offset + 9] | (((data[offset + 5 + 2] >> 2) & 0x03) << 8);

                for (int i = 0; i < 4; i++)
                {
                    mState.IRState.IRSensors[i].Size = 0;
                    mState.IRState.IRSensors[i].Found = true; // Simplified
                }
            }
        }

        public void Disconnect()
        {
            IsConnected = false;
            if (mStream != null) { try { mStream.Close(); } catch { } mStream = null; }
            if (mHandle != null) { try { mHandle.Close(); } catch { } mHandle = null; }
        }

        public void Dispose()
        {
            Disconnect();
            mReadEvent?.Dispose();
            mWriteEvent?.Dispose();
            mStatusEvent?.Dispose();
        }
    }
}
