param(
    [switch]$CleanupOnly
)

$ErrorActionPreference = 'Stop'
$runId = $env:CHESSREALMS_IDENTITY_E2E_RUN_ID
if ([string]::IsNullOrEmpty($runId) -and -not $CleanupOnly) {
    $runId = [Guid]::NewGuid().ToString('N')
}
if ($runId -notmatch '\A[0-9a-fA-F]{32}\z') {
    throw 'Identity E2E cleanup requires a valid run ID.'
}

function Remove-TestContainers {
    $filter = "label=org.chessrealms.identity-e2e=$runId"
    $containers = @(docker container ls --all --quiet --filter $filter)
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not list the containers for this Identity E2E run.'
    }
    if ($containers.Count -gt 0) {
        docker container rm --force --volumes @containers
        if ($LASTEXITCODE -ne 0) {
            throw 'Could not remove the containers for this Identity E2E run.'
        }
    }
    $remaining = @(docker container ls --all --quiet --filter $filter)
    if ($LASTEXITCODE -ne 0 -or $remaining.Count -gt 0) {
        throw 'Could not verify cleanup of the Identity E2E containers.'
    }
}

if ($CleanupOnly) {
    Remove-TestContainers
    return
}

$project = Join-Path $PSScriptRoot '../tests/ChessRealms.Platform.Identity.E2E.Tests/ChessRealms.Platform.Identity.E2E.Tests.csproj'
$previousRunId = $env:CHESSREALMS_IDENTITY_E2E_RUN_ID
$env:CHESSREALMS_IDENTITY_E2E_RUN_ID = $runId
Write-Output "Identity E2E run: $runId"
try {
    dotnet test $project --no-build --no-restore
    $testExitCode = $LASTEXITCODE
} finally {
    $env:CHESSREALMS_IDENTITY_E2E_RUN_ID = $previousRunId
    Remove-TestContainers
}
exit $testExitCode
