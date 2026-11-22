<#
  Removes Claude Tracker and the account shortcuts and launchers made by install.ps1.
  Sign-ins (the %APPDATA%\Claude-N folders) are kept unless -RemoveSignIns is given.
#>
param(
    [switch]$RemoveSignIns,
    [switch]$Quiet,
    [string]$InstallRoot = (Join-Path $env:USERPROFILE "Apps")
)
$tracker = Join-Path $InstallRoot "Claude Tracker\Claude Tracker.exe"
if (Test-Path $tracker) {
    $p = Start-Process $tracker -ArgumentList "--unregister" -PassThru; $null = $p.WaitForExit(20000)   # take the limits plugin out of Claude Code's settings
}
$folders = @(Get-ChildItem $InstallRoot -Directory -Filter "Claude Account *" -ErrorAction SilentlyContinue) + @(Get-Item (Join-Path $InstallRoot "Claude Tracker") -ErrorAction SilentlyContinue)
foreach ($d in $folders) {
    $folder = $d.FullName
    Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($folder + "\") } | Stop-Process -Force
}
Start-Sleep -Milliseconds 500

# Shortcuts that point into those folders, wherever they are.
$sh = New-Object -ComObject WScript.Shell
$places = @([Environment]::GetFolderPath("Desktop"), [Environment]::GetFolderPath("Programs"), [Environment]::GetFolderPath("Startup"),
            (Join-Path ([Environment]::GetFolderPath("Programs")) "Claude Accounts"),
            (Join-Path $env:APPDATA "Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"))
foreach ($pl in $places) {
    Get-ChildItem $pl -Filter *.lnk -ErrorAction SilentlyContinue | ForEach-Object {
        $k = $sh.CreateShortcut($_.FullName)
        $hit = $false
        foreach ($d in $folders) { if ($k.TargetPath.StartsWith($d.FullName + "\") -or $k.IconLocation.StartsWith($d.FullName + "\")) { $hit = $true } }
        if ($hit) { Remove-Item $_.FullName -Force }
    }
}
Remove-Item (Join-Path ([Environment]::GetFolderPath("Programs")) "Claude Accounts") -Recurse -Force -ErrorAction SilentlyContinue
foreach ($d in $folders) { Remove-Item $d.FullName -Recurse -Force -ErrorAction SilentlyContinue }

if ($RemoveSignIns) {
    Get-ChildItem $env:APPDATA -Directory | Where-Object Name -match '^Claude-\d+$' | Remove-Item -Recurse -Force
}
if (-not $Quiet) { Write-Host "Removed." -ForegroundColor Green; Read-Host "Press Enter to close" | Out-Null }
