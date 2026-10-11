function Assert-ReleaseBinaryClean([string]$Path, [string]$ProjectRoot) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    # Native strings may start at either byte alignment. Do not print any match.
    $strings = @([Text.Encoding]::UTF8.GetString($bytes), [Text.Encoding]::Unicode.GetString($bytes))
    if ($bytes.Length -gt 1) { $strings += [Text.Encoding]::Unicode.GetString($bytes,1,$bytes.Length-1) }
    foreach ($text in $strings) {
        if ($text -match '(?i)[a-z]:[\\/][^\x00\r\n"<>|]*\.(cpp|hpp|h|cs|pdb)\b') { throw 'Release binary contains an absolute build/source path.' }
        foreach ($prefix in @($ProjectRoot,$env:USERPROFILE)) {
            if ($prefix -and ($text.IndexOf($prefix,[StringComparison]::OrdinalIgnoreCase) -ge 0 -or $text.IndexOf($prefix.Replace('\','/'),[StringComparison]::OrdinalIgnoreCase) -ge 0)) { throw 'Release binary contains a local workspace/profile path.' }
        }
    }
}
