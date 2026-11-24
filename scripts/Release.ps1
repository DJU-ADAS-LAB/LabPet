#Requires -Version 7.2
[CmdletBinding(DefaultParameterSetName = 'CreateTag')]
param(
    [Parameter(Mandatory,ParameterSetName='Resolve')][switch]$ResolveVerification,
    [Parameter(Mandatory,ParameterSetName='Publish')][switch]$Publish,
    [Parameter(Mandatory,ParameterSetName='CreateTag')]
    [Parameter(Mandatory,ParameterSetName='Publish')][string]$PackageDirectory,
    [Parameter(Mandatory,ParameterSetName='Publish')][long]$RunId,
    [Parameter(Mandatory,ParameterSetName='Publish')][int]$RunAttempt
)

. "$PSScriptRoot/Common.ps1"

function Get-ExistingRelease {
    $found = @(Invoke-LabPetGitHub 'releases?per_page=100' | Where-Object { $_.tag_name -ceq $tag })
    if ($found.Count -gt 1) { throw "릴리스가 중복되었습니다. $tag" }
    if ($found.Count -eq 1) { return $found[0] }
    try { return Invoke-LabPetGitHub "releases/tags/$tag" }
    catch [Microsoft.PowerShell.Commands.HttpResponseException] {
        if ($_.Exception.Response.StatusCode.value__ -ne 404) { throw }
        return $null
    }
}

function Get-PinnedVerifiedRun {
    param([long]$Id, [int]$Attempt)
    $run = Invoke-LabPetGitHub "actions/runs/$Id/attempts/$Attempt"
    if ($run.head_sha -cne $commit -or $run.head_branch -cne 'main' -or $run.event -cne 'push' -or
        $run.head_repository.full_name -ine $LabPetRepository -or $run.path -notmatch '^\.github/workflows/verify\.yml(@.*)?$' -or
        $run.status -cne 'completed' -or $run.conclusion -cne 'success' -or $run.run_attempt -ne $Attempt) {
        throw '지정한 실행은 같은 커밋의 성공한 main 검증이 아닙니다.'
    }
    return $run
}

function Assert-PackageProvenance {
    param([switch]$FromActions)
    $verification = Get-Content -LiteralPath (Join-Path $PackageDirectory 'verification.json') -Raw | ConvertFrom-Json
    $manifestPath = Join-Path $PackageDirectory 'package-manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($verification.commit -cne $commit -or $verification.trackedClean -ne $true -or
        ($verification.configurations -join ',') -cne 'Debug,Release' -or
        $verification.manifestSha256 -ine (Get-FileHash -LiteralPath $manifestPath).Hash -or
        $manifest.schema -ne 1 -or $manifest.commit -cne $commit -or $manifest.version -cne $LabPetVersion -or
        $manifest.runtime -cne $LabPetRuntime -or $manifest.packageId -cne $LabPetPackageId -or
        $manifest.velopackVersion -cne $LabPetVelopackVersion) {
        throw '검증 기록의 커밋, 구성, 버전 또는 매니페스트 해시가 현재 릴리스와 다릅니다.'
    }
    if ($FromActions -and ($verification.runId -ne $RunId -or $verification.runAttempt -ne $RunAttempt)) {
        throw '패키지의 검증 실행 번호가 다운로드한 실행과 다릅니다.'
    }
    $actual = Get-LabPetPackageInventory $PackageDirectory
    if ($actual.Count -ne $manifest.files.Count) { throw '검증된 릴리스 파일 수가 일치하지 않습니다.' }
    foreach ($asset in $actual) {
        $expected = @($manifest.files | Where-Object { $_.name -ceq $asset.name })
        if ($expected.Count -ne 1 -or $expected[0].sha256 -ine $asset.sha256 -or $expected[0].size -ne $asset.size) {
            throw "검증 기록과 실제 파일이 다릅니다. $($asset.name)"
        }
    }
    return $actual
}

function Assert-RemoteAsset {
    param($Remote, $Local)
    if ($Remote.state -cne 'uploaded' -or $Remote.size -ne $Local.size) {
        throw "GitHub 릴리스 자산 크기 또는 업로드 상태가 다릅니다. $($Local.name)"
    }
    $digestProperty = $Remote.PSObject.Properties['digest']
    if ($null -ne $digestProperty -and $digestProperty.Value) {
        if ($digestProperty.Value -ine "sha256:$($Local.sha256)") { throw "GitHub SHA256 불일치 $($Local.name)" }
        return
    }

    $temp = [IO.Path]::GetTempFileName()
    try {
        $headers = @{ Accept = 'application/octet-stream'; Authorization = "Bearer $env:GH_TOKEN"; 'X-GitHub-Api-Version' = '2022-11-28' }
        $uri = "https://api.github.com/repos/$LabPetRepository/releases/assets/$($Remote.id)"
        Invoke-WebRequest -Uri $uri -Headers $headers -OutFile $temp -TimeoutSec 300
        if ((Get-FileHash -LiteralPath $temp).Hash -ine $Local.sha256) { throw "GitHub 다운로드 SHA256 불일치 $($Local.name)" }
    }
    finally {
        Remove-Item -LiteralPath $temp
    }
}

