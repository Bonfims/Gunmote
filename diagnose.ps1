# Diagnostic script for DolphinBar + Gunmote
# Run this in PowerShell as Administrator
# Shows all relevant HID and Bluetooth devices

Write-Host "=== HID Devices (Nintendo & Mayflash) ===" -ForegroundColor Green

# List all HID devices with Nintendo VID (0x057E) or Mayflash VID (0x0079)
Get-PnpDevice -Class HIDClass | Where-Object {
    $id = $_.InstanceId
    $id -match "VID_057E" -or $id -match "VID_0079" -or $id -match "VID_057e" -or $id -match "VID_0079"
} | Format-Table FriendlyName, Status, InstanceId -AutoSize

Write-Host "`n=== Bluetooth Radios ===" -ForegroundColor Green
Get-PnpDevice -Class Bluetooth | Format-Table FriendlyName, Status, InstanceId -AutoSize

Write-Host "`n=== Bluetooth HID Devices (BTHENUM) ===" -ForegroundColor Green
Get-PnpDevice | Where-Object {
    $_.InstanceId -match "BTHENUM"
} | Format-Table FriendlyName, Status, InstanceId -AutoSize

Write-Host "`n=== All HID devices with paths ===" -ForegroundColor Green
Get-PnpDevice -Class HIDClass | Where-Object {
    $_.FriendlyName -match "Nintendo|Mayflash|vmulti|Wiimote|wiimote|Dolphin|dolphin|RVL"
} | ForEach-Object {
    Write-Host "$($_.FriendlyName) | Status: $($_.Status) | $($_.InstanceId)"
}

Write-Host "`n=== USB Devices ===" -ForegroundColor Green
Get-PnpDevice -Class USB | Where-Object {
    $_.FriendlyName -match "Mayflash|DolphinBar|CSR|Bluetooth"
} | Format-Table FriendlyName, Status -AutoSize

Write-Host "`n=== Joy.CPL devices (game controllers) ===" -ForegroundColor Green
Get-PnpDevice -Class HIDClass | Where-Object {
    $_.FriendlyName -match "Mayflash|vmulti|Virtual"
} | Format-Table FriendlyName, Status, InstanceId -AutoSize

Write-Host "`nDone. Send this output to Claude." -ForegroundColor Yellow
