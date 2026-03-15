@{
    RootModule        = 'ProcessDrive.dll'
    ModuleVersion     = '0.1.0'
    GUID              = 'a3f7c8e1-9d4b-4e6a-b5c2-1f8d3e7a9b0c'
    Author            = 'Yoshifumi Tsuda'
    CompanyName       = 'Yoshifumi Tsuda'
    Copyright         = '(c) 2026 Yoshifumi Tsuda. All rights reserved.'
    Description       = 'Navigate Windows process tree as a PowerShell drive (Windows only). A CLI alternative to Process Explorer. Provides cd/dir navigation through parent-child process hierarchy, virtual folders for Modules, Threads, Services, and Network connections, detailed process properties via Get-Item, and process kill via Remove-Item.'
    PowerShellVersion = '7.0'
    CompatiblePSEditions = @('Core')
    FormatsToProcess  = @('ProcessDrive.format.ps1xml')
    CmdletsToExport   = @('New-ProcDrive')
    FunctionsToExport = @()
    VariablesToExport = @()
    AliasesToExport   = @()

    PrivateData = @{
        PSData = @{
            Tags         = @('Process', 'Tree', 'Provider', 'Drive', 'ProcessExplorer', 'Windows', 'procexp', 'Navigation')
            ProjectUri   = 'https://github.com/yotsuda/ProcessDrive'
            LicenseUri   = 'https://github.com/yotsuda/ProcessDrive/blob/master/LICENSE'
            ReleaseNotes = 'Initial release. Process tree navigation, virtual folders (Modules, Threads, Services, Network), detailed Get-Item properties, Remove-Item for process kill.'
        }
    }
}
