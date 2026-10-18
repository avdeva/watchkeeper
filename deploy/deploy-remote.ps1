<#
.SYNOPSIS
  Installs Watchkeeper on other servers from this computer, over PowerShell remoting (WinRM).

.DESCRIPTION
  For each computer: copies Watchkeeper.exe (and the configuration file, if given) to a new folder in
  the remote TEMP folder, checks that the copy is byte for byte the program checked here (SHA-256),
  runs "Watchkeeper.exe --deploy" there (writes the configuration, encrypts its passwords for that
  computer, installs and starts the service), shows the end of its deploy log, and deletes the copied
  files again, also when something failed.
  Needs administrator rights on the target servers and PowerShell remoting enabled on them
  (Enable-PSRemoting, usually on by default on Windows Server).

  -Sha256: the SHA-256 published with the release (SHA256SUMS). The local Watchkeeper.exe must match it,
  or nothing is deployed. Without it, the hash of the local file is shown and used for the remote check.

.EXAMPLE
  .\deploy-remote.ps1 -ComputerName APP1,APP2 -Config .\config-watchdog.json -Sha256 3F2A...

.EXAMPLE
  .\deploy-remote.ps1 -ComputerName APP3 -StarterProfile watchdog -Credential (Get-Credential)
#>
param(
    [Parameter(Mandatory = $true)][string[]]$ComputerName,
    [string]$Config,
    [ValidateSet('studio', 'watchdog')][string]$StarterProfile = 'watchdog',
    [System.Management.Automation.PSCredential]$Credential,
    [string]$Sha256,
    [switch]$AllowUntrustedPaths
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $here 'Watchkeeper.exe'
if (-not (Test-Path $exe)) { throw "Watchkeeper.exe not found next to this script ($here)." }

$hash = (Get-FileHash -Path $exe -Algorithm SHA256).Hash
if ($Sha256) {
    if ($hash -ne $Sha256.Trim().ToUpperInvariant()) {
        throw "Watchkeeper.exe does not match the SHA-256 given ($Sha256); this file is $hash. Nothing was deployed."
    }
    Write-Host "Watchkeeper.exe matches the published SHA-256." -ForegroundColor Green
}
else {
    Write-Host "Watchkeeper.exe SHA-256: $hash" -ForegroundColor Yellow
    Write-Host "Compare it with SHA256SUMS of the release, or pass -Sha256 to have it checked." -ForegroundColor Yellow
}

if ($Config) {
    if (-not (Test-Path $Config)) { throw "Configuration file not found: $Config" }
    $Config = (Resolve-Path $Config).Path
    # passwords, tokens and secret addresses typed in as plain text (they are encrypted on each server). Webhook
    # addresses are secrets too ("url" also matches other addresses, so this may count a few too many).
    $text = Get-Content -Raw -Path $Config
    $plain = [regex]::Matches($text, '"(smtp_password|jira_token|secret|password|health_password|headers|proxy_url|url)"\s*:\s*"(?!dpapi|keep:|")')
    if ($plain.Count -gt 0) {
        Write-Host "Note: $Config holds up to $($plain.Count) password(s), token(s) or webhook address(es) in plain text. It is sent over the encrypted" -ForegroundColor Yellow
        Write-Host "remoting connection and removed from each server afterwards; delete it here when all servers are done." -ForegroundColor Yellow
    }
}

foreach ($computer in $ComputerName) {
    Write-Host "== $computer" -ForegroundColor Cyan
    $session = $null
    $dir = $null
    try {
        $sessionArgs = @{ ComputerName = $computer }
        if ($Credential) { $sessionArgs.Credential = $Credential }
        $session = New-PSSession @sessionArgs
        # a new folder with a random name in the connecting account's TEMP (only that account and administrators can open it)
        $dir = Invoke-Command -Session $session -ScriptBlock {
            $d = Join-Path $env:TEMP ('Watchkeeper-deploy-' + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $d | Out-Null
            $d
        }
        Copy-Item -Path $exe -Destination $dir -ToSession $session -Force
        $remoteArgs = @('--deploy')
        if ($Config) {
            Copy-Item -Path $Config -Destination $dir -ToSession $session -Force
            $remoteArgs += @('--config', (Join-Path $dir (Split-Path -Leaf $Config)))
        }
        else {
            $remoteArgs += @('--profile', $StarterProfile)
        }
        if ($AllowUntrustedPaths) { $remoteArgs += '--allow-untrusted-paths' }
        $lines = Invoke-Command -Session $session -ArgumentList $dir, $remoteArgs, $hash -ScriptBlock {
            param($dir, $remoteArgs, $hash)
            $target = Join-Path $dir 'Watchkeeper.exe'
            $got = (Get-FileHash -Path $target -Algorithm SHA256).Hash
            if ($got -ne $hash) { throw "the copy on this server does not match (SHA-256 $got); not run" }
            $quoted = $remoteArgs | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }
            $p = Start-Process -FilePath $target -ArgumentList ($quoted -join ' ') -Wait -PassThru
            $log = Join-Path $env:ProgramData 'Watchkeeper\deploy.log'
            if (Test-Path $log) { Get-Content -Path $log -Tail 12 }
            'exit code ' + $p.ExitCode
        }
        foreach ($l in $lines) { Write-Host "   $l" }
    }
    catch {
        Write-Host "   failed: $($_.Exception.Message)" -ForegroundColor Red
    }
    finally {
        if ($session) {
            if ($dir) {
                try {
                    Invoke-Command -Session $session -ArgumentList $dir -ScriptBlock {
                        param($dir)
                        Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction Stop
                    }
                    Write-Host "   removed the copied files from $computer"
                }
                catch { Write-Host "   could not remove $dir on ${computer}: $($_.Exception.Message); delete it by hand" -ForegroundColor Red }
            }
            Remove-PSSession $session
        }
    }
}
