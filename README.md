# ProcessDrive

Navigate the Windows process tree as a PowerShell drive — a CLI alternative to Process Explorer.

## Quick Start

```powershell
New-ProcDrive
cd Proc:\
dir
```

## Using with AI Agents (PowerShell.MCP)

ProcessDrive works with [PowerShell.MCP](https://github.com/yotsuda/PowerShell.MCP#readme), enabling AI agents (Claude, GitHub Copilot, etc.) to explore processes through natural conversation:

- "What child processes does chrome have?" → `dir Proc:\chrome_21236`
- "Show me chrome's network connections" → `dir Proc:\chrome_21236\Network`
- "Which services are hosted by svchost PID 1804?" → `dir Proc:\svchost_1804\Services`
- "Find all processes that loaded gdi32.dll" → `dir Proc:\ | % { dir "Proc:\$($_.PSChildName)\Modules" -EA Ignore } | ? Name -like '*gdi*'`

The typed DTO output (`ProcessInfo`, `ModuleInfo`, `ThreadInfo`, etc.) makes it easy for AI agents to parse and reason about process data.

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

Tab completion works throughout the drive:

```
cd chr<Tab>        →  cd chrome_21236       # Process name completion
cd Mod<Tab>        →  cd Modules            # Virtual folder completion
Get-Item ntd<Tab>  →  Get-Item ntdll.dll    # Module name completion (inside Modules)
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
| Memory | WorkingSetMB, PeakWorkingSetMB, PrivateBytesMB, VirtualSizeMB, PagedMemoryMB, PageFaults |
| CPU | UserCPU, KernelCPU, TotalCPU |
| Process | SessionId, BasePriority, PriorityClass |
| I/O | IOReadOps, IOWriteOps, IOReadBytesMB, IOWriteBytesMB |

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
dir Proc:\ | Sort-Object MemMB -Descending | Select-Object -First 10

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

## Process Explorer Feature Mapping

**Process tree view**
```powershell
dir Proc:\
dir Proc:\ -Recurse
```

**Process properties**
```powershell
Get-Item Proc:\chrome_21236 | Format-List *
```

**Loaded DLLs / Threads / Services / Network**
```powershell
dir Proc:\chrome_21236\Modules
dir Proc:\chrome_21236\Threads
dir Proc:\svchost_1804\Services
dir Proc:\chrome_21236\Network
```

**Kill process / Kill process tree**
```powershell
Remove-Item Proc:\notepad_1234
Remove-Item Proc:\chrome_21236 -Recurse
```

**Find process by name**
```powershell
dir Proc:\ -Include note* -Recurse
```

**Find DLL across all processes**
```powershell
dir Proc:\ | % { dir "Proc:\$($_.PSChildName)\Modules" -EA Ignore } | ? Name -like '*gdi*'
```

**Sort by memory / CPU**
```powershell
dir Proc:\ | Sort-Object MemMB -Descending
dir Proc:\ | Sort-Object CPU -Descending
```

**Refresh cache**
```powershell
dir Proc:\ -Force
```

**Export to file**
```powershell
dir Proc:\ -Recurse | Export-Csv processes.csv
```

## Requirements

- Windows
- PowerShell 7.4+

## Architecture

ProcessDrive is a `NavigationCmdletProvider` that exposes the Windows process tree as a hierarchical drive.

- **Process tree** is built from WMI `Win32_Process` (cached for 10 seconds, `dir -Force` to refresh)
- **Modules / Services** are cached per process (10 seconds TTL, `dir -Force` to refresh)
- **Threads / Network** are fetched live on each request
- **`dir`** uses WMI cache only (fast), **`Get-Item`** adds live stats from `System.Diagnostics.Process`
- **Network connections** use P/Invoke to `GetExtendedTcpTable` / `GetExtendedUdpTable` (iphlpapi.dll)
