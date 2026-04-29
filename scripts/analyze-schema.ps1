$ErrorActionPreference = 'Stop'
$rows = Import-Csv 'Context\schema_export.csv'
Write-Host "Total column rows: $($rows.Count)"
$tables = $rows | Select-Object table_schema, table_name -Unique
Write-Host "Unique tables: $($tables.Count)"
Write-Host ''
Write-Host 'Tables per schema:'
$tables | Group-Object table_schema | Sort-Object Count -Descending | ForEach-Object {
    Write-Host ("  {0,-15} {1}" -f $_.Name, $_.Count)
}
Write-Host ''
Write-Host 'Top 25 data types:'
$rows | Group-Object data_type | Sort-Object Count -Descending | Select-Object -First 25 | ForEach-Object {
    Write-Host ("  {0,-25} {1}" -f $_.Name, $_.Count)
}
Write-Host ''
Write-Host 'Constraint types:'
$rows | Where-Object { $_.constraint_type } | Group-Object constraint_type | Sort-Object Count -Descending | ForEach-Object {
    Write-Host ("  {0,-15} {1}" -f $_.Name, $_.Count)
}
