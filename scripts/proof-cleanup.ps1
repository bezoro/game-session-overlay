function Remove-ProofDirectory {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$LiteralPath)

    # Velopack may still remove its own temporary tree after uninstall returns.
    # Absence is success; a child disappearing during enumeration is retryable.
    # Other failures remain errors, and filesystem retries have a fixed deadline.
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    while (Test-Path -LiteralPath $LiteralPath) {
        try {
            Remove-Item -LiteralPath $LiteralPath -Recurse -Force -ErrorAction Stop
        }
        catch {
            if (!(Test-Path -LiteralPath $LiteralPath)) {
                return
            }
            $cause = $_.Exception.GetBaseException()
            $filesystemRace = $cause -is [IO.IOException] -or
            $cause -is [System.Management.Automation.ItemNotFoundException] -or
            $_.CategoryInfo.Category -eq [System.Management.Automation.ErrorCategory]::ObjectNotFound -or
            ($cause -is [ComponentModel.Win32Exception] -and $cause.NativeErrorCode -in @(2, 3))
            if (!$filesystemRace -or $elapsed.ElapsedMilliseconds -ge 5000) {
                throw
            }
            Start-Sleep -Milliseconds 100
        }
    }
}