function Assert-ReleaseAssets {
    param($Release, [array]$Expected)
    $assets = @(Invoke-LabPetGitHub "releases/$($Release.id)/assets?per_page=100")
    if ($assets.Count -ne $Expected.Count) { throw 'GitHub에 필수 릴리스 파일이 모두 업로드되지 않았습니다.' }
    foreach ($local in $Expected) {
        $remote = @($assets | Where-Object { $_.name -ceq $local.name })
        if ($remote.Count -ne 1) { throw "GitHub 파일 누락 $($local.name)" }
        Assert-RemoteAsset $remote[0] $local
    }
}

function Assert-RemoteTag {
    $remote = @(Get-LabPetGit ls-remote --tags origin "refs/tags/$tag" "refs/tags/$tag^{}")
    $peeled = @($remote | Where-Object { $_.EndsWith("refs/tags/$tag^{}", [StringComparison]::Ordinal) })
    $reference = if ($peeled.Count -eq 1) { $peeled[0] } elseif ($remote.Count -eq 1) { $remote[0] } else { '' }
    if (!$reference -or ($reference -split '\s+')[0] -cne $commit) {
        throw '원격 태그가 검증 대상 커밋과 일치하지 않습니다.'
    }
}

function Assert-NewReleaseVersion {
    try {
        $previous = Invoke-LabPetGitHub 'releases/latest'
        if ($previous.tag_name -match '^v([0-9]+\.[0-9]+\.[0-9]+)$' -and [version]$Matches[1] -ge [version]$LabPetVersion) {
            throw '최신 공개 버전보다 높은 릴리스 버전이 필요합니다.'
        }
    }
    catch [Microsoft.PowerShell.Commands.HttpResponseException] {
        if ($_.Exception.Response.StatusCode.value__ -ne 404) { throw }
    }
}

function Assert-DownloadableUpdateFeed {
    param([array]$Expected)
    $temp = [IO.Path]::GetTempFileName()
    try {
        $uri = "https://github.com/$LabPetRepository/releases/download/$tag/releases.win.json"
        Invoke-WebRequest -Uri $uri -OutFile $temp -TimeoutSec 30
        $feed = $Expected | Where-Object { $_.name -ceq 'releases.win.json' }
        if ((Get-FileHash -LiteralPath $temp).Hash -ine $feed.sha256) {
            throw '공개 자동 업데이트 피드의 SHA256이 다릅니다.'
        }
    }
    finally {
        Remove-Item -LiteralPath $temp
    }
}

$tag = "v$LabPetVersion"
$commit = Get-LabPetGit rev-parse HEAD

if (!$ResolveVerification -and !$Publish) {
    if ((Get-LabPetGit branch --show-current) -cne 'main' -or !(Test-LabPetBuildInputsClean)) {
        throw '빌드 입력이 깨끗한 main에서 릴리스 태그를 생성하세요.'
    }
    $origin = Get-LabPetGit remote get-url origin
    if ($origin -notmatch '^(https://github\.com/|git@github\.com:)DJU-ADAS-LAB/LabPet(?:\.git)?$') {
        throw 'origin이 LabPet 공식 GitHub 저장소를 가리켜야 합니다.'
    }
    if ((Get-LabPetGit tag --list $tag) -or (Get-LabPetGit ls-remote --tags origin "refs/tags/$tag")) {
        throw "$tag 태그가 이미 있습니다. 같은 커밋의 배포는 기존 Actions 실행에서 재실행하세요."
    }
    if (Get-ExistingRelease) { throw "$tag 릴리스가 이미 있습니다." }
    Assert-NewReleaseVersion
    $null = Assert-PackageProvenance
    $verified = Get-LabPetVerifiedRun $commit
    $null = Get-LabPetVerifiedArtifactName $verified $commit
    Get-LabPetGit tag -a $tag -m "$LabPetPackageId $tag" $commit
    Write-Host "로컬 태그 $tag 생성 완료. 원격 태그 전송은 별도로 실행하세요."
    return
}

