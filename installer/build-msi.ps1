<#
.SYNOPSIS
    TouchKeyboard.msi を一から作る。

.DESCRIPTION
    次を順番に行う。

      1. dotnet build でビルドする（-SkipBuild で省略可）
      2. 署名用証明書を作る／再利用する（tools/install-dev.ps1 と同じ Subject）
      3. TouchKeyboard.exe に署名する
      4. 証明書の公開鍵だけを LunaProject.cer として書き出す
         （MSI に同梱し、インストール時にこの PC の「信頼されたルート証明機関」と
         「信頼された発行元」へ登録するため。TouchKeyboard.wxs 側のカスタムアクションが行う）
      5. generate-files.ps1 でビルド出力から Files.wxs を作る
      6. wix build で TouchKeyboard.msi を作る

    元データは `dotnet publish` の出力ではなく `dotnet build` の出力
    （src/TouchKeyboard/bin/<Configuration>/.../win-arm64）を使う。
    `dotnet publish` の出力では TouchKeyboard.pri / *.xbf
    （コンパイル済み XAML）が抜け落ち、`Microsoft.UI.Xaml.dll` 内部の
    `Application.Start()` が確率的にクラッシュする不具合を実機で確認したため
    （利用者の指示：「いったんアンインストールした上で、ビルドしたやつを
    直接コピーしてください」→ 直接コピーだと起動した、という報告から特定）。

    管理者権限は不要（証明書は CurrentUser ストアに作るだけで、
    信頼ストアへの登録は MSI の実行時、インストールする側の権限で行う）。

.NOTES
    事前に一度だけ:
      dotnet tool install --global wix
      wix extension add WixToolset.Util.wixext
#>

param(
    [string] $Configuration = "Release",
    [switch] $SkipBuild
)

$ErrorActionPreference = "Stop"

$subject   = "CN=Luna Project"
$friendly  = "Luna Project Code Signing"

$installerDir = $PSScriptRoot
$projectRoot  = Split-Path -Parent $installerDir
$csproj       = Join-Path $projectRoot "src\TouchKeyboard\TouchKeyboard.csproj"
$buildDir     = Join-Path $projectRoot "src\TouchKeyboard\bin\$Configuration\net10.0-windows10.0.19041.0\win-arm64"
$certPath     = Join-Path $installerDir "LunaProject.cer"
$filesWxs     = Join-Path $installerDir "Files.wxs"
$productWxs   = Join-Path $installerDir "TouchKeyboard.wxs"
$msiOut       = Join-Path $installerDir "TouchKeyboard.msi"

# TouchKeyboard.wxs 内の相対パスは、wix build を実行した時点の
# カレントディレクトリ基準で解決される。README の手順どおりリポジトリの
# ルートから呼ばれても壊れないよう固定する。
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

if (-not $SkipBuild) {
    # 動いていると上書きできない
    Get-Process TouchKeyboard -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "動作中のアプリを終了します (pid=$($_.Id))" -ForegroundColor Cyan
        try { $_.Kill(); $_.WaitForExit(5000) } catch { }
    }

    Write-Host "ビルドしています ($Configuration)..." -ForegroundColor Cyan
    dotnet build $csproj -c $Configuration
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} elseif (-not (Test-Path $buildDir)) {
    Write-Host "ビルド出力がありません: $buildDir" -ForegroundColor Red
    Write-Host "-SkipBuild を外すか、先に dotnet build を実行してください。" -ForegroundColor Yellow
    exit 1
}

$cert = Get-SigningCertificate

$exe = Join-Path $buildDir "TouchKeyboard.exe"
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
    -PublishDir $buildDir `
    -OutFile $filesWxs `
    -ExcludeAtRoot @("TouchKeyboard.exe") `
    -ExcludeDirsAtRoot @("native", "publish") `
    -ExcludeExtensions @(".pdb")

Write-Host "MSI をビルドしています..." -ForegroundColor Cyan
if (Test-Path $msiOut) { Remove-Item $msiOut -Force }
& wix build $productWxs $filesWxs -arch arm64 -ext WixToolset.Util.wixext -d "BuildDir=$buildDir" -o $msiOut
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
Write-Host "できました: $msiOut" -ForegroundColor Green
