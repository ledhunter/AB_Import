# Обходит Visary API в read-only режиме, собирает примеры ответов в .audit/raw/.
# Не модифицирует данные. Token берётся из .audit/.token.
#
# Использование:
#   pwsh -File scripts/audit-visary-api.ps1
#   pwsh -File scripts/audit-visary-api.ps1 -Mnemonics constructionsite,room

[CmdletBinding()]
param(
    [string[]] $Mnemonics,
    [string]   $BaseUrl  = 'https://isup-alfa-test.k8s.npc.ba',
    [string]   $TokenFile = '.audit/.token',
    [string]   $OutDir    = '.audit/raw'
)

$ErrorActionPreference = 'Stop'

$token = (Get-Content $TokenFile -Raw).Trim()
$headers = @{
    Authorization = "Bearer $token"
    Accept        = 'application/json'
}

# Все мнемоники, упомянутые в текущей библиотеке + кандидаты "пошире" из observed UI.
$allKnown = @(
    'constructionproject',
    'constructionsite',
    'constructionsection',
    'constructionsiteindicator',
    'constructionsiteindicatorvalue',
    'room',
    'cadastralarea',
    'percentbet',
    'shareagreement',
    'deal',
    'organization',
    # кандидаты — попробуем вслепую, ошибки залогируем
    'constructionprojectcalculated',
    'projectparameter',
    'siteratesprices',
    'checkpoint',
    'checklist',
    'escrowaccount',
    'roomkind',
    'roompurpose',
    'town',
    'region',
    'roomcategory',
    'parkingplacetype',
    'estateclass',
    'buildingmaterial',
    'finishingmaterial',
    'currency',
    'projecttype',
    'projectstage',
    'projectphase',
    'inflationcalcmethod',
    'developer',
    'developergroup'
)
$targets = if ($Mnemonics) { $Mnemonics } else { $allKnown }

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

$summary = @()

function Invoke-VisaryListView {
    param([string] $Mnemonic, [int] $PageSize = 5)
    $body = @{
        Mnemonic    = $Mnemonic
        PageSkip    = 0
        PageSize    = $PageSize
        Columns     = @('ID')
        SearchPhrase = $null
        Sorts       = 'null'
        Hidden      = $false
        Summaries   = @()
    } | ConvertTo-Json -Depth 5
    Invoke-RestMethod -Uri "$BaseUrl/api/visary/listview/$Mnemonic" `
        -Method Post -Headers $headers -ContentType 'application/json' -Body $body -TimeoutSec 30
}

function Invoke-VisaryGet {
    param([string] $Mnemonic, [int] $Id)
    Invoke-RestMethod -Uri "$BaseUrl/api/visary/crud/$Mnemonic/$Id" `
        -Method Get -Headers $headers -TimeoutSec 30
}

foreach ($m in $targets) {
    $row = [pscustomobject]@{
        Mnemonic       = $m
        ListViewStatus = ''
        Total          = $null
        SampleId       = $null
        GetStatus      = ''
        TopFields      = $null
        Note           = ''
    }
    try {
        $lv = Invoke-VisaryListView -Mnemonic $m -PageSize 5
        $row.ListViewStatus = 'OK'
        $row.Total          = $lv.Total
        $row.SampleId       = if ($lv.Data -and $lv.Data.Count -gt 0) { $lv.Data[0].ID } else { $null }
    } catch {
        $code = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { -1 }
        $row.ListViewStatus = "ERR $code"
    }

    if ($row.SampleId) {
        try {
            $entity = Invoke-VisaryGet -Mnemonic $m -Id $row.SampleId
            $row.GetStatus = 'OK'
            $row.TopFields = $entity.PSObject.Properties.Name.Count
            # UTF-8 БЕЗ BOM — иначе при последующем импорте через `docker exec -i psql`
            # консоль может портить отдельные не-ASCII символы (наблюдалось:
            # 'C' в InsuranceCompanyAccreditation становилось '?').
            $json = $entity | ConvertTo-Json -Depth 10
            [System.IO.File]::WriteAllText((Join-Path $OutDir "$m.json"), $json, [System.Text.UTF8Encoding]::new($false))
        } catch {
            $code = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { -1 }
            $row.GetStatus = "ERR $code"
        }
    } else {
        $row.GetStatus = 'skip'
    }
    $summary += $row
    Write-Host ("{0,-35} listview={1,-8} total={2,-6} id={3,-8} get={4,-7} fields={5}" -f `
        $m, $row.ListViewStatus, ($row.Total ?? '-'), ($row.SampleId ?? '-'), $row.GetStatus, ($row.TopFields ?? '-'))
}

$summary | Export-Csv -Path (Join-Path $OutDir '_summary.csv') -NoTypeInformation -Encoding utf8
Write-Host ""
Write-Host "Summary saved to $OutDir/_summary.csv"
Write-Host "Per-entity JSONs saved to $OutDir/<mnemonic>.json"
