# taskbar-monitor
DeskBand with monitoring charts (CPU and memory) for Windows

This app shows some cool graphs displaying CPU and memory usage on the taskbar (as a Desk Band).


![Example 1](media/demo.png)

This project was made using the  [CS DeskBand](https://github.com/dsafa/CSDeskBand) library.

## Install instructions
                
To install the taskbar-monitor, first make sure you have <a href="https://dotnet.microsoft.com/download/dotnet-framework/net472" target="_blank" rel="noreferrer">.NET Framework 4.7.2 runtime</a> installed on your computer. If you use Windows 10 1803 April 2018 or later, it is already installed on your computer. 

Then, download the **TaskbarMonitorInstaller.exe** installer and run it.</p>

<p>It needs administrator rights to run, as it installs on your PROGRAM FILES (x86) folder.
The reason for this is that this tool registers itself for all users, so the dll files should be accessible for
    all users on the computer.</p>

## Uninstall
To uninstall it, just use the uninstaller from the "Add or Remove Programs" list.

## Manual DLL replacement (hotfix)

When applying a rebuilt `TaskbarMonitor.dll` without re-running the installer, copy both the new `TaskbarMonitor.dll` and its matching `Newtonsoft.Json.dll` together. The app has no `bindingRedirect`, so a Newtonsoft version mismatch causes `FileLoadException` at startup.

1. Exit taskbar-monitor from the tray icon (or kill `TaskbarMonitorWindows11.exe`).
2. In an **Administrator PowerShell**:

   ```powershell
   $src = 'C:\path\to\repo\TaskbarMonitor\bin\Release'
   $dst = 'C:\Program Files\TaskbarMonitor'
   Copy-Item "$dst\TaskbarMonitor.dll"   "$dst\TaskbarMonitor.dll.bak"   -Force
   Copy-Item "$dst\Newtonsoft.Json.dll"  "$dst\Newtonsoft.Json.dll.bak"  -Force
   Copy-Item "$src\TaskbarMonitor.dll"   "$dst\TaskbarMonitor.dll"       -Force
   Copy-Item "$src\Newtonsoft.Json.dll"  "$dst\Newtonsoft.Json.dll"      -Force
   ```

3. Restart taskbar-monitor. If it fails to start, restore the `.bak` files.