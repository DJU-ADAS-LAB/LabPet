#Requires -Version 7.2

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
$LabPetRoot = Split-Path -Parent $PSScriptRoot
$LabPetRuntime = 'win-x64'
$LabPetPackageId = 'LabPet'
$LabPetExecutable = 'LabPet.Client.exe'
$LabPetRepository = 'DJU-ADAS-LAB/LabPet'
$LabPetSolution = Join-Path $LabPetRoot 'LabPet.slnx'
$LabPetProject = Join-Path $LabPetRoot 'src/LabPet.Client/LabPet.Client.csproj'
$LabPetSdkFile = Join-Path $LabPetRoot 'global.json'
[xml]$LabPetProjectXml = Get-Content -LiteralPath $LabPetProject -Raw
$LabPetVersion = $LabPetProjectXml.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
$LabPetVelopackVersion = $LabPetProjectXml.SelectSingleNode('/Project/ItemGroup/PackageReference[@Include="Velopack"]').GetAttribute('Version')
if ($LabPetVersion -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw "클라이언트 Version은 세 자리 정식 릴리스 버전이어야 합니다. 현재 $LabPetVersion"
}

function Invoke-LabPetCommand {
    param([string]$Stage, [string]$Exe, [string[]]$Arguments)
    Write-Host "[$Stage]"
    & $Exe @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "[$Stage] 실패 ($Exe 종료 코드 $LASTEXITCODE)" }
}

function Get-LabPetGit {
    param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
    $result = & git -C $LabPetRoot @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Git $($Arguments[0]) 실패 ($LASTEXITCODE)" }
    return $result
}

function Test-LabPetBuildInputsClean {
    return !(Get-LabPetGit status --porcelain --untracked-files=all -- src tests scripts .github global.json LabPet.slnx Directory.Build.props Directory.Build.targets Directory.Packages.props NuGet.Config)
}

function Assert-LabPetBuildHost {
    if (!$IsWindows -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') {
        throw 'LabPet Windows 검증은 Windows x64에서 실행해야 합니다.'
    }
    $expected = (Get-Content -LiteralPath $LabPetSdkFile -Raw | ConvertFrom-Json).sdk.version
    $actual = & dotnet --version
    if ($LASTEXITCODE -ne 0 -or $actual -ne $expected) {
        throw "global.json의 .NET SDK $expected 버전이 필요합니다. 현재 $actual"
    }
}

function New-LabPetOutputDirectory {
    param([string]$Path)
    $resolved = [IO.Path]::GetFullPath($Path)
    if ((Test-Path -LiteralPath $resolved) -and @(Get-ChildItem -LiteralPath $resolved -Force).Count -gt 0) {
        throw "빌드 결과 섞임을 막기 위해 비어 있는 출력 디렉터리를 사용하세요. $resolved"
    }
    return (New-Item -Path $resolved -ItemType Directory -Force).FullName
}

function Get-LabPetVelopack {
    param([string]$ToolDirectory)
    Invoke-LabPetCommand 'Velopack CLI 설치' 'dotnet' @('tool', 'install', 'vpk', '--tool-path', $ToolDirectory, '--version', $LabPetVelopackVersion)
    $tool = Join-Path $ToolDirectory 'vpk.exe'
    $listed = & dotnet tool list --tool-path $ToolDirectory
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $tool -PathType Leaf) -or
        ($listed -join [Environment]::NewLine) -notmatch "(?m)^vpk\s+$([regex]::Escape($LabPetVelopackVersion))\s+vpk\s*$") {
        throw "Velopack CLI와 클라이언트 NuGet 버전($LabPetVelopackVersion)이 다릅니다."
    }
    return $tool
}

function Get-LabPetFileRecord {
    param([IO.FileInfo]$File)
    return [ordered]@{ name = $File.Name; size = $File.Length; sha256 = (Get-FileHash -LiteralPath $File.FullName -Algorithm SHA256).Hash }
}

