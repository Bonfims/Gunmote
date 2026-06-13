using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Windows;
using WiimoteLib;
using System.Runtime.InteropServices;
using System.Drawing;
using WindowsInput;
using WiiTUIO.Properties;
using System.Windows.Controls;
using System.Threading;
using WiiTUIO.Output;
using WiiTUIO;

namespace WiiTUIO.Provider
{
    /// <summary>
    /// The WiiProvider implements <see cref="IProvider"/> in order to offer a type of object which uses the Wiimote to generate new event frames.
    /// </summary>
    public class MultiWiiPointerProvider : IProvider
    {
        private int WIIMOTE_POWER_SAVE_DISCONNECT_TIMEOUT = 15000;
        private int POWER_SAVE_STATUS_INTERVAL = 6000;

        private int WIIMOTE_DISCONNECT_TIMEOUT = 2000; //If we haven't recieved input from a wiimote in 2 seconds we consider it disconnected.
        private int WIIMOTE_SIGNIFICANT_DISCONNECT_TIMEOUT = Settings.Default.autoDisconnectTimeout; //If we haven't recieved significant input from a wiimote in 60 seconds we will put it to sleep
        private ulong OLD_FRAME_TIMEOUT = 200; //Timeout for a previous frame from a Wiimote to be considered old, so we wont enable it when getting input from other wiimotes.
        private int CONNECTION_THREAD_SLEEP = 2000;
        private int POWER_SAVE_BLINK_DELAY = 10000;
        private int CONNECT_RUMBLE_TIME = 100;

        private int cursorUpdateToggle = 0;

        private int blinkWait = 0;
        private int statusWait = 0;

        private Mutex pDeviceMutex = new Mutex();
        private Mutex connectionMutex = new Mutex();

        private Timer wiimoteConnectorTimer;
        private Thread wiimoteHandlerThread;

        private Dictionary<string, WiimoteControl> pWiimoteMap = new Dictionary<string, WiimoteControl>();

        private Dictionary<string, WiimoteChangedEventArgs> eventBuffer = new Dictionary<string, WiimoteChangedEventArgs>();

        private WiimoteCollection pWC;

        private bool readyToRender = false;

        private EventHandler<WiimoteChangedEventArgs> wiimoteChangedEventHandler;
        private EventHandler<WiimoteExtensionChangedEventArgs> wiimoteExtensionChangedEventHandler;

        #region Properties and Constructor
        /// <summary>
        /// Boolean which indicates if we are generating input or not.
        /// </summary>
        private bool bRunning = false;

        /// <summary>
        /// A property to determine if this input provider is running (and thus generating events).
        /// </summary>
        public bool IsRunning { get { return this.bRunning; } }

        #region Battery State
        /// <summary>
        /// An event which is fired when the battery state changes.
        /// </summary>
        public event Action<WiimoteStatus> OnStatusUpdate;

        public event Action<int, int> OnConnect;
        public event Action<int, int> OnDisconnect;


        #endregion

        /// <summary>
        /// Construct a new wiimote provider.
        /// </summary>
        public MultiWiiPointerProvider()
        {
            this.pWC = new WiimoteCollection();

            this.wiimoteChangedEventHandler = new EventHandler<WiimoteChangedEventArgs>(handleWiimoteChanged);
            this.wiimoteExtensionChangedEventHandler = new EventHandler<WiimoteExtensionChangedEventArgs>(handleWiimoteExtensionChanged);

            wiimoteConnectorTimer = new Timer(wiimoteConnectorTimer_Elapsed, null, Timeout.Infinite, CONNECTION_THREAD_SLEEP);

            wiimoteHandlerThread = new Thread(WiimoteHandlerWorker);
            wiimoteHandlerThread.Priority = ThreadPriority.Highest;
            wiimoteHandlerThread.IsBackground = true;
            wiimoteHandlerThread.Start();

        }

        #endregion

        #region Start and Stop

        /// <summary>
        /// Instructs this input provider to begin generating events.
        /// </summary>
        public void start()
        {
            Console.WriteLine("Start");
            this.bRunning = true;
            wiimoteConnectorTimer.Change(0, Timeout.Infinite);
        }

