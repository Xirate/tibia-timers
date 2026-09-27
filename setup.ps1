# Guided setup for Tibia Timers: builds both programs, finds Tibia and creates desktop shortcuts.
# Easiest way to run it: double-click setup.cmd
# -Yes accepts every default answer (no questions). -ShortcutFolder puts the shortcuts somewhere other than the desktop.
param(
    [switch]$Yes,
    [string]$ShortcutFolder = [Environment]::GetFolderPath('Desktop')
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

function Step($n, $text) { Write-Host ""; Write-Host "[$n/5] $text" -ForegroundColor Cyan }
function Ok($text)   { Write-Host "  OK  $text" -ForegroundColor Green }
function Warn($text) { Write-Host "  !!  $text" -ForegroundColor Yellow }
function Info($text) { Write-Host "      $text" }

function Ask($question, [bool]$default = $true) {
    if ($Yes) { return $default }
    $hint = if ($default) { '[Y/n]' } else { '[y/N]' }
    while ($true) {
        $a = (Read-Host "  ?   $question $hint").Trim().ToLower()
        if ($a -eq '') { return $default }
        if ($a -in 'y', 'yes', 't', 'tak') { return $true }
        if ($a -in 'n', 'no', 'nie') { return $false }
    }
}

function Fail($text) {
    Write-Host ""
    Write-Host "  XX  $text" -ForegroundColor Red
    if (-not $Yes) { Read-Host "Press Enter to close" | Out-Null }
    exit 1
}

Write-Host "=============================================" -ForegroundColor DarkCyan
Write-Host "  Tibia Timers - setup" -ForegroundColor White
Write-Host "=============================================" -ForegroundColor DarkCyan
Info "This will build TibiaTimers.exe and TibiaLauncher.exe in:"
Info $root
Info "and optionally create desktop shortcuts. Nothing is installed elsewhere."

# ---------------------------------------------------------------- 1. compiler
Step 1 "Checking the C# compiler (part of Windows / .NET Framework 4)"
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) {
    Fail ".NET Framework 4.x was not found. Install '.NET Framework 4.8' from Microsoft and run setup again."
}
Ok $csc
foreach ($f in 'TibiaTimers.cs', 'TibiaLauncher.cs') {
    if (-not (Test-Path (Join-Path $root $f))) { Fail "$f is missing - run setup from the project folder." }
}
# Files downloaded as a ZIP are marked as coming from the internet; clear that so they run without warnings.
Get-ChildItem $root -File | Unblock-File -ErrorAction SilentlyContinue

