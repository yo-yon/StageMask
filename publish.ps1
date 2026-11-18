# StageMask をダブルクリックで起動できる exe にまとめるスクリプト
#
# 使い方(StageMask フォルダーで実行):
#   .\publish.ps1
#
# 出力先: .\publish\StageMask.exe
# .NET を入れていないPC(登壇用PCなど)でも動くよう、.NET ランタイムごと1つの exe にまとめる(自己完結型)。

$ErrorActionPreference = 'Stop'
Set-Location -Path $PSScriptRoot

# 起動中の StageMask があると上書きできないため、先に確認する
if (Get-Process -Name 'StageMask' -ErrorAction SilentlyContinue) {
    Write-Host 'StageMask が起動中です。終了してから、もう一度実行してください。' -ForegroundColor Yellow
    exit 1
}

Write-Host 'テストを実行しています...' -ForegroundColor Cyan
dotnet test tests/StageMask.Core.Tests --configuration Release
if ($LASTEXITCODE -ne 0) {
    Write-Host 'テストが失敗したため、exe を作成しません。' -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host 'exe を作成しています...' -ForegroundColor Cyan
dotnet publish src/StageMask.App/StageMask.App.csproj `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=embedded `
    --output publish
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$exe = Join-Path $PSScriptRoot 'publish\StageMask.exe'
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ''
Write-Host "完成しました: $exe ($size MB)" -ForegroundColor Green
Write-Host 'この exe をダブルクリックすると起動します。設定は %APPDATA%\StageMask に保存されるため、exe を移動しても引き継がれます。'
