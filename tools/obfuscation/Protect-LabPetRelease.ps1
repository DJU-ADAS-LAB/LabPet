[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PublishDirectory,
    [Parameter(Mandatory)][string] $WorkDirectory
)

$ErrorActionPreference = 'Stop'
$publishPath = (Resolve-Path -LiteralPath $PublishDirectory).Path
$workPath = (New-Item -ItemType Directory -Path $WorkDirectory -Force).FullName
if ($workPath.Equals($publishPath, [StringComparison]::OrdinalIgnoreCase) -or
    $workPath.StartsWith($publishPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw '난독화 도구와 매핑은 배포 디렉터리 밖에 있어야 합니다.'
}

$client = Join-Path $publishPath 'LabPet.Client.dll'
$before = (Get-FileHash -LiteralPath $client -Algorithm SHA256).Hash
$tools = Join-Path $workPath 'tools-2.2.50'
$obfuscar = Join-Path $tools 'obfuscar.console.exe'
if (-not (Test-Path -LiteralPath $obfuscar -PathType Leaf)) {
    dotnet tool install Obfuscar.GlobalTool --version 2.2.50 --tool-path $tools --allow-roll-forward
    if ($LASTEXITCODE -ne 0) { throw 'Obfuscar 설치에 실패했습니다.' }
}

# 표식이 붙은 검증·정산 메서드만 대상으로 삼아 WPF와 직렬화 계약을 유지한다.
$output = (New-Item -ItemType Directory -Path (Join-Path $workPath ([Guid]::NewGuid().ToString('N')))).FullName
$config = Join-Path $output 'obfuscar.xml'
$inputXml = [System.Security.SecurityElement]::Escape($publishPath)
$outputXml = [System.Security.SecurityElement]::Escape($output)
$clientXml = [System.Security.SecurityElement]::Escape($client)
@"
<Obfuscator>
  <Var name="InPath" value="$inputXml" />
  <Var name="OutPath" value="$outputXml" />
  <Var name="MarkedOnly" value="true" />
  <Var name="KeepPublicApi" value="true" />
  <Var name="HidePrivateApi" value="true" />
  <Var name="HideStrings" value="false" />
  <AssemblySearchPath path="$inputXml" />
  <Module file="$clientXml" />
</Obfuscator>
"@ | Set-Content -LiteralPath $config -Encoding utf8

& $obfuscar $config
if ($LASTEXITCODE -ne 0) { throw '클라이언트 난독화에 실패했습니다.' }

$rewritten = Join-Path $output 'LabPet.Client.dll'
$mapping = Get-Content -LiteralPath (Join-Path $output 'Mapping.txt') -Raw
foreach ($method in @('SaveProtectedProfileAsync', 'SettleMultiplayerWagerAsync',
        'TryResolveTypingMatchAsync', 'TryAcceptPetInteraction')) {
    if ($mapping -notmatch "(?m)::${method}\[\d+\]\([^\r\n]* -> ") {
        throw "필수 난독화 대상이 재작성되지 않았습니다: $method"
    }
}
$after = (Get-FileHash -LiteralPath $rewritten -Algorithm SHA256).Hash
if ($after -eq $before) { throw '난독화 전후 클라이언트 DLL이 같습니다.' }

Copy-Item -LiteralPath $rewritten -Destination $client -Force
foreach ($symbol in @('LabPet.Client.pdb', 'LabPet.Shared.pdb')) {
    $path = Join-Path $publishPath $symbol
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        Move-Item -LiteralPath $path -Destination (Join-Path $output "original-$symbol")
    }
}
Write-Host 'LabPet.Client.dll 난독화 및 매핑 검증 완료'
