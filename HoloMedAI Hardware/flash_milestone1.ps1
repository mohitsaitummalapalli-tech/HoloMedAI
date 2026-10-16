# HoloMedAI Hardware - Milestone 1 Compile & Flash Script
param(
    [string]$Port
)

$HardwareDir = $PSScriptRoot
$BinDir = Join-Path $HardwareDir "bin"
$SketchDir = Join-Path $HardwareDir "milestone1_esp32"
$DriverDir = Join-Path $HardwareDir "CP210x_Driver"
$CliPath = Join-Path $BinDir "arduino-cli.exe"

Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host "   HoloMed AI - ESP32 Milestone 1 Flash Automation   " -ForegroundColor Cyan
Write-Host "=====================================================" -ForegroundColor Cyan

# 1. Detect Port if not provided
if (-not $Port) {
    Write-Host "[*] Scanning for connected COM ports..." -ForegroundColor Yellow
    $ports = [System.IO.Ports.SerialPort]::GetPortNames()

    if ($ports.Count -eq 0) {
        Write-Host ""
        Write-Host "[!] No active COM ports detected!" -ForegroundColor Red
        Write-Host "    Reason: The ESP32 (CP2102) is connected, but Windows is missing the VCP driver." -ForegroundColor Yellow
        Write-Host "    To fix: Run the installer at:" -ForegroundColor White
        Write-Host "    $DriverDir\CP210xVCPInstaller_x64.exe" -ForegroundColor Green
        Write-Host "    (Double-click the file and click 'Next' to install)." -ForegroundColor White
        Write-Host ""
        return
    } elseif ($ports.Count -eq 1) {
        $Port = $ports[0]
        Write-Host "[+] Auto-detected port: $Port" -ForegroundColor Green
    } else {
        Write-Host "[?] Multiple COM ports detected: $($ports -join ', ')" -ForegroundColor Yellow
        $Port = Read-Host "Please enter the target COM port (e.g., $($ports[0]))"
    }
}

# 2. Compile Sketch
Write-Host "`n[*] Compiling milestone1_esp32 for FQBN esp32:esp32:esp32..." -ForegroundColor Yellow
& $CliPath compile --fqbn esp32:esp32:esp32 $SketchDir
if ($LASTEXITCODE -ne 0) {
    Write-Host "[-] Compilation failed!" -ForegroundColor Red
    return
}
Write-Host "[+] Compilation successful!" -ForegroundColor Green

# 3. Upload Sketch
Write-Host "`n[*] Uploading firmware to $Port..." -ForegroundColor Yellow
& $CliPath upload -p $Port --fqbn esp32:esp32:esp32 $SketchDir
if ($LASTEXITCODE -ne 0) {
    Write-Host "[-] Upload failed! Ensure the port is not open in another serial monitor." -ForegroundColor Red
    return
}
Write-Host "[+] Firmware successfully uploaded to ESP32!" -ForegroundColor Green

# 4. Prompt to start listener
$startListener = Read-Host "`nWould you like to start hardware_listener.py now? (Y/n)"
if ($startListener -ne 'n' -and $startListener -ne 'N') {
    python (Join-Path $HardwareDir "hardware_listener.py") -p $Port
}
