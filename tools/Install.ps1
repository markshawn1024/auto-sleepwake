$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework
try {
    $installTarget = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\WakeGlass'))
    $installedExe = Join-Path $installTarget 'WakeGlass.exe'
    $sourceDir = [IO.Path]::GetFullPath($PSScriptRoot)
    if (!(Test-Path -LiteralPath (Join-Path $sourceDir 'WakeGlass.exe'))) { throw 'WakeGlass.exe is missing from this folder.' }
    $running = Get-Process WakeGlass -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $installedExe }
    if ($running) { throw 'Pause the multi-stage plan, then exit the installed WakeGlass from its tray menu before updating.' }
    if ($sourceDir -ne $installTarget) {
        New-Item -ItemType Directory -Path $installTarget -Force | Out-Null
        Get-ChildItem -LiteralPath $sourceDir -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $installTarget $_.Name) -Force }
    }
    $menuPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'WakeGlass'
    New-Item -ItemType Directory -Path $menuPath -Force | Out-Null
    $shortcutShell = New-Object -ComObject WScript.Shell
    $shortcut = $shortcutShell.CreateShortcut((Join-Path $menuPath 'WakeGlass.lnk'))
    $shortcut.TargetPath = $installedExe; $shortcut.WorkingDirectory = $installTarget; $shortcut.IconLocation = "$installedExe,0"; $shortcut.Save()
    $uninstallShortcut = $shortcutShell.CreateShortcut((Join-Path $menuPath 'Uninstall WakeGlass.lnk'))
    $uninstallShortcut.TargetPath = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
    $uninstallShortcut.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $installTarget 'Uninstall.ps1') + '"'
    $uninstallShortcut.WindowStyle = 7; $uninstallShortcut.Save()
    [System.Windows.MessageBox]::Show('Installed for the current user. Open WakeGlass from the Start menu. If schedules were created from a portable folder, save them again from the installed app to update their executable path.', 'WakeGlass', 'OK', 'Information') | Out-Null
} catch { [System.Windows.MessageBox]::Show($_.Exception.Message, 'WakeGlass installation', 'OK', 'Error') | Out-Null; exit 1 }
