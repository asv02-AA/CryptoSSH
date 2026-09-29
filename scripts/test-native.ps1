param([switch]$Integration, [switch]$CoreOnly)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$env:RUTOKEN_SSH_TEST_DATA = Join-Path $root ('artifacts\test-' + [guid]::NewGuid().ToString('N'))
$version = ([xml](Get-Content (Join-Path $root 'native\CryptoSSH\CryptoSSH.csproj') -Raw)).Project.PropertyGroup.Version
$exe = Join-Path $root "release\app-$version\CryptoSSH.exe"
if (!(Test-Path -LiteralPath $exe)) { throw 'Build the native application first.' }
$fixture = $null
try {
  if ($Integration) {
    $fixture = Start-Process node -ArgumentList (Join-Path $PSScriptRoot 'ssh-fixture.cjs') -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 2
    $env:RUTOKEN_SSH_FIXTURE_PORT = Get-Content (Join-Path $root 'artifacts\ssh-integration\port.txt')
    $env:RUTOKEN_SSH_FIXTURE_KEY = Join-Path $root 'artifacts\ssh-integration\id_ed25519'
  }
  $testFlags = if ($CoreOnly) { @('--self-test') } else { @('--self-test','--smoke-test') }
  foreach ($flag in $testFlags) {
    $process = Start-Process -FilePath $exe -ArgumentList $flag -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(60000)) { $process.Kill(); throw "Test timed out: $flag" }
    if ($process.ExitCode -ne 0) { throw "Test failed: $flag. See $env:RUTOKEN_SSH_TEST_DATA" }
  }
  Get-Content (Join-Path $env:RUTOKEN_SSH_TEST_DATA 'self-test.txt')
  if (!$CoreOnly) { Get-Content (Join-Path $env:RUTOKEN_SSH_TEST_DATA 'artifacts\smoke-result.txt') }
} finally {
  if ($fixture -and !$fixture.HasExited) { $fixture.Kill() }
  Remove-Item Env:\RUTOKEN_SSH_TEST_DATA -ErrorAction SilentlyContinue
  Remove-Item Env:\RUTOKEN_SSH_FIXTURE_PORT -ErrorAction SilentlyContinue
  Remove-Item Env:\RUTOKEN_SSH_FIXTURE_KEY -ErrorAction SilentlyContinue
}