if ($env:GITHUB_ACTIONS -cne 'true' -or $env:GITHUB_EVENT_NAME -cne 'push' -or
    $env:GITHUB_REPOSITORY -ine $LabPetRepository -or $env:GITHUB_REF -cne "refs/tags/$tag" -or
    (Get-LabPetGit rev-parse "$tag^{commit}") -cne $commit -or !$env:GH_TOKEN) {
    throw '공식 저장소의 검증된 릴리스 태그 워크플로에서만 공개할 수 있습니다.'
}

Assert-RemoteTag
$release = Get-ExistingRelease
$markerPattern = "<!-- LabPet commit=$commit run=([0-9]+) attempt=([0-9]+) -->"
if ($release -and $release.body -cnotmatch $markerPattern) {
    throw "$tag의 기존 릴리스는 검증된 커밋 및 실행 기록과 일치하지 않습니다."
}

if ($ResolveVerification) {
    $verified = if ($release) {
        $markerMatch = [regex]::Match($release.body, $markerPattern)
        Get-PinnedVerifiedRun ([long]$markerMatch.Groups[1].Value) ([int]$markerMatch.Groups[2].Value)
    } else {
        Get-LabPetVerifiedRun $commit
    }
    "run-id=$($verified.id)" >> $env:GITHUB_OUTPUT
    "attempt=$($verified.run_attempt)" >> $env:GITHUB_OUTPUT
    "artifact=$(Get-LabPetVerifiedArtifactName $verified $commit)" >> $env:GITHUB_OUTPUT
    return
}

$marker = "<!-- LabPet commit=$commit run=$RunId attempt=$RunAttempt -->"
if ($release -and !$release.body.Contains($marker)) {
    throw '기존 릴리스 초안은 다른 검증 산출물에 연결되어 있습니다.'
}
$null = Get-PinnedVerifiedRun $RunId $RunAttempt
$inventory = Assert-PackageProvenance -FromActions
if ($release -and !$release.draft) {
    Assert-ReleaseAssets $release $inventory
    Assert-DownloadableUpdateFeed $inventory
    Write-Host "이미 게시되었으며 파일 검증을 통과했습니다. $($release.html_url)"
    return
}

Assert-NewReleaseVersion

if (!$release) {
    $release = Invoke-LabPetGitHub 'releases' -Method Post -Body @{
        tag_name = $tag; target_commitish = $commit; name = "$LabPetPackageId $tag"; body = $marker
        draft = $true; prerelease = $false
    }
}

$existingAssets = @(Invoke-LabPetGitHub "releases/$($release.id)/assets?per_page=100")
foreach ($asset in $inventory) {
    $existing = $existingAssets | Where-Object { $_.name -ceq $asset.name } | Select-Object -First 1
    if ($existing -and $existing.state -ceq 'starter') {
        $null = Invoke-LabPetGitHub "releases/assets/$($existing.id)" -Method Delete
        $existing = $null
    }
    if ($existing) {
        Assert-RemoteAsset $existing $asset
        continue
    }
    Write-Host "[GitHub 초안 업로드] $($asset.name)"
    $uploadBase = $release.upload_url.Split('{')[0]
    if ($uploadBase -notmatch '^https://uploads\.github\.com/repos/DJU-ADAS-LAB/LabPet/releases/[0-9]+/assets$') {
        throw 'GitHub 업로드 주소가 공식 저장소와 다릅니다.'
    }
    $upload = @{
        Uri = "$($uploadBase)?name=$([Uri]::EscapeDataString($asset.name))"
        Method = 'Post'
        Headers = @{ Authorization = "Bearer $env:GH_TOKEN"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
        ContentType = 'application/octet-stream'
        InFile = Join-Path $PackageDirectory $asset.name
        TimeoutSec = 300
    }
    Assert-RemoteAsset (Invoke-RestMethod @upload) $asset
}

Assert-ReleaseAssets $release $inventory
Assert-RemoteTag
$published = Invoke-LabPetGitHub "releases/$($release.id)" -Method Patch -Body @{ draft = $false; make_latest = 'true' }
if ($published.draft -or $published.tag_name -cne $tag) { throw 'GitHub 정식 릴리스 공개 확인 실패' }
Assert-ReleaseAssets $published $inventory
Assert-DownloadableUpdateFeed $inventory
$latest = Invoke-LabPetGitHub 'releases/latest'
if ($latest.id -ne $published.id) { throw '공개 릴리스가 최신 자동 업데이트 대상으로 표시되지 않았습니다.' }
Write-Host "공개 및 파일 검증 완료 $($published.html_url)"
