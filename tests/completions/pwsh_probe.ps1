# Functional probe: dot-source the generated script, then drive the
# completion engine the way PSReadLine would — CompleteInput resolves the
# Register-ArgumentCompleter -Native block in this session.
. /completions/fedit.ps1

$script:fail = $false

function Assert-Completion([string]$label, [string]$line, [string[]]$expected, [string[]]$absent = @()) {
    $res = [System.Management.Automation.CommandCompletion]::CompleteInput($line, $line.Length, $null)
    $texts = @($res.CompletionMatches | ForEach-Object CompletionText)
    $missing = @($expected | Where-Object { $w = $_; -not ($texts | Where-Object { $_ -like "*$w*" }) })
    $leaked = @($absent | Where-Object { $w = $_; ($texts | Where-Object { $_ -like "*$w*" }) })
    if ($missing.Count -eq 0 -and $leaked.Count -eq 0) {
        Write-Output "  ${label}: OK ($($texts.Count) candidates)"
    } else {
        Write-Output "  ${label}: FAILED missing=[$($missing -join ' ')] leaked=[$($leaked -join ' ')]"
        Write-Output "    got: $($texts -join ' ')"
        $script:fail = $true
    }
}

Assert-Completion 'subcommands' 'fedit ' @('plugins', 'completions', 'keybinds', 'themes')
Assert-Completion 'flags' 'fedit --' @('--help', '--version', '--log')
Assert-Completion 'root file' 'fedit /tmp/feditc/som' @('somefile.txt')
Assert-Completion 'dynamic (plugins remove)' 'fedit plugins remove ' @('alpha', 'beta')

if ($script:fail) { exit 1 } else { exit 0 }
