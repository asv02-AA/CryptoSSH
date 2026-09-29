param([string]$SoftHsmRoot = '')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$SoftHsmRoot) { $SoftHsmRoot = Join-Path $root '.tools\softhsm\files\SoftHSM2' }
$testRoot = Join-Path $root ('artifacts\pkcs11-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $testRoot 'tokens') -Force | Out-Null
$env:SOFTHSM2_CONF = Join-Path $testRoot 'softhsm2.conf'
$env:CRYPTOSSH_TEST_PKCS11 = Join-Path $SoftHsmRoot 'lib\softhsm2-x64.dll'
$env:RUTOKEN_SSH_TEST_DATA = $testRoot
$fixture = $null
try {
  ('directories.tokendir = ' + (Join-Path $testRoot 'tokens').Replace('\','/') + "`nobjectstore.backend = file`nlog.level = ERROR`n") | Set-Content -LiteralPath $env:SOFTHSM2_CONF -Encoding ascii
  & (Join-Path $SoftHsmRoot 'bin\softhsm2-util.exe') --module (Join-Path $SoftHsmRoot 'lib\softhsm2.dll') --init-token --free --label 'CryptoSSH isolated test' --so-pin 'test-admin-only' --pin 'test-fixture-only'
  if ($LASTEXITCODE -ne 0) { throw 'SoftHSM initialization failed' }
  $version = ([xml](Get-Content (Join-Path $root 'native\CryptoSSH\CryptoSSH.csproj') -Raw)).Project.PropertyGroup.Version
  $exe = Join-Path $root "release\app-$version\CryptoSSH.exe"
  $test = Start-Process -FilePath $exe -ArgumentList '--token-self-test' -WindowStyle Hidden -PassThru
  if (!$test.WaitForExit(30000)) { $test.Kill(); throw 'PKCS#11 test timed out' }
  Get-Content (Join-Path $testRoot 'token-test.txt')
  if ($test.ExitCode -ne 0) { throw 'PKCS#11 test failed' }
  $env:CRYPTOSSH_TEST_TOKEN_PUBLIC = Join-Path $testRoot 'token.pub'
  $fixture = Start-Process node -ArgumentList (Join-Path $PSScriptRoot 'ssh-fixture.cjs') -WindowStyle Hidden -PassThru
  Start-Sleep -Seconds 2
  $env:RUTOKEN_SSH_FIXTURE_PORT = Get-Content (Join-Path $root 'artifacts\ssh-integration\port.txt')
  $env:RUTOKEN_SSH_FIXTURE_KEY = Join-Path $root 'artifacts\ssh-integration\id_ed25519'
  $test = Start-Process -FilePath $exe -ArgumentList '--smoke-test' -WindowStyle Hidden -PassThru
  if (!$test.WaitForExit(60000)) { $test.Kill(); throw 'SSH PKCS#11 test timed out' }
  Get-Content (Join-Path $testRoot 'artifacts\smoke-result.txt')
  if ($test.ExitCode -ne 0) { throw 'SSH PKCS#11 test failed' }
} finally {
  if ($fixture -and !$fixture.HasExited) { $fixture.Kill() }
  foreach ($name in @('SOFTHSM2_CONF','CRYPTOSSH_TEST_PKCS11','CRYPTOSSH_TEST_TOKEN_PUBLIC','RUTOKEN_SSH_TEST_DATA','RUTOKEN_SSH_FIXTURE_PORT','RUTOKEN_SSH_FIXTURE_KEY')) { Remove-Item -LiteralPath "Env:\$name" -ErrorAction SilentlyContinue }
}
