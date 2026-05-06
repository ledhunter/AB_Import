# Применяет visary_api_schema.sql и заполняет таблицы из .audit/raw/*.json.
# Postgres хост — контейнер kilo-import-pg-service, БД import_service_db.
# Импорт идемпотентный: TRUNCATE + INSERT, повторный запуск перезаписывает снэпшот.

[CmdletBinding()]
param(
    [string] $RawDir       = '.audit/raw',
    [string] $SchemaFile   = 'scripts/visary_api_schema.sql',
    [string] $PgContainer  = 'kilo-import-pg-service',
    [string] $PgDb         = 'import_service_db',
    [string] $PgUser       = 'import_service'
)

$ErrorActionPreference = 'Stop'

# Мнемоники, реально используемые ICrudClient/IListViewClient.
$libMnemonics = @(
  'constructionproject','constructionsite','constructionsection',
  'constructionsiteindicator','constructionsiteindicatorvalue',
  'room','cadastralarea','percentbet','shareagreement','deal','organization'
)

function Sql-Quote([string] $s) {
    if ($null -eq $s) { return 'NULL' }
    return "'" + ($s -replace "'", "''") + "'"
}

function Get-DataType($v) {
    if ($null -eq $v) { return 'null' }
    if ($v -is [bool])    { return 'boolean' }
    if ($v -is [int])     { return 'int' }
    if ($v -is [long])    { return 'long' }
    if ($v -is [double] -or $v -is [decimal] -or $v -is [single]) { return 'double' }
    if ($v -is [string]) {
        # ISO-8601 датавремя из Visary (с 'Z' или +00:00)
        if ($v -match '^\d{4}-\d{2}-\d{2}(T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+\-]\d{2}:\d{2}))?$') { return 'datetime' }
        return 'string'
    }
    if ($v -is [array])   { return 'array' }
    # PSCustomObject — может быть VisaryRef или произвольный вложенный объект
    if ($v.PSObject.Properties.Name -contains 'ID' -and $v.PSObject.Properties.Name -contains 'Title') { return 'ref' }
    return 'object'
}

# Рекурсивно обходит JSON-объект и накапливает плоский список полей.
function Walk-Fields {
    param($node, [string] $prefix, [System.Collections.ArrayList] $sink)

    foreach ($prop in $node.PSObject.Properties) {
        $name = $prop.Name
        $val  = $prop.Value
        $path = if ($prefix) { "$prefix.$name" } else { $name }
        $type = Get-DataType $val

        # Префиксы A_O2O_/A_O2M_/A_M2M_ — служебные ассоциации Visary
        $location = if ($name -match '^A_(O2O|O2M|M2M)_') { 'association' } else { 'response_body' }

        $sample = $null
        if ($type -in @('string','int','long','double','boolean','datetime')) {
            $sample = "$val"
            if ($sample.Length -gt 200) { $sample = $sample.Substring(0,200) + '…' }
        }

        [void] $sink.Add([pscustomobject]@{
            Path     = $path
            Type     = $type
            Nullable = ($null -eq $val)
            Sample   = $sample
            Location = $location
        })

        if ($type -eq 'ref') {
            # Раскрываем VisaryRef, чтобы его поля (Title, ID, Hidden, RowVersion) тоже попали в выборку
            Walk-Fields -node $val -prefix $path -sink $sink
        } elseif ($type -eq 'object' -and $val -is [psobject]) {
            Walk-Fields -node $val -prefix $path -sink $sink
        }
    }
}

# ─── Сборка SQL ───
$sb = New-Object System.Text.StringBuilder
[void] $sb.AppendLine('SET client_min_messages = WARNING;')
[void] $sb.AppendLine('TRUNCATE visary_api.fields, visary_api.endpoints, visary_api.captures, visary_api.entities RESTART IDENTITY CASCADE;')

$jsonFiles = Get-ChildItem -Path $RawDir -Filter '*.json' | Sort-Object Name
"Found $($jsonFiles.Count) entity dumps in $RawDir"

foreach ($f in $jsonFiles) {
    $mnemonic = [System.IO.Path]::GetFileNameWithoutExtension($f.Name)
    if ($mnemonic.StartsWith('_')) { continue }
    $inLib = $libMnemonics -contains $mnemonic

    $entity = Get-Content $f.FullName -Raw | ConvertFrom-Json
    $sampleId = $entity.ID

    # entities
    [void] $sb.AppendLine("INSERT INTO visary_api.entities (mnemonic, is_in_library) VALUES ($(Sql-Quote $mnemonic), $($inLib.ToString().ToLower()));")

    # endpoints — стандартный набор для всех мнемоник
    $endpoints = @(
        @{ op='get_by_id'; method='GET';   url="/api/visary/crud/$mnemonic/{id}" },
        @{ op='list';      method='POST';  url="/api/visary/listview/$mnemonic" },
        @{ op='create';    method='POST';  url="/api/visary/crud/$mnemonic" },
        @{ op='patch';     method='PATCH'; url="/api/visary/crud/$mnemonic/{id}?forceUpdate=false" }
    )
    foreach ($ep in $endpoints) {
        [void] $sb.AppendLine("INSERT INTO visary_api.endpoints (mnemonic, operation, http_method, url_template) VALUES ($(Sql-Quote $mnemonic), $(Sql-Quote $ep.op), $(Sql-Quote $ep.method), $(Sql-Quote $ep.url));")
    }

    # fields — обход дерева
    $fields = New-Object System.Collections.ArrayList
    Walk-Fields -node $entity -prefix '' -sink $fields
    foreach ($fld in $fields) {
        $sql = "INSERT INTO visary_api.fields (mnemonic, location, path, data_type, is_nullable, sample_value) VALUES ({0}, {1}, {2}, {3}, {4}, {5}) ON CONFLICT DO NOTHING;" -f `
            (Sql-Quote $mnemonic), (Sql-Quote $fld.Location), (Sql-Quote $fld.Path), (Sql-Quote $fld.Type), $fld.Nullable.ToString().ToLower(), (Sql-Quote $fld.Sample)
        [void] $sb.AppendLine($sql)
    }

    # captures — оригинальный JSON
    $jsonRaw = (Get-Content $f.FullName -Raw)
    [void] $sb.AppendLine("INSERT INTO visary_api.captures (mnemonic, operation, sample_id, response_body) VALUES ($(Sql-Quote $mnemonic), 'get_by_id', $($sampleId), $(Sql-Quote $jsonRaw)::jsonb);")

    "  + $mnemonic ($($fields.Count) fields, sample id=$sampleId)"
}

# ─── Применение ───
$tmpSql = Join-Path $env:TEMP "visary_api_import_$(Get-Random).sql"
# В файл сначала кладём DDL, потом сгенерированные INSERT'ы.
Get-Content $SchemaFile -Raw | Out-File -FilePath $tmpSql -Encoding utf8
Add-Content -Path $tmpSql -Value $sb.ToString() -Encoding utf8

"Applying SQL via docker exec $PgContainer..."
Get-Content $tmpSql -Raw | docker exec -i $PgContainer psql -U $PgUser -d $PgDb -v ON_ERROR_STOP=1
Remove-Item $tmpSql

"Done. Quick check:"
docker exec $PgContainer psql -U $PgUser -d $PgDb -c "SELECT mnemonic, is_in_library, (SELECT COUNT(*) FROM visary_api.fields f WHERE f.mnemonic = e.mnemonic) AS field_count FROM visary_api.entities e ORDER BY is_in_library DESC, mnemonic;"
