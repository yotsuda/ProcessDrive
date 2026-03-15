# ProcessDrive

Navigate the Windows process tree as a PowerShell drive — a CLI alternative to Process Explorer.

## Quick Start

```powershell
New-ProcDrive
cd Proc:\
dir
```

## Features

### Process Tree Navigation

```powershell
dir Proc:\                              # Root processes
dir Proc:\ -Recurse                     # Full process tree
cd Proc:\chrome_21236                   # Navigate into a process
cd Modules                              # Navigate into virtual folder
cd ..                                   # Go back to parent
cd \                                    # Go back to root
```

### Process Details (Get-Item)

```powershell
Get-Item Proc:\devenv_24032 | Format-List *
```

Returns detailed properties equivalent to Process Explorer's General/Statistics tabs:

| Category | Properties |
|---|---|
| General | Name, PID, ParentPID, CommandLine, Path, StartTime, RunningTime |
| File | FileVersion, Company, Description, ProductName |
| Memory | WorkingSet, PeakWorkingSet, PrivateBytes, VirtualSize, PagedMemory, PageFaults |
| CPU | UserCPU, KernelCPU, TotalCPU |
| Process | SessionId, BasePriority, PriorityClass, HandleCount, ThreadCount |
| I/O | ReadOps, WriteOps, ReadBytes, WriteBytes |

### Virtual Folders

Each process exposes four virtual sub-folders:

```powershell
dir Proc:\chrome_21236\Modules          # Loaded DLLs (Name, Size, Version, Company, Path)
dir Proc:\chrome_21236\Threads          # Threads (TID, State, WaitReason, Priority, CPU)
dir Proc:\chrome_21236\Services         # Associated Windows services
dir Proc:\chrome_21236\Network          # TCP/UDP connections (Local, Remote, State)
```

### Search Processes

```powershell
# Find a process anywhere in the tree
dir Proc:\ -Include note* -Recurse

# Refresh cache and search
dir Proc:\ -Include note* -Recurse -Force

# Alternative: pipeline filter
dir Proc:\ -Recurse | Where-Object Name -like 'note*'
```

### Kill Processes

```powershell
Remove-Item Proc:\notepad_1234          # Kill a process
Remove-Item Proc:\chrome_21236 -Recurse # Kill entire process tree
```

### Pipeline Power

Things Process Explorer can't do:

```powershell
# Top 10 memory consumers
dir Proc:\ | Sort-Object 'Mem(MB)' -Descending | Select-Object -First 10

# Find which process loaded a specific DLL
dir Proc:\ | ForEach-Object {
    dir "Proc:\$($_.PSChildName)\Modules" -ErrorAction SilentlyContinue
} | Where-Object Name -like '*websocket*'

# All Established TCP connections with process names
dir Proc:\ | ForEach-Object {
    $name = $_.Name
    dir "Proc:\$($_.PSChildName)\Network" -ErrorAction SilentlyContinue |
        Select-Object @{N='Process';E={$name}}, Protocol, LocalAddress, RemoteAddress, State
} | Where-Object State -eq 'Established'

# Export process tree to CSV
dir Proc:\ -Recurse | Export-Csv processes.csv

# Services hosted by svchost processes
Get-Process svchost | ForEach-Object {
    dir "Proc:\svchost_$($_.Id)\Services" -ErrorAction SilentlyContinue
}
```

## Custom Drive Name

```powershell
New-ProcDrive           # Creates Proc:\
New-ProcDrive MyProc    # Creates MyProc:\
```

## Requirements

- Windows
- PowerShell 7.0+

## Architecture

ProcessDrive is a `NavigationCmdletProvider` that exposes the Windows process tree as a hierarchical drive.

- **Process tree** is built from WMI `Win32_Process` (cached for 10 seconds, `dir -Force` to refresh)
- **`dir`** uses WMI cache only (fast), **`Get-Item`** adds live stats from `System.Diagnostics.Process`
- **Network connections** use P/Invoke to `GetExtendedTcpTable` / `GetExtendedUdpTable` (iphlpapi.dll)
- **Services** are queried from WMI `Win32_Service`