        bool waitingToConnect = false;

        void wiimoteConnectorTimer_Elapsed(object sender)
        {
            wiimoteConnectorTimer.Change(Timeout.Infinite, Timeout.Infinite);
            Console.WriteLine("wiimoteConnectorTimer_Elapsed");
            if (this.bRunning)
            {
                Exception pError;
                if (!this.initialiseWiimoteConnections(out pError))
                {
                    Console.WriteLine("Could not establish connection to a Wiimote: " + pError.Message, pError);
                }
                wiimoteConnectorTimer.Change(CONNECTION_THREAD_SLEEP, Timeout.Infinite);
            }
        }

        /// <summary>
        /// Instructs this input provider to stop generating events.
        /// </summary>
        public void stop()
        {
            Console.WriteLine("stop");
            // Set the running flag.
            this.bRunning = false;

            this.wiimoteConnectorTimer.Change(Timeout.Infinite, Timeout.Infinite);

            this.teardownWiimoteConnections();

            this.pWC.Clear();
        }
        #endregion

        #region Connection creation and teardown.
        /// <summary>  
        /// This method creates and sets up our connection to our class-gloal Wiimote device.
        /// This destroys any existing connection before creating a new one.
        /// </summary>
        /// <param name="pErrorReport">A reference to an exception which we want to contain our error if one happened.</param>
        private bool initialiseWiimoteConnections(out Exception pErrorReport)
        {
            this.connectionMutex.WaitOne();
            // If we have an existing device, teardown the connection.
            //this.teardownWiimoteConnection();

            pErrorReport = null;
            bool isDolphinBar = false;
            bool anyConnected = false;

            try
            {
                Dictionary<string, WiimoteControl> copy = new Dictionary<string, WiimoteControl>(pWiimoteMap);
                foreach (WiimoteControl control in copy.Values)
                {
                    Wiimote pDevice = control.Wiimote;
                    try
                    {
                        if (!control.Status.InPowerSave
                            && control.LastWiimoteEventTime != null
                            && DateTime.Now.Subtract(control.LastWiimoteEventTime).TotalMilliseconds > WIIMOTE_DISCONNECT_TIMEOUT)
                        {
                            Console.WriteLine("Teardown 1 " + pDevice.HIDDevicePath + " because of timeout with delta " + DateTime.Now.Subtract(pWiimoteMap[pDevice.HIDDevicePath].LastWiimoteEventTime).TotalMilliseconds);
                            teardownWiimoteConnection(control.Wiimote);
                        }
                        else if (!control.Status.InPowerSave
                            && control.LastSignificantWiimoteEventTime != null
                            && DateTime.Now.Subtract(control.LastSignificantWiimoteEventTime).TotalMilliseconds > WIIMOTE_SIGNIFICANT_DISCONNECT_TIMEOUT)
                        {
                            Console.WriteLine("Put " + pDevice.HIDDevicePath + " to power saver mode because of timeout with delta " + DateTime.Now.Subtract(control.LastSignificantWiimoteEventTime).TotalMilliseconds);
                            //teardownWiimoteConnection(pWiimoteMap[pDevice.HIDDevicePath].Wiimote);
                            putToPowerSave(control);
                        }
                        else if (control.Status.InPowerSave
                        && control.LastWiimoteEventTime != null
                        && DateTime.Now.Subtract(control.LastWiimoteEventTime).TotalMilliseconds > WIIMOTE_POWER_SAVE_DISCONNECT_TIMEOUT)
                        {
                            Console.WriteLine("Teardown 2 " + pDevice.HIDDevicePath + " because of timeout with delta " + DateTime.Now.Subtract(control.LastWiimoteEventTime).TotalMilliseconds);
                            teardownWiimoteConnection(control.Wiimote);
                        }
                        else if (control.Status.InPowerSave)
                        {


                            if (CONNECTION_THREAD_SLEEP * statusWait >= POWER_SAVE_STATUS_INTERVAL)
                            {
                                statusWait = 0;
                                control.WiimoteMutex.WaitOne();
                                try
                                {
                                    control.Wiimote.GetStatus();
                                }
                                catch { }
                                control.WiimoteMutex.ReleaseMutex();
                            }
                            else
                            {
                                statusWait++;
                            }

                            if (CONNECTION_THREAD_SLEEP * blinkWait >= POWER_SAVE_BLINK_DELAY)
                            {
                                blinkWait = 0;
                                control.Wiimote.SetLEDs(true, true, true, true);
                                Thread.Sleep(100);
                                control.Wiimote.SetLEDs(false, false, false, false);
                            }
                            else
                            {
                                blinkWait++;
                            }
                        }
                    }
                    catch (Exception pError)
                    {
                        try
                        {
                            Console.WriteLine("Teardown 3 " + pDevice.HIDDevicePath + " because of " + pError.Message);
                            this.teardownWiimoteConnection(pDevice);
                        }
                        finally
                        {
                        }
                        pErrorReport = pError;
                    }
                }

                isDolphinBar = DolphinBarHelper.IsDolphinBarPresent();

                // Dispose old Wiimotes before clearing — matches Wii Mote Hooks GClass1.Dispose
                // which closes all GClass12 handles on restart. Without this, old FileStream
                // and SafeFileHandle instances keep HID slots locked and prevent reconnection.
                foreach (Wiimote oldDevice in this.pWC)
                {
                    try { oldDevice.Disconnect(); } catch { }
                }
                this.pWC.Clear();

                if (isDolphinBar)
                {
                    // DolphinBar mode: skip FindAllWiimotes (opens ALL 4 HID slots and disrupts
                    // the bar's connection). Instead, use reflection to create Wiimote objects
                    // directly for each Wii Remote HID path from the registry.
                    Console.WriteLine("MultiWiiPointerProvider: DolphinBar mode — using reflection to connect directly");

                    var hidPaths = DolphinBarHelper.GetWiimoteDevicePaths();
                    if (hidPaths.Count == 0)
                    {
                        // Registry might not have HID paths, try WiimoteLib as fallback
                        Console.WriteLine("MultiWiiPointerProvider: No registry paths found, scanning all HID...");
                        this.pWC.FindAllWiimotes();
                    }
                    else
                    {
                        // Wii Mote Hooks does a temporary open/close of each HID device
                        // during its scan phase (GClass14.method_0 → GClass12.smethod_1).
                        // The DolphinBar may need this to initialize each slot before
                        // the actual connection attempt.
                        Console.WriteLine("MultiWiiPointerProvider: Pre-scanning DolphinBar slots (open/close to wake)...");
                        foreach (var devicePath in hidPaths)
                        {
                            try
                            {
                                var tempHandle = HIDImports.CreateFile(devicePath,
                                    System.IO.FileAccess.ReadWrite, System.IO.FileShare.ReadWrite,
                                    IntPtr.Zero, System.IO.FileMode.Open,
                                    HIDImports.EFileAttributes.Overlapped, IntPtr.Zero);
                                if (!tempHandle.IsInvalid)
                                {
                                    var tempAttrib = new HIDImports.HIDD_ATTRIBUTES();
                                    tempAttrib.Size = Marshal.SizeOf(tempAttrib);
                                    HIDImports.HidD_GetAttributes(tempHandle.DangerousGetHandle(), ref tempAttrib);
                                    Console.WriteLine("  Pre-scan {0}: VID={1:x4} PID={2:x4}",
                                        devicePath, (ushort)tempAttrib.VendorID, (ushort)tempAttrib.ProductID);
                                    tempHandle.Close();
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("  Pre-scan failed: " + ex.Message);
                            }
                        }
                        Console.WriteLine("MultiWiiPointerProvider: Pre-scan complete.");

                        // Use reflection to call the internal Wiimote(string devicePath) constructor
                        var ctor = typeof(Wiimote).GetConstructor(
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                            null, new Type[] { typeof(string) }, null);

                        if (ctor == null)
                        {
                            Console.WriteLine("MultiWiiPointerProvider: ERROR — Wiimote(string) constructor not found!");
                            // Fallback to normal scan
                            this.pWC.FindAllWiimotes();
                        }
                        else
                        {
                            Console.WriteLine("MultiWiiPointerProvider: Creating Wiimote objects via reflection for {0} paths", hidPaths.Count);
                            foreach (var devicePath in hidPaths)
                            {
                                try
                                {
                                    var wiimote = (Wiimote)ctor.Invoke(new object[] { devicePath });
                                    this.pWC.Add(wiimote);
                                    Console.WriteLine("MultiWiiPointerProvider: Created Wiimote for " + devicePath);
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine("MultiWiiPointerProvider: Failed to create Wiimote: " + ex.Message);
                                }
                            }
                        }
                    }

                    Console.WriteLine("MultiWiiPointerProvider: DolphinBar mode — {0} Wiimotes to connect", this.pWC.Count);
                }
                else
                {
                    Console.WriteLine("MultiWiiPointerProvider: Scanning for Wii Remotes via HID...");
                    this.pWC.FindAllWiimotes();
                    Console.WriteLine("MultiWiiPointerProvider: Found {0} Wii Remote(s) via WiimoteLib", this.pWC.Count);
                }

                // Wii Mote Hooks tries from mi_03 backwards and stops
                // after first success. Opening empty DolphinBar slots may
                // disrupt the slot with the active Wii Remote.
                var devices = new List<Wiimote>(pWC);
                anyConnected = false;
                if (isDolphinBar)
                {
                    devices.Reverse(); // Try mi_03 first, mi_00 last
                    Console.WriteLine("MultiWiiPointerProvider: Reversed device order (Wii Mote Hooks pattern)");
                }

                foreach (Wiimote pDevice in devices)
                {
                    try
                    {
                        if (!pWiimoteMap.Keys.Contains(pDevice.HIDDevicePath))
                        {
                            if (isDolphinBar)
                            {
                                // Wii Mote Hooks pattern: try each device ONCE.
                                // Timeout (GException1) → full restart from scratch.
                                // Other error → remove this device, try next.
                                // The timer callback will retry the whole process.
                                try
                                {
                                    this.connectWiimote(pDevice);
                                    anyConnected = true;
                                }
                                catch (Exception ex)
                                {
                                    // DolphinBar mode: ALL errors on a slot mean "empty slot" —
                                    // timeout or device-not-functioning are both normal for
                                    // slots with no Wii Remote paired. Dispose and try next.
                                    // Only restart (return false) if no slots worked at all.
                                    Console.WriteLine("MultiWiiPointerProvider: Skipping {0}: {1}", pDevice.HIDDevicePath, ex.Message);
                                    try { pDevice.Disconnect(); } catch { }
                                    // Continue to next device
                                }
                            }
                            else
                            {
                                this.connectWiimote(pDevice);
                            }
                        }
                    }
                    // If something went wrong - notify the user..
                    catch (Exception pError)
                    {
                        // Ensure we are ok.
                        try
                        {
                            Console.WriteLine("Teardown 4 " + pDevice.HIDDevicePath + " because of " + pError.Message);
                            this.teardownWiimoteConnection(pDevice);
                        }
                        finally { }
                        // Say we screwed up.
                        pErrorReport = pError;
                        //throw new Exception("Error establishing connection: " + , pError);

                    }

                }
            }
            catch (Exception e)
            {
                pErrorReport = e;
            }

            this.connectionMutex.ReleaseMutex();

            // DolphinBar mode: if we tried all 4 slots and none worked,
            // report the error and trigger a retry on next timer tick.
            if (isDolphinBar && !anyConnected)
            {
                Console.WriteLine("MultiWiiPointerProvider: No Wiimote found on any DolphinBar slot, will retry...");
                pErrorReport = new Exception("No Wiimote responded on any DolphinBar slot");
                return false;
            }

            if (pErrorReport != null)
            {
                return false;
            }
            return true;
        }

        private void connectWiimote(Wiimote wiimote)
        {
            Console.WriteLine("Trying to connect " + wiimote.HIDDevicePath);
            // Try to establish a connection, enable the IR reader and flag some LEDs.
            wiimote.Connect();
            wiimote.SetReportType(InputReport.IRExtensionAccel, IRSensitivity.Maximum, true);

            new Timer(new TimerCallback(connectRumble), wiimote, 0, Timeout.Infinite);

            int id = this.getFirstFreeId();
            wiimote.SetLEDs(id == 1, id == 2, id == 3, id == 4);

            wiimote.WiimoteState.SpeakerState.DataFormat = SpeakerDataFormat.PCM;
            wiimote.WiimoteState.SpeakerState.SampleRate = 6000;
            wiimote.WiimoteState.SpeakerState.Volume = 0xFF;
            wiimote.EnableSpeaker();

            WiimoteControl control = new WiimoteControl(id, wiimote);

            pDeviceMutex.WaitOne(); //Don't mess with the list of wiimotes if it is enumerating in an update
            pWiimoteMap[wiimote.HIDDevicePath] = control;
            pDeviceMutex.ReleaseMutex();

            // Hook up device event handlers.
            wiimote.WiimoteChanged += this.wiimoteChangedEventHandler;
            wiimote.WiimoteExtensionChanged += this.wiimoteExtensionChangedEventHandler;

            OnConnect(id, this.pWiimoteMap.Count);

        }



        private int getFirstFreeId()
        {
            HashSet<int> usedIDs = new HashSet<int>();
            foreach (WiimoteControl control in pWiimoteMap.Values)
            {
                usedIDs.Add(control.Status.ID);
            }

            int id = 1;
            while (usedIDs.Contains(id))
            {
                id++;
            }
            return id;
        }

        private void putToPowerSave(WiimoteControl control)
        {
            control.WiimoteMutex.WaitOne();
            try
            {
                control.Wiimote.SetReportType(InputReport.Buttons, false);
                control.Status.InPowerSave = true;
                control.Wiimote.SetLEDs(false, false, false, false);
                control.Wiimote.SetRumble(false);
                control.Wiimote.SetSpeakerMuteState(true);
            }
            catch { }
            finally
            {
                control.WiimoteMutex.ReleaseMutex();
            }
        }

        private void wakeFromPowerSave(WiimoteControl control)
        {
            control.WiimoteMutex.WaitOne();
            try
            {
                control.Wiimote.SetReportType(InputReport.IRExtensionAccel, IRSensitivity.Maximum, true);
                control.Status.InPowerSave = false;
                int id = control.Status.ID;
                control.Wiimote.SetLEDs(id == 1, id == 2, id == 3, id == 4);
                control.Wiimote.SetRumble(true);
                control.Wiimote.SetSpeakerMuteState(false);
                new Timer(connectRumble, control.Wiimote, 0, Timeout.Infinite);
            }
            catch { }
            finally
            {
                control.WiimoteMutex.ReleaseMutex();
            }
        }

        /// <summary>
        /// This method destroys our connection to our class-global Wiimote device.
        /// </summary>
        private void teardownWiimoteConnections()
        {
            if (pWiimoteMap.Count > 0)
            {
                IEnumerable<WiimoteControl> controls = new Queue<WiimoteControl>(pWiimoteMap.Values);
                foreach (WiimoteControl control in controls)
                {
                    teardownWiimoteConnection(control.Wiimote);
                }
            }
            else
            {
                OnDisconnect?.Invoke(0, 0);
            }
        }

        private void teardownWiimoteConnection(Wiimote pDevice)
        {
            if (pDevice != null)
            {
                pDeviceMutex.WaitOne();
                pDevice.WiimoteChanged -= this.wiimoteChangedEventHandler;
                pDevice.WiimoteExtensionChanged -= this.wiimoteExtensionChangedEventHandler;
                int wiimoteid;
                if (pWiimoteMap.Keys.Contains(pDevice.HIDDevicePath))
                {
                    wiimoteid = this.pWiimoteMap[pDevice.HIDDevicePath].Status.ID;
                    this.pWiimoteMap[pDevice.HIDDevicePath].Teardown();
                    this.pWiimoteMap.Remove(pDevice.HIDDevicePath);
                }
                else
                {
                    wiimoteid = this.pWiimoteMap.Count + 1;
                }
                pDeviceMutex.ReleaseMutex();

                try
                {
                    pDevice.SetReportType(InputReport.Status, false);

                    pDevice.SetRumble(false);
                    pDevice.SetLEDs(true, true, true, true);
                    pDevice.DisableSpeaker();
                }
                catch { }

                // Close the connection and dispose of the device.
                pDevice.Disconnect();
                pDevice.Dispose();

                OnDisconnect?.Invoke(wiimoteid, this.pWiimoteMap.Count);
            }
        }
        #endregion

        private void connectRumble(object device)
        {
            Wiimote wiimote = (Wiimote)device;
            Thread.Sleep(CONNECT_RUMBLE_TIME);
            wiimote.SetRumble(true);
            Thread.Sleep(CONNECT_RUMBLE_TIME);
            wiimote.SetRumble(false);
        }

        #region Wiimote Event Handlers
        /// <summary>
        /// This is called when an extension is attached or unplugged.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void handleWiimoteExtensionChanged(object sender, WiimoteExtensionChangedEventArgs e)
        {

            //pDeviceMutex.WaitOne();

            // Check we have a valid device.
            if (sender == null)
                return;

            Wiimote pDevice = ((Wiimote)sender);
            // If an extension is attached at runtime we want to enable it.
            if (e.Inserted)
            {
                Console.WriteLine("Enabling extension " + e.ExtensionType);
                pDevice.SetReportType(InputReport.IRExtensionAccel, true);
            }
            else
            {
                Console.WriteLine("Disabling extension " + e.ExtensionType);
                pDevice.SetReportType(InputReport.IRAccel, true);
            }

            //pDeviceMutex.ReleaseMutex();

        }


        private void WiimoteHandlerWorker()
        {

            double millisecondsForEachFrame = 1000 / Settings.Default.pointer_FPS;
            DateTime lastFrame = DateTime.Now;

            while (true)
            {
                double delay = DateTime.Now.Subtract(lastFrame).TotalMilliseconds;
                double wait = millisecondsForEachFrame - delay;
                if (wait > 0)
                {
                    Thread.Sleep((int)wait);
                }

                lastFrame = DateTime.Now;

                if (bRunning)
                {
                    //DateTime now = DateTime.Now;

                    pDeviceMutex.WaitOne();

                    try
                    {
                        foreach (WiimoteControl control in pWiimoteMap.Values)
                        {
                            if (eventBuffer.ContainsKey(control.Wiimote.HIDDevicePath))
                            {
                                WiimoteChangedEventArgs e = eventBuffer[control.Wiimote.HIDDevicePath];

                                if (control.handleWiimoteChanged(this, e) && control.Status.InPowerSave)
                                {
                                    this.wakeFromPowerSave(control);
                                }

                                if (this.OnStatusUpdate != null)
                                {
                                    this.OnStatusUpdate(control.Status);
                                }

                            }
                        }
                        D3DCursorWindow.Current.RefreshCursors();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Error handling Wiimote: " + ex.Message);
                    }

                    pDeviceMutex.ReleaseMutex();

                    //Console.WriteLine("handle wiimote time : " + DateTime.Now.Subtract(now).TotalMilliseconds);
                }

            }
        }

        /// <summary>
        /// This is called when the state of the wiimote changes and a new state report is available.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void handleWiimoteChanged(object sender, WiimoteChangedEventArgs e)
        {

            eventBuffer[((Wiimote)sender).HIDDevicePath] = e;

            pWiimoteMap[((Wiimote)sender).HIDDevicePath].LastWiimoteEventTime = DateTime.Now;

        }
        #endregion

        public static UserControl getSettingsControl()
        {
            return new WiiPointerProviderSettings();
        }
    }
}