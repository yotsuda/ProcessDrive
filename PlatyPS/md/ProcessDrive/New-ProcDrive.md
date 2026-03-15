---
document type: cmdlet
external help file: ProcessDrive.dll-Help.xml
HelpUri: ''
Locale: en-US
Module Name: ProcessDrive
ms.date: 03/15/2026
PlatyPS schema version: 2024-05-01
title: New-ProcDrive
---

# New-ProcDrive

## SYNOPSIS

Creates a PSDrive for navigating the Windows process tree.

## SYNTAX

```
New-ProcDrive [[-Name] <string>] [<CommonParameters>]
```

## ALIASES

## DESCRIPTION

Mounts the Windows process tree as a PowerShell drive. A CLI alternative to Process Explorer
(procexp.exe). Navigate parent-child process hierarchy with cd/dir, and browse each process's
Modules, Threads, Services, and Network connections as virtual folders.

## EXAMPLES

### Example 1: Mount with default name

```powershell
New-ProcDrive
cd Proc:\
dir
```

Creates the Proc:\ drive and lists root processes.

### Example 2: Mount with custom name

```powershell
New-ProcDrive MyProc
cd MyProc:\
```

Creates the drive as MyProc:\.

### Example 3: Get detailed process properties

```powershell
Get-Item Proc:\devenv_24032 | Format-List *
```

Displays CPU, memory, I/O statistics, file version, and other detailed properties.

### Example 4: List loaded DLLs

```powershell
dir Proc:\chrome_21236\Modules
```

Shows all DLLs loaded by the chrome process.

### Example 5: Search for a process

```powershell
dir Proc:\ -Include note* -Recurse
```

Searches the entire process tree for processes matching "note*".

## PARAMETERS

### -Name

Drive name. Defaults to "Proc" if omitted.

```yaml
Type: System.String
DefaultValue: Proc
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: (All)
  Position: 0
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### CommonParameters

This cmdlet supports the common parameters: -Debug, -ErrorAction, -ErrorVariable,
-InformationAction, -InformationVariable, -OutBuffer, -OutVariable, -PipelineVariable,
-ProgressAction, -Verbose, -WarningAction, and -WarningVariable. For more information, see
[about_CommonParameters](https://go.microsoft.com/fwlink/?LinkID=113216).

## INPUTS

### None

## OUTPUTS

### System.Management.Automation.PSDriveInfo

The created PSDrive object.

## NOTES

The process tree is built from WMI and cached for 10 seconds.
Use `dir -Force` to discard the cache and fetch fresh data.
Modules and Services are also cached per process.

## RELATED LINKS

- [about_ProcessDrive]()
