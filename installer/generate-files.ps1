<#
.SYNOPSIS
    publish 出力ディレクトリを丸ごと WiX のディレクトリ・コンポーネント定義に変換する。

.DESCRIPTION
    自己完結配置は 700 ファイル超（衛星リソース DLL を含む）になるため、
    heat.exe 相当の処理を自前で行う。INSTALLFOLDER を起点に実際のフォルダ構成を
    そのまま再現し、ファイル 1 つにつき Component を 1 つ作る。

    Id はファイル名の衝突・使用不可文字・72 文字制限を避けるため、
    読みやすさより機械的な一意性を優先し連番にする。
#>

param(
    [Parameter(Mandatory)] [string] $PublishDir,
    [Parameter(Mandatory)] [string] $OutFile,
    # ルート直下だけで除外するファイル名。固定 Id で個別に宣言したいファイル
    # （TouchKeyboard.exe 本体など）をここで除き、二重定義を避ける。
    [string[]] $ExcludeAtRoot = @()
)

$ErrorActionPreference = "Stop"

$publishDir = (Resolve-Path $PublishDir).Path.TrimEnd('\')

$script:dirCounter = 0
$script:fileCounter = 0
$componentIds = New-Object System.Collections.Generic.List[string]

function Write-DirectoryEntries {
    param([string] $AbsPath, [System.Text.StringBuilder] $Sb, [int] $IndentLevel, [bool] $IsRoot)

    $indent = "  " * $IndentLevel
    $entries = Get-ChildItem -LiteralPath $AbsPath -Force | Sort-Object { -not $_.PSIsContainer }, Name

    foreach ($entry in $entries) {
        if ($IsRoot -and -not $entry.PSIsContainer -and $ExcludeAtRoot -contains $entry.Name) {
            continue
        }
        if ($entry.PSIsContainer) {
            $script:dirCounter++
            $dirId = "Dir{0:D4}" -f $script:dirCounter
            [void]$Sb.AppendLine("$indent<Directory Id=`"$dirId`" Name=`"$($entry.Name)`">")
            Write-DirectoryEntries -AbsPath $entry.FullName -Sb $Sb -IndentLevel ($IndentLevel + 1) -IsRoot $false
            [void]$Sb.AppendLine("$indent</Directory>")
        } else {
            $script:fileCounter++
            $compId = "Cmp{0:D4}" -f $script:fileCounter
            $fileId = "Fil{0:D4}" -f $script:fileCounter
            $componentIds.Add($compId) | Out-Null

            [void]$Sb.AppendLine("$indent<Component Id=`"$compId`" Guid=`"*`">")
            [void]$Sb.AppendLine("$indent  <File Id=`"$fileId`" Name=`"$($entry.Name)`" Source=`"$($entry.FullName)`" KeyPath=`"yes`" />")
            [void]$Sb.AppendLine("$indent</Component>")
        }
    }
}

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
[void]$sb.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
[void]$sb.AppendLine('  <Fragment>')
[void]$sb.AppendLine('    <DirectoryRef Id="INSTALLFOLDER">')
Write-DirectoryEntries -AbsPath $publishDir -Sb $sb -IndentLevel 3 -IsRoot $true
[void]$sb.AppendLine('    </DirectoryRef>')
[void]$sb.AppendLine('  </Fragment>')
[void]$sb.AppendLine('  <Fragment>')
[void]$sb.AppendLine('    <ComponentGroup Id="PublishComponents">')
foreach ($id in $componentIds) {
    [void]$sb.AppendLine("      <ComponentRef Id=`"$id`" />")
}
[void]$sb.AppendLine('    </ComponentGroup>')
[void]$sb.AppendLine('  </Fragment>')
[void]$sb.AppendLine('</Wix>')

$utf8bom = New-Object System.Text.UTF8Encoding($true)
[System.IO.File]::WriteAllText($OutFile, $sb.ToString(), $utf8bom)

Write-Host "生成しました: $OutFile（Component 数: $($componentIds.Count)）" -ForegroundColor Green
