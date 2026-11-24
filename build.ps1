# Builds Clocked In and (re)starts it.  Usage:  .\build.ps1   (add -NoRun to only build)
param([switch]$NoRun)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $root 'bin'
$exe = Join-Path $out 'ClockedIn.exe'
New-Item -ItemType Directory -Force $out | Out-Null

# 1. close a running instance gracefully (so its tray icon disappears cleanly)
try {
    $ev = [System.Threading.EventWaitHandle]::OpenExisting('Local\WorkHours_Quit')
    [void]$ev.Set(); $ev.Dispose()
} catch {}
$deadline = (Get-Date).AddSeconds(5)
while ((Get-Process ClockedIn, WorkHours -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 150 }
Get-Process ClockedIn, WorkHours -ErrorAction SilentlyContinue | Stop-Process -Force

# 2. icon
$ico = Join-Path $root 'assets\app.ico'
if (-not (Test-Path $ico)) { & (Join-Path $root 'tools\make-icon.ps1') -Out $ico }

# 3. compile with the C# compiler that ships with Windows (.NET Framework 4.8)
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wpf = "$fw\WPF"
$refs = @(
    "$wpf\PresentationFramework.dll", "$wpf\PresentationCore.dll", "$wpf\WindowsBase.dll",
    "$fw\System.Xaml.dll", "$fw\System.Windows.Forms.dll", "$fw\System.Drawing.dll",
    "$fw\System.IO.Compression.dll"
) | ForEach-Object { "/r:$_" }
$sources = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
$output = & "$fw\csc.exe" /nologo /target:winexe /optimize+ /warn:3 "/out:$exe" "/win32icon:$ico" "/win32manifest:$(Join-Path $root 'assets\app.manifest')" $refs $sources 2>&1
$output | Where-Object { $_ -notmatch 'only supports language versions up to C# 5|go.microsoft.com/fwlink/\?LinkID=533240|^\s*$|This compiler is provided as part' } | ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "Built $exe"

# 4. run
if (-not $NoRun) { Start-Process $exe; Write-Host "Started Clocked In" }
