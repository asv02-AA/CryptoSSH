param([switch]$SkipVendor, [switch]$SkipInstaller)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $projectRoot
$sdk = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet -ErrorAction Stop).Source }
# Vendor middleware is installed separately by the user. SkipVendor is retained for older build commands.
$version = ([xml](Get-Content native\CryptoSSH\CryptoSSH.csproj -Raw)).Project.PropertyGroup.Version
$publishRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot "release\app-$version"))
& $sdk publish native\CryptoSSH\CryptoSSH.csproj -c Release -r win-x64 --self-contained true -o $publishRoot -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'Native publish failed' }
if (Test-Path -LiteralPath (Join-Path $publishRoot 'vendor\rtDrivers.exe')) { throw 'Rutoken toolkit installer must not be bundled' }
$forbidden = @('rtpkcs11ecp.dll','jcPKCS11-2.dll','ssh.exe','ssh-keygen.exe','pkcs11-tool.exe','conpty.dll','Microsoft.Terminal.Control.dll','Microsoft.Terminal.Wpf.dll','Wpf.Ui.Terminal.dll','vc_redist.x64.exe')
if (Get-ChildItem -LiteralPath $publishRoot -File -Recurse | Where-Object Name -in $forbidden) { throw 'Legacy terminal or external SSH component found in native-engine publication' }
if (!$SkipInstaller) {
    # NSIS LicenseData detects UTF-16 LE via BOM; UTF-8 without a BOM is read as ANSI.
    $licenseText = [IO.File]::ReadAllText((Join-Path $projectRoot 'INSTALL-LICENSE.txt'))
    [IO.File]::WriteAllText((Join-Path $projectRoot 'installer\license-unicode.txt'), $licenseText, [Text.Encoding]::Unicode)
    $uninstallLines = [System.Collections.Generic.List[string]]::new()
    foreach ($installedFile in (Get-ChildItem -LiteralPath $publishRoot -File -Recurse | Where-Object Extension -ne '.pdb')) {
        $relative = [IO.Path]::GetRelativePath($publishRoot, $installedFile.FullName).Replace('$','$$')
        $uninstallLines.Add('Delete "$INSTDIR\' + $relative + '"')
    }
    foreach ($installedDir in (Get-ChildItem -LiteralPath $publishRoot -Directory -Recurse | Sort-Object { $_.FullName.Length } -Descending)) {
        $relative = [IO.Path]::GetRelativePath($publishRoot, $installedDir.FullName).Replace('$','$$')
        $uninstallLines.Add('RMDir "$INSTDIR\' + $relative + '"')
    }
    $uninstallLines | Set-Content -LiteralPath (Join-Path $projectRoot 'installer\generated-uninstall.nsh') -Encoding utf8
    $compiler = Join-Path $projectRoot '.tools\nsis\Bin\makensis.exe'
    if (!(Test-Path $compiler)) { $compiler = (Get-Command makensis.exe -ErrorAction Stop).Source }
    & $compiler /V2 /INPUTCHARSET UTF8 "/DPUBLISH_DIR=$publishRoot" "/DAPP_VERSION=$version" installer\CryptoSSH.nsi
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }
}
