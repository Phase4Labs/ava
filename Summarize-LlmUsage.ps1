param(
    [string]$Path = ".\llm_usage.jsonl"
)

if (-not (Test-Path $Path)) {
    Write-Error "Usage log not found: $Path"
    exit 1
}

$rows = Get-Content $Path |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    ForEach-Object {
        try { $_ | ConvertFrom-Json }
        catch { Write-Warning "Skipping invalid JSONL row" }
    } |
    Where-Object { $_ -ne $null }

if (-not $rows) {
    Write-Host "No usage rows found."
    exit 0
}

Write-Host "`nDAILY TOTALS" -ForegroundColor Cyan
$rows |
    Group-Object { ([datetime]$_.timestamp_utc).ToString('yyyy-MM-dd') } |
    ForEach-Object {
        [pscustomobject]@{
            Date          = $_.Name
            Calls         = $_.Count
            InputTokens   = ($_.Group | Measure-Object input_tokens -Sum).Sum
            CachedTokens  = ($_.Group | Measure-Object cached_input_tokens -Sum).Sum
            OutputTokens  = ($_.Group | Measure-Object output_tokens -Sum).Sum
            Reasoning     = ($_.Group | Measure-Object reasoning_tokens -Sum).Sum
            EstimatedUSD  = [math]::Round((($_.Group | Measure-Object estimated_cost_usd -Sum).Sum), 4)
            AvgLatencyMs  = [math]::Round((($_.Group | Measure-Object latency_ms -Average).Average), 0)
        }
    } | Format-Table -AutoSize

Write-Host "`nBY CALL TYPE" -ForegroundColor Cyan
$rows |
    Group-Object call_type |
    ForEach-Object {
        [pscustomobject]@{
            CallType      = $_.Name
            Calls         = $_.Count
            InputTokens   = ($_.Group | Measure-Object input_tokens -Sum).Sum
            OutputTokens  = ($_.Group | Measure-Object output_tokens -Sum).Sum
            EstimatedUSD  = [math]::Round((($_.Group | Measure-Object estimated_cost_usd -Sum).Sum), 4)
        }
    } | Sort-Object EstimatedUSD -Descending | Format-Table -AutoSize

Write-Host "`nBY TICKER" -ForegroundColor Cyan
$rows |
    Group-Object ticker |
    ForEach-Object {
        [pscustomobject]@{
            Ticker        = $_.Name
            Calls         = $_.Count
            InputTokens   = ($_.Group | Measure-Object input_tokens -Sum).Sum
            OutputTokens  = ($_.Group | Measure-Object output_tokens -Sum).Sum
            EstimatedUSD  = [math]::Round((($_.Group | Measure-Object estimated_cost_usd -Sum).Sum), 4)
        }
    } | Sort-Object EstimatedUSD -Descending | Format-Table -AutoSize
