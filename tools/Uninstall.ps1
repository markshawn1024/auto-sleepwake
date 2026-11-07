$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework
try {
    $installTarget = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\WakeGlass'))
    $executingRoot = [IO.Path]::GetFullPath($PSScriptRoot)
    $canRemoveInstall = $executingRoot -eq $installTarget
    $installedExe = Join-Path $installTarget 'WakeGlass.exe'
    if ($canRemoveInstall -and (Get-CimInstance Win32_Process -Filter "Name = 'WakeGlass.exe'" | Where-Object { $_.ExecutablePath -eq $installedExe -and $_.CommandLine -notmatch '\s--stage-worker\s*$' })) { throw 'Exit WakeGlass using its tray menu before uninstalling.' }
    $choice = [System.Windows.MessageBox]::Show('Remove WakeGlass scheduled tasks too? Yes: remove tasks. No: retain tasks (an installed executable will also be retained so they remain usable). Cancel: stop.', 'WakeGlass uninstall / portable cleanup', 'YesNoCancel', 'Question', 'Cancel')
    if ($choice -eq 'Cancel') { exit 0 }
    if ($choice -eq 'Yes') {
        $service = New-Object -ComObject Schedule.Service; $service.Connect()
        $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        $folderPath = "\WakeGlass-$sid"
        $scheduleLock = [Threading.Mutex]::new($false, "Local\WakeGlass-Schedule-$sid")
        $locked = $false
        try {
            try { $locked = $scheduleLock.WaitOne(15000) } catch [Threading.AbandonedMutexException] { $locked = $true }
            if (!$locked) { throw 'WakeGlass is completing a schedule operation. Try again shortly.' }
            $settingsPath = Join-Path $env:LOCALAPPDATA 'WakeGlass\settings.json'
            if (Test-Path -LiteralPath $settingsPath) {
                $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
                $settings.Schedule = $null
                if ($settings.Stages) { $settings.Stages.Enabled = $false }
                $temporary = $settingsPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
                try {
                    [IO.File]::WriteAllText($temporary, ($settings | ConvertTo-Json -Depth 8))
                    [IO.File]::Replace($temporary, $settingsPath, [NullString]::Value)
                } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
            }
            $folder = $null
            try { $folder = $service.GetFolder($folderPath) } catch { if ($_.Exception.HResult -notin @(-2147024894, -2147024893)) { throw } }
            if ($folder) {
                $ownedNames = @($folder.GetTasks(1) | Where-Object { $_.Name -in @('Daily-Sleep','Daily-Wake','Instant-Wake','Stage-Worker') -or $_.Name -match '^(Probe-|Stage-Wake-)[a-f0-9]{32}$' } | ForEach-Object {
                    [xml]$definition = $_.Xml
                    if ($definition.Task.RegistrationInfo.Author -eq 'WakeGlass' -and $definition.Task.RegistrationInfo.Description -like 'WakeGlass/v1|*') { $_.Name }
                })
                foreach ($taskName in $ownedNames) { $folder.GetTask($taskName).Enabled = $false }
                if ($ownedNames -contains 'Stage-Worker') {
                    $worker = $folder.GetTask('Stage-Worker')
                    if ($worker.GetInstances(0).Count -gt 0) { $worker.Stop(0) }
                }
                foreach ($taskName in $ownedNames) { $folder.DeleteTask($taskName, 0) }
                if ($folder.GetTasks(1).Count -eq 0 -and $folder.GetFolders(0).Count -eq 0) { $service.GetFolder('\').DeleteFolder($folderPath, 0) }
            }
        } finally {
            if ($locked) { $scheduleLock.ReleaseMutex() }
            $scheduleLock.Dispose()
        }
        if ($canRemoveInstall) {
            $expectedTarget = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\WakeGlass'))
            if ($installTarget -ne $expectedTarget -or !(Test-Path -LiteralPath (Join-Path $installTarget 'WakeGlass.exe'))) { throw 'Installation path safety check failed.' }
            if ((Get-Item -LiteralPath $installTarget).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing to remove a linked installation directory.' }
            Remove-Item -LiteralPath $installTarget -Recurse -Force
            $menuPath = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('Programs')) 'WakeGlass'))
            foreach ($shortcut in @('WakeGlass.lnk','Uninstall WakeGlass.lnk')) { $link = Join-Path $menuPath $shortcut; if (Test-Path -LiteralPath $link) { Remove-Item -LiteralPath $link -Force } }
            if ((Test-Path -LiteralPath $menuPath) -and !(Get-ChildItem -LiteralPath $menuPath)) { Remove-Item -LiteralPath $menuPath }
        }
    }
    [System.Windows.MessageBox]::Show('Completed. Local settings and diagnostic history are retained. A portable folder can now be removed manually after tasks are cleared.', 'WakeGlass', 'OK', 'Information') | Out-Null
} catch { [System.Windows.MessageBox]::Show($_.Exception.Message, 'WakeGlass uninstall', 'OK', 'Error') | Out-Null; exit 1 }
