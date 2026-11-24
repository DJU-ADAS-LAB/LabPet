#Requires -Version 7.2
[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "../.artifacts/package-$([Guid]::NewGuid().ToString('N'))"),
    [switch]$IncludePreviousRelease
)

. "$PSScriptRoot/Common.ps1"

function Assert-PackedApplication {
    param([string]$Directory)
    $fullPackage = Join-Path $Directory "$LabPetPackageId-$LabPetVersion-full.nupkg"
    $archive = [IO.Compression.ZipFile]::OpenRead($fullPackage)
    try {
        foreach ($path in $resources) {
            $entry = @($archive.Entries | Where-Object { $_.FullName.EndsWith("/$path", [StringComparison]::OrdinalIgnoreCase) })
            if ($entry.Count -ne 1 -or $entry[0].Length -le 0) { throw "전체 패키지 필수 파일 누락 $path" }
        }
    }
    finally {
        $archive.Dispose()
    }

    $portable = [IO.Compression.ZipFile]::OpenRead((Join-Path $Directory "$LabPetPackageId-win-Portable.zip"))
    try {
        if (@($portable.Entries | Where-Object { $_.Name -ceq $LabPetExecutable -and $_.Length -gt 0 }).Count -lt 1) {
            throw '포터블 ZIP에서 앱 실행 파일을 찾을 수 없습니다.'
        }
    }
    finally {
        $portable.Dispose()
    }
}

Push-Location $LabPetRoot
try {
    Assert-LabPetBuildHost
    $root = New-LabPetOutputDirectory $OutputDirectory
    $publish = Join-Path $root 'publish'
    $fullOutput = Join-Path $root 'full'
    $publishArgs = @('publish', $LabPetProject, '-c', 'Release', '-r', $LabPetRuntime, '--self-contained', 'true',
        '--artifacts-path', (Join-Path $root 'build'), '-o', $publish)
    if (![string]::IsNullOrWhiteSpace($env:LABPET_SERVER_URL)) {
        $publishArgs += "-p:LabPetDefaultServerUrl=$($env:LABPET_SERVER_URL.Trim())"
    }
    Invoke-LabPetCommand "$LabPetRuntime self-contained 게시" 'dotnet' $publishArgs

    $resources = @(
        $LabPetExecutable, 'LabPet.Client.dll', 'coreclr.dll', 'hostfxr.dll', 'Velopack.dll', 'LabPet.Client.runtimeconfig.json',
        'Assets/Models/Characters/mochi_spirit/model.glb', 'Assets/Models/Characters/chibi_cat/model.glb',
        'Assets/Models/Characters/fluffy_cloud/model.glb', 'Assets/Models/Characters/fluffy_puppy/model.glb',
        'Assets/Models/Characters/hachiware/model.glb', 'Assets/Models/Characters/chiikawa/model.glb',
        'Assets/Models/Characters/usagi/model.glb'
    )
    foreach ($path in $resources) {
        $file = Get-Item -LiteralPath (Join-Path $publish $path)
        if ($file -isnot [IO.FileInfo] -or $file.Length -le 0) { throw "게시 필수 파일 누락 $path" }
    }
    Assert-LabPetExecutable (Join-Path $publish $LabPetExecutable)
    $assembly = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $publish 'LabPet.Client.dll'))
    if ($assembly.Version.ToString(3) -cne $LabPetVersion) { throw '게시 앱의 어셈블리 버전이 일치하지 않습니다.' }

    $vpk = Get-LabPetVelopack (Join-Path $root 'tools')
    $packArgs = @('pack', '--runtime', $LabPetRuntime, '-u', $LabPetPackageId, '-v', $LabPetVersion,
        '-p', $publish, '-e', $LabPetExecutable, '-i', (Join-Path $LabPetRoot 'src/LabPet.Client/Assets/app.ico'))
    Invoke-LabPetCommand 'Velopack 전체 설치 패키징' $vpk ($packArgs + @('-o', $fullOutput))
    $inventory = Get-LabPetPackageInventory $fullOutput
    Assert-PackedApplication $fullOutput
    $selected = $fullOutput

    if ($IncludePreviousRelease) {
        $deltaOutput = Join-Path $root 'delta'
        try {
            $downloadArgs = @('download', 'github', '--repoUrl', "https://github.com/$LabPetRepository", '--outputDir', $deltaOutput)
            Invoke-LabPetCommand '이전 릴리스 받기' $vpk $downloadArgs
            if (!(Get-ChildItem -LiteralPath $deltaOutput -File -Filter '*-full.nupkg')) {
                throw '이전 전체 패키지를 받지 못했습니다.'
            }
            Invoke-LabPetCommand 'Velopack 차분 패키징' $vpk ($packArgs + @('-o', $deltaOutput))
            # vpk는 계산에 사용한 이전 전체 패키지도 로컬 피드에 포함한다. 공개 피드에는 이번에 함께 올리는 자산만 둔다.
            $feedPath = Join-Path $deltaOutput 'releases.win.json'
            $feed = Get-Content -LiteralPath $feedPath -Raw | ConvertFrom-Json
            $feed.Assets = @($feed.Assets | Where-Object { $_.Version -ceq $LabPetVersion })
            $feed | ConvertTo-Json -Depth 5 -Compress | Set-Content -LiteralPath $feedPath -Encoding utf8
            $oldFeedPath = Join-Path $deltaOutput 'RELEASES'
            $currentFullName = "$LabPetPackageId-$LabPetVersion-full.nupkg"
            $oldFeed = Get-Content -LiteralPath $oldFeedPath | Where-Object {
                $_ -match "^[0-9a-fA-F]{40} $([regex]::Escape($currentFullName)) [1-9][0-9]*$"
            }
            $oldFeed | Set-Content -LiteralPath $oldFeedPath -Encoding utf8
            $deltaInventory = Get-LabPetPackageInventory $deltaOutput
            if ($deltaInventory.name -cnotcontains "$LabPetPackageId-$LabPetVersion-delta.nupkg") {
                throw 'Velopack이 이번 버전의 차분 패키지를 생성하지 않았습니다.'
            }
            Assert-PackedApplication $deltaOutput
            $inventory = $deltaInventory
            $selected = $deltaOutput
        }
        catch {
            Write-Warning "선택 차분 단계 실패. 검증을 통과한 전체 패키지를 사용합니다. $($_.Exception.Message)"
        }
    }

    $packages = New-LabPetOutputDirectory (Join-Path $root 'packages')
    foreach ($asset in $inventory) {
        Copy-Item -LiteralPath (Join-Path $selected $asset.name) -Destination $packages
    }
    [ordered]@{
        schema = 1
        commit = Get-LabPetGit rev-parse HEAD
        version = $LabPetVersion
        runtime = $LabPetRuntime
        packageId = $LabPetPackageId
        velopackVersion = $LabPetVelopackVersion
        files = $inventory
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $packages 'package-manifest.json') -Encoding utf8
    Write-Host "패키징 완료 $packages"
}
finally {
    Pop-Location
}
