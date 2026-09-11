<#
.SYNOPSIS
    TouchKeyboard.msi を一から作る。

.DESCRIPTION
    次を順番に行う。

      1. dotnet publish で自己完結の配置一式を作る（-SkipPublish で省略可）
      2. 署名用証明書を作る／再利用する（tools/install-dev.ps1 と同じ Subject）
      3. TouchKeyboard.exe に署名する
      4. 証明書の公開鍵だけを LunaProject.cer として書き出す
         （MSI に同梱し、インストール時にこの PC の「信頼されたルート証明機関」と
         「信頼された発行元」へ登録するため。TouchKeyboard.wxs 側のカスタムアクションが行う）
      5. generate-files.ps1 で publish 出力から Files.wxs を作る
      6. wix build で TouchKeyboard.msi を作る

    管理者権限は不要（証明書は CurrentUser ストアに作るだけで、
    信頼ストアへの登録は MSI の実行時、インストールする側の権限で行う）。

.NOTES
    事前に一度だけ:
      dotnet tool install --global wix
      wix extension add WixToolset.Util.wixext
#>

param(
    [string] $Configuration = "Release",
    [switch] $SkipPublish
)

$ErrorActionPreference = "Stop"

$subject   = "CN=Luna Project"
$friendly  = "Luna Project Code Signing"

$installerDir = $PSScriptRoot
$projectRoot  = Split-Path -Parent $installerDir
$csproj       = Join-Path $projectRoot "src\TouchKeyboard\TouchKeyboard.csproj"
$publishDir   = Join-Path $installerDir "publish"
$certPath     = Join-Path $installerDir "LunaProject.cer"
$filesWxs     = Join-Path $installerDir "Files.wxs"
$productWxs   = Join-Path $installerDir "TouchKeyboard.wxs"
$msiOut       = Join-Path $installerDir "TouchKeyboard.msi"

# TouchKeyboard.wxs 内の Source="publish\..." 等の相対パスは、
# wix build を実行した時点のカレントディレクトリ基準で解決される。
# README の手順どおりリポジトリのルートから呼ばれても壊れないよう固定する。
Set-Location $installerDir

function Get-SigningCertificate {
    $existing = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date) } |
        Select-Object -First 1

    if ($existing) {
        Write-Host "既存の証明書を使います: $($existing.Thumbprint)" -ForegroundColor Green
        return $existing
    }

    Write-Host "署名用の証明書を作ります..." -ForegroundColor Cyan
    return New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $subject `
        -FriendlyName $friendly `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyUsage DigitalSignature `
        -KeyExportPolicy Exportable `
        -NotAfter (Get-Date).AddYears(5)
}

# dotnet 系のグローバルツールが PATH に無い環境でも動くようにする。
$dotnetToolsPath = Join-Path $env:USERPROFILE ".dotnet\tools"
if (Test-Path $dotnetToolsPath -PathType Container) {
    $env:PATH = "$env:PATH;$dotnetToolsPath"
}

if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    Write-Host "wix コマンドが見つかりません。次を実行してから再試行してください:" -ForegroundColor Red
    Write-Host "  dotnet tool install --global wix" -ForegroundColor Yellow
    Write-Host "  wix extension add WixToolset.Util.wixext" -ForegroundColor Yellow
    exit 1
}

if (-not $SkipPublish) {
    Write-Host "publish しています ($Configuration, win-arm64)..." -ForegroundColor Cyan
    dotnet publish $csproj -c $Configuration -r win-arm64 --self-contained true -o $publishDir
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} elseif (-not (Test-Path $publishDir)) {
    Write-Host "publish 出力がありません: $publishDir" -ForegroundColor Red
    Write-Host "-SkipPublish を外すか、先に dotnet publish を実行してください。" -ForegroundColor Yellow
    exit 1
}

$cert = Get-SigningCertificate

$exe = Join-Path $publishDir "TouchKeyboard.exe"
Write-Host "署名しています: $exe" -ForegroundColor Cyan
$result = Set-AuthenticodeSignature -FilePath $exe -Certificate $cert -HashAlgorithm SHA256
if ($result.Status -ne "Valid") {
    Write-Host "署名に失敗しました: $($result.Status) $($result.StatusMessage)" -ForegroundColor Red
    exit 1
}
Write-Host "署名しました: $($result.SignerCertificate.Thumbprint)" -ForegroundColor Green

Write-Host "証明書の公開鍵を書き出しています: $certPath" -ForegroundColor Cyan
Export-Certificate -Cert $cert -FilePath $certPath -Type CERT | Out-Null

Write-Host "Files.wxs を生成しています..." -ForegroundColor Cyan
if (Test-Path $filesWxs) { Remove-Item $filesWxs -Force }
& (Join-Path $installerDir "generate-files.ps1") `
    -PublishDir $publishDir `
    -OutFile $filesWxs `
    -ExcludeAtRoot @("TouchKeyboard.exe")

Write-Host "MSI をビルドしています..." -ForegroundColor Cyan
if (Test-Path $msiOut) { Remove-Item $msiOut -Force }
& wix build $productWxs $filesWxs -arch arm64 -ext WixToolset.Util.wixext -o $msiOut
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
Write-Host "できました: $msiOut" -ForegroundColor Green
