$ErrorActionPreference = 'Stop'
$repoPath = Split-Path -Parent $PSScriptRoot
$testProjectName = 'aerosynapse-contract-tests-' + [Guid]::NewGuid().ToString('N')
Push-Location $repoPath
try {
    docker compose -p $testProjectName -f compose.contracts.yaml run --build --rm contract-tests
    $testExitCode = $LASTEXITCODE
}
finally {
    docker compose -p $testProjectName -f compose.contracts.yaml down --remove-orphans
    Pop-Location
}
exit $testExitCode