function Assert-LabPetExecutable {
    param([string]$Path)
    $stream = [IO.File]::OpenRead($Path)
    try {
        if ($stream.Length -lt 64 -or $stream.ReadByte() -ne 0x4D -or $stream.ReadByte() -ne 0x5A) {
            throw "Windows 실행 파일 헤더가 올바르지 않습니다. $Path"
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-LabPetPackageInventory {
    param([string]$Directory)
    $feed = Get-Content -LiteralPath (Join-Path $Directory 'releases.win.json') -Raw | ConvertFrom-Json
    $names = [Collections.Generic.List[string]]::new()
    foreach ($name in @("$LabPetPackageId-win-Setup.exe", "$LabPetPackageId-win-Portable.zip", 'releases.win.json', 'RELEASES')) {
        $names.Add($name)
    }
    $types = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $full = $null
    foreach ($entry in $feed.Assets) {
        if ($entry.PackageId -cne $LabPetPackageId -or $entry.Version -cne $LabPetVersion -or
            $entry.Type -cnotin @('Full','Delta') -or !$types.Add($entry.Type)) {
            throw '업데이트 피드의 패키지 ID, 버전 또는 패키지 유형이 현재 빌드와 일치하지 않습니다.'
        }
        $expectedName = "$LabPetPackageId-$LabPetVersion-$($entry.Type.ToLowerInvariant()).nupkg"
        if ($entry.FileName -cne $expectedName) { throw "패키지 파일명이 올바르지 않습니다. $($entry.FileName)" }
        $record = Get-LabPetFileRecord (Get-Item -LiteralPath (Join-Path $Directory $expectedName))
        if ($record.size -le 0 -or $record.size -ne $entry.Size -or $record.sha256 -ine $entry.SHA256) {
            throw "피드의 패키지 크기 또는 SHA256이 일치하지 않습니다. $expectedName"
        }
        if ($entry.Type -ceq 'Full') { $full = $record }
        $names.Add($expectedName)
    }
    if (!$full) { throw '전체 업데이트 패키지가 없습니다.' }
    Assert-LabPetExecutable (Join-Path $Directory "$LabPetPackageId-win-Setup.exe")
    $oldFeed = (Get-Content -LiteralPath (Join-Path $Directory 'RELEASES') -Raw).Trim()
    $sha1 = (Get-FileHash -LiteralPath (Join-Path $Directory $full.name) -Algorithm SHA1).Hash
    if ($oldFeed -ine "$sha1 $($full.name) $($full.size)") { throw 'RELEASES와 현재 전체 패키지가 일치하지 않습니다.' }
    $inventory = foreach ($name in $names) {
        $file = Get-Item -LiteralPath (Join-Path $Directory $name)
        if ($file -isnot [IO.FileInfo] -or $file.Length -le 0) { throw "필수 릴리스 파일 누락 $name" }
        Get-LabPetFileRecord $file
    }
    return @($inventory)
}

function Invoke-LabPetGitHub {
    param([string]$Path, [string]$Method = 'Get', [hashtable]$Body, [string]$Token = $env:GH_TOKEN)
    $headers = @{ Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
    if ($Token) { $headers.Authorization = "Bearer $Token" }
    $options = @{ Uri = "https://api.github.com/repos/$LabPetRepository/$Path"; Headers = $headers; Method = $Method; TimeoutSec = 60 }
    if ($null -ne $Body) {
        $options.Body = $Body | ConvertTo-Json -Depth 8 -Compress
        $options.ContentType = 'application/json'
    }
    # GitHub의 목록 응답은 REST 호출이 한 덩어리로 반환하므로 호출자에게 개별 항목으로 전달한다.
    $response = Invoke-RestMethod @options
    return $response
}

function Get-LabPetVerifiedRun {
    param([string]$Commit)
    if ($Commit -cnotmatch '^[0-9a-f]{40}$') { throw '검증 조회에 전체 커밋 SHA가 필요합니다.' }
    $runs = Invoke-LabPetGitHub "actions/workflows/verify.yml/runs?branch=main&event=push&head_sha=$Commit&status=success&per_page=30"
    $verified = $runs.workflow_runs | Where-Object {
        $_.head_sha -ceq $Commit -and $_.head_branch -ceq 'main' -and $_.event -ceq 'push' -and
        $_.status -ceq 'completed' -and $_.conclusion -ceq 'success' -and $_.head_repository.full_name -ieq $LabPetRepository
    } | Select-Object -First 1
    if (!$verified) { throw "main의 커밋 $Commit 검증이 성공한 기록이 없습니다." }
    return $verified
}

function Get-LabPetVerifiedArtifactName {
    param($Run, [string]$Commit)
    $name = "labpet-windows-$Commit-$($Run.run_attempt)"
    $available = Invoke-LabPetGitHub "actions/runs/$($Run.id)/artifacts?name=$name"
    $artifact = @($available.artifacts | Where-Object { $_.name -ceq $name -and !$_.expired -and $_.size_in_bytes -gt 0 })
    if ($artifact.Count -ne 1) { throw "해당 검증 실행에 사용 가능한 패키지 아티팩트가 없습니다. $name" }
    return $name
}
