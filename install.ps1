<#
  Claude Accounts installer
  One Claude desktop shortcut per account (each stays signed in on its own and never merges with
  the others), plus Claude Tracker: a small always-on-top panel with each account's plan limits and
  the conversations waiting for you, where you can also add accounts and change their icons later.

  Usage:  double-click install.cmd
     or:  powershell -ExecutionPolicy Bypass -File install.ps1 [-Accounts 3] [-NoWidget] [-Quiet]
#>
param(
    [int]$Accounts = 0,
    [switch]$NoWidget,
    [switch]$Quiet,
    [string]$InstallRoot = (Join-Path $env:USERPROFILE "Apps")
)
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
function Say($t, $c = "Gray") { if (-not $Quiet) { Write-Host $t -ForegroundColor $c } }
function Done($code) { if (-not $Quiet) { Read-Host "Press Enter to close" | Out-Null }; exit $code }

# ---- checks
$claude = Join-Path $env:LOCALAPPDATA "Microsoft\WindowsApps\claude-desktop.exe"
if (-not (Test-Path $claude)) {
    Say "The Claude desktop app isn't installed. Install it from https://claude.ai/download, then run this again." Red
    Done 1
}
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) { Say "Couldn't find the .NET Framework 4 compiler that comes with Windows." Red; Done 1 }

# ---- questions
if ($Accounts -lt 1) {
    $ans = Read-Host "How many Claude accounts do you want shortcuts for? (1-9, default 3)"
    $Accounts = if ($ans -match '^[1-9]$') { [int]$ans } else { 3 }
}
if (-not $NoWidget -and -not $Quiet) {
    $ans = Read-Host "Also install Claude Tracker, the little panel with limits and conversations? (Y/n)"
    if ($ans -match '^[nN]') { $NoWidget = $true }
}

# ---- build Claude Tracker (it also makes the account launchers, icons and shortcuts)
$dir = Join-Path $InstallRoot "Claude Tracker"
$exe = Join-Path $dir "Claude Tracker.exe"
New-Item -ItemType Directory -Force $dir | Out-Null
Get-Process -Name "Claude Tracker" -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
Start-Sleep -Milliseconds 300

$src = Join-Path $here "src"
$files = Get-ChildItem (Join-Path $src "tracker") -Filter *.cs | ForEach-Object { $_.FullName }
$tmp = Join-Path $env:TEMP "claude-tracker-build.exe"
$log = & $csc /nologo /target:winexe /optimize /codepage:65001 "/win32icon:$(Join-Path $src 'tracker.ico')" "/resource:$(Join-Path $src 'launcher.cs'),launcher.cs" `
    /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll `
    /r:Microsoft.CSharp.dll /r:System.Core.dll "/out:$tmp" $files 2>&1
if ($LASTEXITCODE -ne 0) { Say "Couldn't build Claude Tracker:`n$log" Red; Done 1 }
Move-Item $tmp $exe -Force
# The limits plugin lets Claude Code save its plan-limit readings for the panel.
if (-not $NoWidget) { Copy-Item (Join-Path $here "plugins") $dir -Recurse -Force }

$setupArgs = @("--setup", "$Accounts")
if ($NoWidget) { $setupArgs += "--no-widget" }
$p = Start-Process $exe -ArgumentList $setupArgs -PassThru
if (-not $p.WaitForExit(180000)) { Say "Setup is taking too long; stopping." Red; $p.Kill(); Done 1 }
if ($p.ExitCode -ne 0) { Say "Setup didn't finish (code $($p.ExitCode))." Red; Done 1 }

if (-not $NoWidget) { Start-Process explorer.exe "`"$exe`"" }   # starts it as a normal app, not as a child of this window

Say ""
Say "Done." Green
Say "  - $Accounts Claude shortcut(s) on your desktop and in the Start menu folder 'Claude Accounts'."
Say "  - Open each one once and sign in to a different account; each stays signed in on its own."
Say "  - To keep one on the taskbar: open it, right-click its taskbar button, Pin to taskbar."
if ($NoWidget) { Say "  - Add accounts or change icons later: Start menu > Claude Accounts > Manage Claude accounts." }
else { Say "  - Claude Tracker is running and starts with Windows. Its gear button adds accounts and changes icons." }
Done 0
