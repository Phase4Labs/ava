$path = ".\Program.cs"
$backup = ".\Program.cs.stage2d-dispatch-backup"

if (-not (Test-Path $path)) {
    throw "Program.cs not found in the current directory."
}

$text = Get-Content $path -Raw

if ($text -match [regex]::Escape("--stage2d-quality-selftest")) {
    Write-Host "Stage 2D self-test dispatch is already present. No change made."
    exit 0
}

$newline = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }

$pattern = '(?ms)^(?<indent>[ \t]*)if \(args\.Any\(a => string\.Equals\(a, "--stage2b4-gate-selftest", StringComparison\.OrdinalIgnoreCase\)\)\)\s*\{\s*Environment\.ExitCode = Stage2B4PromotionSelfTest\.Run\(\);\s*return;\s*\}'

$matches = [regex]::Matches($text, $pattern)

if ($matches.Count -ne 1) {
    throw "Expected exactly one Stage 2B.4 self-test dispatch anchor, found $($matches.Count). Program.cs was not changed."
}

$indent = $matches[0].Groups["indent"].Value

$replacement = @(
    $indent + 'if (args.Any(a => string.Equals(a, "--stage2b4-gate-selftest", StringComparison.OrdinalIgnoreCase)))'
    $indent + '{'
    $indent + '    Environment.ExitCode = Stage2B4PromotionSelfTest.Run();'
    $indent + '    return;'
    $indent + '}'
    ''
    $indent + '// Stage 2D quality-selection self-test. Pure in-memory; exits before'
    $indent + '// credentials, market data, DB, or LLM initialization.'
    $indent + 'if (args.Any(a => string.Equals(a, "--stage2d-quality-selftest", StringComparison.OrdinalIgnoreCase)))'
    $indent + '{'
    $indent + '    Environment.ExitCode = Stage2DQualitySelectionSelfTest.Run();'
    $indent + '    return;'
    $indent + '}'
) -join $newline

Copy-Item $path $backup -Force

$newText = [regex]::Replace($text, $pattern, [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $replacement }, 1)

[System.IO.File]::WriteAllText(
    (Resolve-Path $path),
    $newText,
    [System.Text.UTF8Encoding]::new($false)
)

Write-Host "Stage 2D top-level self-test dispatch inserted successfully."
Write-Host "Backup: $backup"
