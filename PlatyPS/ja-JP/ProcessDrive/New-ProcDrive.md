---
document type: cmdlet
external help file: ProcessDrive.dll-Help.xml
HelpUri: ''
Locale: ja-JP
Module Name: ProcessDrive
ms.date: 03/15/2026
PlatyPS schema version: 2024-05-01
title: New-ProcDrive
---

# New-ProcDrive

## SYNOPSIS

Windows プロセスツリーをナビゲートする PSDrive を作成します。

## SYNTAX

### __AllParameterSets

```
New-ProcDrive [[-Name] <string>] [<CommonParameters>]
```

## ALIASES

## DESCRIPTION

Windows プロセスツリーを PowerShell ドライブとしてマウントします。
Process Explorer (procexp.exe) の CLI 代替として、cd/dir でプロセスの親子階層を
ナビゲートし、各プロセスの Modules、Threads、Services、Network を仮想フォルダとして
閲覧できます。

## EXAMPLES

### Example 1: デフォルト名でマウント

```powershell
New-ProcDrive
cd Proc:\
dir
```

Proc:\ ドライブを作成し、プロセスツリーを表示します。

### Example 2: カスタム名でマウント

```powershell
New-ProcDrive MyProc
cd MyProc:\
```

MyProc:\ としてドライブを作成します。

### Example 3: プロセスの詳細情報を取得

```powershell
Get-Item Proc:\devenv_24032 | Format-List *
```

CPU、メモリ、I/O 統計などの詳細プロパティを表示します。

## PARAMETERS

### -Name

ドライブ名。省略時は "Proc" が使用されます。

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

作成された PSDrive オブジェクト。

## NOTES

プロセスツリーは WMI から取得し、10 秒間キャッシュされます。
`dir -Force` でキャッシュを破棄して最新のデータを取得できます。
Modules と Services もプロセスごとにキャッシュされます。

## RELATED LINKS

- [about_ProcessDrive]()
