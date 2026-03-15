#Requires -Modules Microsoft.PowerShell.PlatyPS
<#
.SYNOPSIS
    Build MAML XML help from PlatyPS markdown and deploy to module folder.
.DESCRIPTION
    Generates XML help files from English and Japanese markdown sources
    and deploys them to module/en-US/ and module/ja-JP/.
#>
$ErrorActionPreference = 'Stop'

$xmlPath = "$PSScriptRoot\xml"
$moduleDir = Join-Path $PSScriptRoot '..\module'

# Clean previous build output
if (Test-Path $xmlPath) { Remove-Item "$xmlPath\*" -Recurse -Force }

foreach ($locale in @(@{Lang='en-US'; Md="$PSScriptRoot\en-US"}, @{Lang='ja-JP'; Md="$PSScriptRoot\ja-JP"})) {
    if (!(Test-Path $locale.Md)) { continue }

    Write-Host "Building $($locale.Lang) help..." -ForegroundColor Cyan
    $outDir = Join-Path $xmlPath $locale.Lang

    # Build XML from markdown
    Measure-PlatyPSMarkdown -Path "$($locale.Md)\ProcessDrive\*.md" |
        Where-Object Filetype -match 'CommandHelp' |
        Import-MarkdownCommandHelp -Path { $_.FilePath } |
        Export-MamlCommandHelp -OutputFolder $outDir -Force

    # Workaround: PlatyPS v1 emits &#x80; (invalid XML char)
    foreach ($xml in Get-ChildItem $outDir -Filter '*.xml' -Recurse) {
        $content = [System.IO.File]::ReadAllText($xml.FullName)
        $cleaned = $content -replace '\s*<maml:para>&#x80;</maml:para>\r?\n', ''
        if ($cleaned -ne $content) {
            [System.IO.File]::WriteAllText($xml.FullName, $cleaned)
            Write-Host "  Cleaned invalid XML chars: $($xml.Name)" -ForegroundColor Yellow
        }
    }

    # Deploy to module locale folder
    $targetDir = Join-Path $moduleDir $locale.Lang
    if (!(Test-Path $targetDir)) { New-Item $targetDir -ItemType Directory -Force | Out-Null }

    $xmlSourceDir = Join-Path $outDir 'ProcessDrive'
    if (Test-Path $xmlSourceDir) {
        Copy-Item "$xmlSourceDir\*" $targetDir -Force
    }

    # Deploy about_ topics
    $aboutSrc = Join-Path $locale.Md 'about_ProcessDrive.md'
    if (Test-Path $aboutSrc) {
        # Convert about_ markdown to plain text help
        $aboutContent = Get-Content $aboutSrc -Raw
        $aboutDest = Join-Path $targetDir 'about_ProcessDrive.help.txt'
        Set-Content $aboutDest -Value $aboutContent
    }

    Get-ChildItem $targetDir | Format-Table Name, Length -AutoSize
}

Write-Host 'Help files deployed.' -ForegroundColor Green
