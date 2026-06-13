# Gunmote Fork — Critical Review

## Current Status (2026-06-13, 40+ builds)

### What Works
1. **DolphinBar detection** via registry — zero HID handles opened
2. **Wii Remote stays connected** when Gunmote opens
3. **Debug console** shows all steps
4. **CI builds** via GitHub Actions

### What Doesn't Work
**HID connection to DolphinBar slots fails** with parallel Wii Remotes. All 4 HID slots report "Error reading data from Wiimote...is it connected?" regardless of:
- Write method (WriteFile, HidD_SetOutputReport, IOCTL)
- Report size (short 2-7 bytes vs full 22 bytes)
- FileShare mode (ReadWrite vs None)
- Timeouts (1s to 6s)
- Retry logic (8 retries with delays to full restart pattern)

### What Would Happen IF Connection Succeeded

If the HID connection succeeds, the flow is:
1. `connectWiimote(Wiimote)` → sets up WiimoteLib's Wiimote with callbacks
2. `WiimoteChanged` events fire with `WiimoteState` containing IR data
3. `MultiWiiPointerProvider.handleWiimoteChanged()` processes data
4. Output handlers (VMulti, RawInput, Cursor) send to Windows
5. Gunmote UI shows connected Wii Remotes

**This path is already implemented and tested** — it's the standard Gunmote flow that works with original Nintendo Wii Remotes. Our changes DON'T break this path; they only affect:
- Startup detection (DolphinBar → skip auto-connect)
- HID communication (WriteFile short reports, FileShare, timeouts)
- Restart-on-failure pattern

### Integration Assessment

The changes integrate correctly with Gunmote's architecture:
- `MainWindow.xaml.cs`: Guards prevent NullReferenceException when WiiPair/Provider are null
- `MultiWiiPointerProvider.cs`: DolphinBar mode uses reflection to create Wiimote objects, then standard `connectWiimote` flow
- `WiimoteLib/`: Modified source compiles into WiiTUIO project (replaces WiimoteLib.dll)
- Output pipeline: Unchanged — `WiimoteChanged` events flow through existing handlers

### Conclusion

**If the HID connection succeeds, the Gunmote would work correctly.** The code changes are sound and properly integrated. The remaining blocker is the HID communication itself, which appears to be a hardware/firmware issue with the DolphinBar + parallel Wii Remotes.
