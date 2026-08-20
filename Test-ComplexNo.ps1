# Test-ComplexNo.ps1
#
# 목적: 단체 ID(duckeun72)로 "중개사 매물목록" API(RealtorArticleList)를 실제로 한 번 호출해
#       응답에 complexNo/complexNumber(단지번호) 필드가 실제로 들어오는지 확인합니다.
#       최상위 필드뿐 아니라 항목 내부에 중첩된 필드까지 탐색해 실제 위치(경로)를 알려줍니다.
#       Bearer 토큰/Cookie 등 민감정보는 appsettings.json에서 로컬로만 읽어 사용하며
#       콘솔에 출력하지 않습니다.
#
# 실행 방법 (반드시 이 컴퓨터의 일반 PowerShell/Windows Terminal에서 실행하세요):
#
#   cd D:\Source\NaverPropertyRanking
#   powershell -ExecutionPolicy Bypass -File .\Test-ComplexNo.ps1 -GroupId duckeun72
#
# 필요하면 -SettingsPath 로 appsettings.json 경로를 직접 지정할 수 있습니다.
# (기본값: 이 스크립트와 같은 폴더의 src\NaverPropertyRanking\appsettings.json)

param(
    [string]$GroupId = "duckeun72",
    [string]$SettingsPath = "",
    [int]$Page = 1
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($SettingsPath)) {
    $SettingsPath = Join-Path $PSScriptRoot "src\NaverPropertyRanking\appsettings.json"
}

if (-not (Test-Path $SettingsPath)) {
    Write-Error "appsettings.json을 찾을 수 없습니다: $SettingsPath`n-SettingsPath 로 실제 경로를 지정해 주세요."
    exit 1
}

Write-Host "설정 파일: $SettingsPath"
$settings = Get-Content -Raw -Path $SettingsPath | ConvertFrom-Json

$api = $settings.Api.RealtorArticleList
if ($null -eq $api) {
    Write-Error "appsettings.json에 Api.RealtorArticleList 섹션이 없습니다."
    exit 1
}

$baseUrl = $settings.Api.BaseUrl.TrimEnd('/')
$endpoint = $api.Endpoint
$realtorIdParam = if ([string]::IsNullOrWhiteSpace($api.RealtorIdParameter)) { "realtorId" } else { $api.RealtorIdParameter }

# appsettings.json의 Params + realtorId + page 를 합쳐 쿼리스트링 구성 (앱의 BuildArticleListPath와 동일 로직)
$params = [ordered]@{}
if ($api.Params) {
    foreach ($prop in $api.Params.PSObject.Properties) {
        $params[$prop.Name] = [string]$prop.Value
    }
}
$params[$realtorIdParam] = $GroupId
$params["page"] = [string]$Page

$queryString = ($params.GetEnumerator() | ForEach-Object {
    "$([uri]::EscapeDataString($_.Key))=$([uri]::EscapeDataString($_.Value))"
}) -join "&"

$url = "$baseUrl$endpoint`?$queryString"

# 헤더 구성 (앱의 ApplyConfiguredHeaders와 동일한 기본값 사용, 민감값은 appsettings.json에서만 로드)
$headers = @{}
if ($api.Headers) {
    foreach ($prop in $api.Headers.PSObject.Properties) {
        $headers[$prop.Name] = [string]$prop.Value
    }
}
if (-not $headers.ContainsKey("Accept")) { $headers["Accept"] = "application/json" }
if (-not $headers.ContainsKey("Accept-Language")) { $headers["Accept-Language"] = "ko-KR,ko;q=0.9" }
if (-not $headers.ContainsKey("Referer")) { $headers["Referer"] = "$baseUrl/" }
if (-not $headers.ContainsKey("User-Agent")) {
    $headers["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/144.0.0.0 Safari/537.36"
}

if ([string]::IsNullOrWhiteSpace($headers["Authorization"]) -or $headers["Authorization"] -match "REALTOR_LIST_TOKEN") {
    Write-Warning "Authorization(Bearer 토큰)이 비어 있거나 예시값(REALTOR_LIST_TOKEN)입니다. appsettings.json을 확인하세요."
}
if ([string]::IsNullOrWhiteSpace($headers["Cookie"]) -or $headers["Cookie"] -match "REALTOR_LIST_COOKIE") {
    Write-Warning "Cookie가 비어 있거나 예시값(REALTOR_LIST_COOKIE)입니다. appsettings.json을 확인하세요."
}

Write-Host "요청 URL: $baseUrl$endpoint (쿼리 파라미터 생략, $realtorIdParam=$GroupId, page=$Page)"
Write-Host "요청 중..."

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $response = Invoke-WebRequest -Uri $url -Headers $headers -Method Get -TimeoutSec 20 -UseBasicParsing
} catch {
    Write-Error "요청 실패: $($_.Exception.Message)"
    if ($_.Exception.Response) {
        Write-Host "HTTP 상태: $($_.Exception.Response.StatusCode.value__)"
    }
    exit 1
}

