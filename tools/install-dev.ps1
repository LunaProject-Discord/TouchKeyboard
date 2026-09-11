<#
.SYNOPSIS
    ビルド結果に署名して Program Files へ配置する。開発中の反復用。

.DESCRIPTION
    uiAccess="true" のアプリは、次の両方を満たさないと起動できない。

      - 実行ファイルが Authenticode 署名済みであること
      - 信頼された場所（Program Files 配下）から起動されること

    このため出力ディレクトリから直接実行できない。ビルドのたびにこれを実行する。

    証明書は自己署名で作り、この PC の「信頼されたルート証明機関」と
    「信頼された発行元」に登録する。すでにあれば作り直さない。

.NOTES
    管理者権限が必要（証明書の登録と Program Files への書き込み）。
    最終版では、この処理をインストーラーに移す。
#>

param(
    [string] $Configuration = "Debug",
    [switch] $NoLaunch
)

$ErrorActionPreference = "Stop"

$subject     = "CN=Luna Project"
$friendly    = "Luna Project Code Signing"
$installDir  = "C:\Program Files\TouchKeyboard"
$projectRoot = Split-Path -Parent $PSScriptRoot
$buildDir    = Join-Path $projectRoot "src\TouchKeyboard\bin\$Configuration\net10.0-windows10.0.19041.0\win-arm64"

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)

    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Write-Host ""
        Write-Host "管理者権限が必要です。" -ForegroundColor Red
        Write-Host "証明書の登録と Program Files への書き込みを行うためです。" -ForegroundColor Red
        Write-Host ""
        Write-Host "管理者として PowerShell を開き、もう一度実行してください:" -ForegroundColor Yellow
        Write-Host "  powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`"" -ForegroundColor Yellow
        exit 1
    }
}

function Get-SigningCertificate {
    # 期限内のものを再利用する。毎回作ると証明書ストアが増え続ける。
    $existing = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date) } |
        Select-Object -First 1

    if ($existing) {
        Write-Host "既存の証明書を使います: $($existing.Thumbprint)" -ForegroundColor Green
        return $existing
    }

    Write-Host "署名用の証明書を作ります..." -ForegroundColor Cyan

    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $subject `
        -FriendlyName $friendly `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyUsage DigitalSignature `
        -KeyExportPolicy Exportable `
        -NotAfter (Get-Date).AddYears(5)

    # uiAccess の判定は「信頼された発行元」を見る。ルートにも入れて連鎖を成立させる。
    foreach ($store in "Root", "TrustedPublisher") {
        $target = New-Object System.Security.Cryptography.X509Certificates.X509Store($store, "LocalMachine")
        $target.Open("ReadWrite")
        $target.Add($cert)
        $target.Close()
        Write-Host "  LocalMachine\$store に登録しました" -ForegroundColor Green
    }

    return $cert
}

Assert-Administrator

if (-not (Test-Path $buildDir)) {
    Write-Host "ビルド結果がありません: $buildDir" -ForegroundColor Red
    Write-Host "先に dotnet build を実行してください。" -ForegroundColor Yellow
    exit 1
}

$cert = Get-SigningCertificate

# 動いていると上書きできない
Get-Process TouchKeyboard -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "動作中のアプリを終了します (pid=$($_.Id))" -ForegroundColor Cyan
    try { $_.Kill(); $_.WaitForExit(5000) } catch { }
}
Start-Sleep -Milliseconds 500

Write-Host "配置しています: $installDir" -ForegroundColor Cyan
if (-not (Test-Path $installDir)) { New-Item -ItemType Directory -Force $installDir | Out-Null }
Copy-Item "$buildDir\*" $installDir -Recurse -Force

# 署名は配置後に行う。コピーで署名が失われることはないが、
# 署名済みのものを確実に置くため、置いた実体に対して署名する。
$exe = Join-Path $installDir "TouchKeyboard.exe"
Write-Host "署名しています: $exe" -ForegroundColor Cyan

$result = Set-AuthenticodeSignature -FilePath $exe -Certificate $cert -HashAlgorithm SHA256

if ($result.Status -ne "Valid") {
    Write-Host "署名に失敗しました: $($result.Status) $($result.StatusMessage)" -ForegroundColor Red
    exit 1
}

Write-Host "署名しました: $($result.SignerCertificate.Thumbprint)" -ForegroundColor Green

if (-not $NoLaunch) {
    Write-Host "起動します..." -ForegroundColor Cyan
    Start-Process $exe
    Start-Sleep -Seconds 5

    $running = @(Get-Process TouchKeyboard -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        Write-Host "起動しました (pid=$($running[0].Id))" -ForegroundColor Green
    } else {
        Write-Host ""
        Write-Host "起動できませんでした。" -ForegroundColor Red
        Write-Host "uiAccess の条件を満たしていない可能性があります:" -ForegroundColor Yellow
        Write-Host "  - 署名が信頼されているか（証明書ストアの登録）" -ForegroundColor Yellow
        Write-Host "  - Program Files 配下から起動しているか" -ForegroundColor Yellow
        exit 1
    }
}
