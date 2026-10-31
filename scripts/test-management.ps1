$ErrorActionPreference = 'Stop'
$repoPath = Split-Path -Parent $PSScriptRoot
$projectName = 'aerosynapse-management-tests-' + [Guid]::NewGuid().ToString('N')
Push-Location $repoPath
try {
    docker compose -p $projectName -f compose.management-tests.yaml run --build --rm management-tests
    $result = $LASTEXITCODE
}
finally {
    docker compose -p $projectName -f compose.management-tests.yaml down --volumes --remove-orphans
    Pop-Location
}
exit $result
