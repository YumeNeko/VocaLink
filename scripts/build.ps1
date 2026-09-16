param(
    [switch]$NoShortcut
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\Desktop\VocaLink.Desktop.csproj"
$output = Join-Path $root "bin"
$temporaryOutput = Join-Path $root ".publish-temp"
$backupOutput = Join-Path $root ".publish-backup"
$config = Join-Path $root "NuGet.Config"

$resolvedRoot = [System.IO.Path]::GetFullPath($root).TrimEnd('\')
foreach ($path in @($output, $temporaryOutput, $backupOutput)) {
    $resolvedPath = [System.IO.Path]::GetFullPath($path).TrimEnd('\')
    if (-not $resolvedPath.StartsWith($resolvedRoot + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Publish directory is outside the project root. Operation stopped."
    }
}

foreach ($path in @($temporaryOutput, $backupOutput)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

dotnet restore $project -r win-x64 --configfile $config
if ($LASTEXITCODE -ne 0) { throw "Dependency restore failed." }

dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -o $temporaryOutput `
    --no-restore `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

Get-ChildItem -LiteralPath $temporaryOutput -File -Recurse |
    Where-Object { $_.Extension -in '.pdb', '.xml' } |
    Remove-Item -Force

if (Test-Path -LiteralPath $output) {
    Move-Item -LiteralPath $output -Destination $backupOutput
}
try {
    Move-Item -LiteralPath $temporaryOutput -Destination $output
    if (Test-Path -LiteralPath $backupOutput) {
        Remove-Item -LiteralPath $backupOutput -Recurse -Force
    }
}
catch {
    if (-not (Test-Path -LiteralPath $output) -and (Test-Path -LiteralPath $backupOutput)) {
        Move-Item -LiteralPath $backupOutput -Destination $output
    }
    throw
}

if (-not $NoShortcut) {
    $shortcutPath = Join-Path $root "VocaLink.lnk"
    $targetPath = Join-Path $output "VocaLink.exe"
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $targetPath
    $shortcut.WorkingDirectory = $root
    $shortcut.IconLocation = "$targetPath,0"
    $shortcut.Description = "Start VocaLink"
    $shortcut.Save()
}

Write-Host "Published: $output"
if (-not $NoShortcut) { Write-Host "Shortcut: $(Join-Path $root 'VocaLink.lnk')" }
