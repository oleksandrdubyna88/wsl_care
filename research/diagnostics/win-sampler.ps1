# READ-ONLY (S7b experiment, 2026-10-09): every 250 ms for 120 s, the creds-mcp.exe children of the given wsl.exe pid (2026-10-10 rerun) by pid and
# creation time; prints the set once, then every change with its UTC time. Nothing is stopped or changed.
$parent = [int]$args[0]
$deadline = [DateTime]::UtcNow.AddSeconds(120)
$last = $null
while ([DateTime]::UtcNow -lt $deadline) {
    $now = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
    $set = Get-CimInstance Win32_Process -Filter "Name = 'creds-mcp.exe' AND ParentProcessId = $parent" -Property ProcessId, CreationDate |
        ForEach-Object { "$($_.ProcessId)@$($_.CreationDate.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.ffffffZ'))" } | Sort-Object
    $key = $set -join ' '
    if ($null -eq $last) { "$now START count=$($set.Count): $key" }
    elseif ($key -ne $last) {
        $gone = $last.Split(' ') | Where-Object { $_ -and ($set -notcontains $_) }
        $new = $set | Where-Object { $last.Split(' ') -notcontains $_ }
        "$now CHANGE count=$($set.Count) gone=[$($gone -join ' ')] new=[$($new -join ' ')]"
    }
    $last = $key
    Start-Sleep -Milliseconds 250
}
"$([DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')) END count=$($set.Count)"
