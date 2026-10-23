[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [switch]$FrameworkDependent,

    [switch]$SingleFile
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'src\YandexMusicDiscordRpc\YandexMusicDiscordRpc.csproj'
$output = Join-Path $root "dist\$Runtime"

if (-not (Test-Path $project)) {
    throw "Не найден проект: $project"
}

if (Test-Path $output) {
    Remove-Item -Path $output -Recurse -Force
}

$arguments = @(
    'publish', $project,
    '-c', 'Release',
    '-r', $Runtime,
    '-o', $output,
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-p:GenerateDocumentationFile=false',
    '-p:PublishTrimmed=false'
)

if ($FrameworkDependent) {
    $arguments += @('--self-contained', 'false')
} else {
    $arguments += @('--self-contained', 'true')
}

if ($SingleFile) {
    $arguments += @(
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true'
    )
} else {
    $arguments += @('-p:PublishSingleFile=false')
}

Write-Host "Публикация $Runtime..." -ForegroundColor Cyan
& dotnet @arguments

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish завершился с кодом $LASTEXITCODE"
}

Copy-Item -Path (Join-Path $root 'README.md') -Destination $output -Force
Copy-Item -Path (Join-Path $root 'LICENSE') -Destination $output -Force
Copy-Item -Path (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $output -Force

$executable = Join-Path $output 'YandexMusicRPC.exe'
if (-not (Test-Path $executable)) {
    throw "Не найден результат публикации: $executable"
}

$size = [math]::Round((Get-Item $executable).Length / 1MB, 1)
$total = [math]::Round(((Get-ChildItem -Path $output -Recurse -File | Measure-Object -Property Length -Sum).Sum) / 1MB, 1)
$fileCount = (Get-ChildItem -Path $output -Recurse -File | Measure-Object).Count

Write-Host ''
Write-Host 'Готово.' -ForegroundColor Green
Write-Host "Каталог: $output"
Write-Host "YandexMusicRPC.exe: $size МБ"
Write-Host "Всего файлов: $fileCount, объём: $total МБ"

if ($SingleFile) {
    Write-Warning 'Single-file сборка потребляет больше памяти. Для постоянной работы используйте публикацию папкой (без -SingleFile).'
}