# ---------------------------------------------------------------- 2. close running copies
Step 2 "Making sure the programs are not running (Windows locks running .exe files)"
Add-Type -Namespace Setup -Name Paths -MemberDefinition @'
[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
static extern uint GetLongPathName(string s, System.Text.StringBuilder b, uint n);
public static string Long(string p) { var b = new System.Text.StringBuilder(1024); return GetLongPathName(p, b, 1024) > 0 ? b.ToString() : p; }
'@
$longRoot = [Setup.Paths]::Long($root).TrimEnd('\') + '\'
$running = @(Get-Process TibiaTimers, TibiaLauncher -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and [Setup.Paths]::Long($_.Path).StartsWith($longRoot, [StringComparison]::OrdinalIgnoreCase) })
if ($running.Count -eq 0) { Ok "Nothing running." }
else {
    Warn ("Running now: " + (($running | ForEach-Object { $_.ProcessName }) -join ', '))
    if (-not (Ask "Close them now? (running countdowns are saved and continue after restart)")) {
        Fail "Close Tibia Timers yourself and run setup again."
    }
    foreach ($p in $running) { [void]$p.CloseMainWindow() }
    foreach ($p in $running) {
        if (-not $p.WaitForExit(5000)) { $p.Kill(); $p.WaitForExit() }
    }
    Ok "Closed."
}

# ---------------------------------------------------------------- 3. build
Step 3 "Building"
foreach ($name in 'TibiaTimers', 'TibiaLauncher') {
    $out = Join-Path $root "$name.exe"
    $log = & $csc /nologo /target:winexe /optimize+ "/out:$out" /r:System.Windows.Forms.dll /r:System.Drawing.dll (Join-Path $root "$name.cs") 2>&1
    if ($LASTEXITCODE -ne 0) {
        $log | ForEach-Object { Info $_ }
        Fail "Building $name failed."
    }
    Ok "$name.exe"
}

# ---------------------------------------------------------------- 4. find Tibia
Step 4 "Finding Tibia"
$default = Join-Path $env:LOCALAPPDATA 'Tibia\Tibia.exe'
$launcherIni = Join-Path $root 'TibiaLauncher.ini'
$tibia = $null
if (Test-Path $launcherIni) {
    $line = Get-Content $launcherIni | Where-Object { $_ -like 'TibiaPath=*' } | Select-Object -First 1
    if ($line) { $p = $line.Substring(10); if (Test-Path $p) { $tibia = $p } }
}
if (-not $tibia -and (Test-Path $default)) { $tibia = $default }

if ($tibia) { Ok $tibia }
else {
    Warn "Tibia.exe was not found in the usual place ($default)."
    if ($Yes) { Warn "Skipping - you can pick it later with 'Change...' in the launcher." }
    elseif (Ask "Pick Tibia.exe now?") {
        Add-Type -AssemblyName System.Windows.Forms
        $dlg = New-Object System.Windows.Forms.OpenFileDialog
        $dlg.Title = 'Select Tibia.exe'
        $dlg.Filter = 'Tibia.exe|Tibia.exe|Programs (*.exe)|*.exe'
        if ($dlg.ShowDialog() -eq 'OK') { $tibia = $dlg.FileName; Ok $tibia }
        else { Warn "No file chosen - you can pick it later with 'Change...' in the launcher." }
    }
}
if ($tibia -and $tibia -ne $default) {
    $close = '1'
    if (Test-Path $launcherIni) {
        $c = Get-Content $launcherIni | Where-Object { $_ -like 'CloseTimers=*' } | Select-Object -First 1
        if ($c) { $close = $c.Substring(12) }
    }
    [IO.File]::WriteAllText($launcherIni, "TibiaPath=$tibia`r`nCloseTimers=$close`r`n", (New-Object Text.UTF8Encoding $false))
    Info "Saved the location for the launcher."
}
$iconSource = if ($tibia) { $tibia } else { Join-Path $root 'TibiaLauncher.exe' }

# ---------------------------------------------------------------- 5. shortcuts
Step 5 "Shortcuts in $ShortcutFolder"
$launcher = Join-Path $root 'TibiaLauncher.exe'
$shortcuts = @(
    @{ Name = 'Tibia + Timers';    Args = '--timers';    Default = $true;  Desc = 'Start Tibia with Tibia Timers' },
    @{ Name = 'Tibia (no timers)'; Args = '--no-timers'; Default = $true;  Desc = 'Start Tibia without Tibia Timers' },
    @{ Name = 'Tibia Launcher';    Args = '';            Default = $false; Desc = 'Choose: Tibia with or without timers' },
    @{ Name = 'Tibia Timers';      Args = '';            Default = $false; Desc = 'Only the timers'; Target = (Join-Path $root 'TibiaTimers.exe') }
)
$ws = New-Object -ComObject WScript.Shell
$made = 0
foreach ($s in $shortcuts) {
    $path = Join-Path $ShortcutFolder ($s.Name + '.lnk')
    $exists = Test-Path $path
    $q = "Create '$($s.Name)' ($($s.Desc))" + $(if ($exists) { ' - already exists, replace it' } else { '' }) + '?'
    if (-not (Ask $q $s.Default)) { continue }
    $target = if ($s.Target) { $s.Target } else { $launcher }
    $lnk = $ws.CreateShortcut($path)
    $lnk.TargetPath = $target
    $lnk.Arguments = $s.Args
    $lnk.WorkingDirectory = $root
    $lnk.Description = $s.Desc
    $lnk.IconLocation = $(if ($s.Target) { "$target,0" } else { "$iconSource,0" })
    $lnk.Save()
    Ok "$($s.Name)"
    $made++
}
if ($made -eq 0) { Info "No shortcuts created." }

# ---------------------------------------------------------------- done
Write-Host ""
Write-Host "=============================================" -ForegroundColor DarkCyan
Write-Host "  All done!" -ForegroundColor Green
Write-Host "=============================================" -ForegroundColor DarkCyan
Info "First time? Open Tibia Timers, then for each item:"
Info "  Set region -> drag a box over its action bar slot in Tibia"
Info "  Sound...   -> pick an .mp3 / .wav file"
Info "Settings are saved next to the .exe files. Run setup again after updating the code."
Write-Host ""
if (-not $Yes -and (Ask "Open Tibia Timers now to set it up?")) {
    Start-Process (Join-Path $root 'TibiaTimers.exe') -WorkingDirectory $root
}
