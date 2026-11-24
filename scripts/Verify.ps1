#Requires -Version 7.2
[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "../.artifacts/verify-$([Guid]::NewGuid().ToString('N'))"),
    [ValidateRange(1, 10)][int]$Repeat = 1,
    [switch]$IncludePreviousRelease
)

. "$PSScriptRoot/Common.ps1"
Push-Location $LabPetRoot
try {
    Assert-LabPetBuildHost
    $root = New-LabPetOutputDirectory $OutputDirectory
    $commit = Get-LabPetGit rev-parse HEAD
    $trackedClean = Test-LabPetBuildInputsClean
    $build = Join-Path $root 'build'
    $results = Join-Path $root 'TestResults'
    Invoke-LabPetCommand 'NuGet 복원' 'dotnet' @('restore', $LabPetSolution, '--artifacts-path', $build)
    foreach ($configuration in @('Debug', 'Release')) {
        Invoke-LabPetCommand "$configuration 빌드" 'dotnet' @('build', $LabPetSolution, '-c', $configuration, '--no-restore', '--artifacts-path', $build)
        for ($iteration = 1; $iteration -le $Repeat; $iteration++) {
            Invoke-LabPetCommand "$configuration 전체 테스트 ($iteration/$Repeat)" 'dotnet' @(
                'test', $LabPetSolution, '-c', $configuration, '--no-build', '--no-restore', '--artifacts-path', $build,
                '--results-directory', $results, '--logger', "trx;LogFileName=$configuration-$iteration.trx")
        }
    }

    & "$PSScriptRoot/BuildPackage.ps1" -OutputDirectory (Join-Path $root 'package') -IncludePreviousRelease:$IncludePreviousRelease
    $packages = Join-Path $root 'package/packages'
    [ordered]@{
        commit = $commit
        trackedClean = $trackedClean -and (Test-LabPetBuildInputsClean) -and (Get-LabPetGit rev-parse HEAD) -ceq $commit
        configurations = @('Debug','Release')
        manifestSha256 = (Get-FileHash -LiteralPath (Join-Path $packages 'package-manifest.json')).Hash
        runId = $env:GITHUB_RUN_ID
        runAttempt = $env:GITHUB_RUN_ATTEMPT
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packages 'verification.json') -Encoding utf8
    Write-Host "검증 완료 $root"
}
finally {
    Pop-Location
}