Write-Host "HTTP 상태: $($response.StatusCode)"

$json = $response.Content | ConvertFrom-Json

# 앱의 FindArticleArray와 동일한 순서로 배열 탐색: root -> articleList/articles/list -> result.(articleList/articles/list)
function Find-ArticleArray($node) {
    if ($null -eq $node) { return $null }
    if ($node -is [System.Array]) { return $node }
    foreach ($name in @("articleList", "articles", "list")) {
        $prop = $node.PSObject.Properties[$name]
        if ($prop -and $prop.Value -is [System.Array]) { return $prop.Value }
    }
    $resultProp = $node.PSObject.Properties["result"]
    if ($resultProp) {
        if ($resultProp.Value -is [System.Array]) { return $resultProp.Value }
        foreach ($name in @("articleList", "articles", "list")) {
            $prop = $resultProp.Value.PSObject.Properties[$name]
            if ($prop -and $prop.Value -is [System.Array]) { return $prop.Value }
        }
    }
    return $null
}

# 항목 내부를 재귀 탐색해 complexNo/complexNumber/hscpNo 값과 경로를 찾는다 (앱의 FindTextRecursively 대응).
function Find-ComplexNo($node, [string]$path) {
    if ($null -eq $node) { return $null }
    if ($node -is [System.Array]) {
        for ($i = 0; $i -lt $node.Count; $i++) {
            $found = Find-ComplexNo $node[$i] "$path[$i]"
            if ($found) { return $found }
        }
        return $null
    }
    if ($node -isnot [System.Management.Automation.PSCustomObject]) { return $null }
    foreach ($name in @("complexNo", "complexNumber", "hscpNo")) {
        $prop = $node.PSObject.Properties[$name]
        if ($prop -and -not [string]::IsNullOrWhiteSpace([string]$prop.Value) -and
            $prop.Value -isnot [System.Management.Automation.PSCustomObject] -and
            $prop.Value -isnot [System.Array]) {
            return [pscustomobject]@{ Value = [string]$prop.Value; Path = "$path.$name".TrimStart('.') }
        }
    }
    foreach ($prop in $node.PSObject.Properties) {
        if ($prop.Value -is [System.Management.Automation.PSCustomObject] -or $prop.Value -is [System.Array]) {
            $found = Find-ComplexNo $prop.Value "$path.$($prop.Name)"
            if ($found) { return $found }
        }
    }
    return $null
}

$articles = Find-ArticleArray $json
if ($null -eq $articles) {
    Write-Warning "응답에서 매물 배열(articleList/articles/list)을 찾지 못했습니다. 원본 응답 앞부분을 출력합니다."
    ($response.Content).Substring(0, [Math]::Min(1000, $response.Content.Length)) | Write-Host
    exit 0
}

$total = $articles.Count
$topLevelCount = 0
$nestedCount = 0
$sample = @()

foreach ($item in $articles) {
    $topLevel = $null
    foreach ($name in @("complexNo", "complexNumber")) {
        $prop = $item.PSObject.Properties[$name]
        if ($prop -and -not [string]::IsNullOrWhiteSpace([string]$prop.Value)) {
            $topLevel = [string]$prop.Value
            break
        }
    }
    $nested = Find-ComplexNo $item ""
    if ($topLevel) { $topLevelCount++ }
    elseif ($nested) { $nestedCount++ }

    if ($sample.Count -lt 20) {
        $display = "(없음)"
        $pathDisplay = "-"
        if ($topLevel) { $display = $topLevel; $pathDisplay = "최상위" }
        elseif ($nested) { $display = $nested.Value; $pathDisplay = $nested.Path }
        $sample += [pscustomobject]@{
            articleNo   = $item.PSObject.Properties["articleNo"].Value
            articleName = $item.PSObject.Properties["articleName"].Value
            complexNo   = $display
            위치        = $pathDisplay
        }
    }
}

Write-Host ""
Write-Host "===== 결과 (단체 ID: $GroupId, $Page 페이지) ====="
Write-Host "매물 수: $total 건"
Write-Host "최상위 complexNo/complexNumber 있는 매물: $topLevelCount 건"
Write-Host "중첩 위치에만 있는 매물: $nestedCount 건"
Write-Host "단지번호 전혀 없는 매물: $($total - $topLevelCount - $nestedCount) 건"
Write-Host ""
Write-Host "샘플 (최대 20건):"
$sample | Format-Table -AutoSize | Out-String | Write-Host

if ($topLevelCount -eq 0 -and $nestedCount -eq 0 -and $total -gt 0) {
    Write-Warning "이 페이지의 매물에는 단지번호가 전혀 없습니다. 첫 번째 매물의 원본 JSON을 출력합니다."
    $articles[0] | ConvertTo-Json -Depth 6 | Write-Host
}
