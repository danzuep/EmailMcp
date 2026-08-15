$ErrorActionPreference = 'Stop'

$env:IMAP_HOST = 'localhost:143'
$env:IMAP_USER = 'test'
$env:IMAP_PASSWORD = 'test'

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = 'dotnet'
$psi.Arguments = 'run --file "./EmailMcp.cs"'
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true

$p = [System.Diagnostics.Process]::Start($psi)

function Send-Json {
    param($obj)
    $json = $obj | ConvertTo-Json -Compress
    $p.StandardInput.WriteLine($json)
}

function Read-Json {
    param($timeoutSec = 10)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
        if ($p.StandardOutput.Peek() -ge 0) {
            $line = $p.StandardOutput.ReadLine()
            if ($line) { return $line }
        }
        Start-Sleep -Milliseconds 100
    }
    return $null
}

try {
    Send-Json @{
        jsonrpc = '2.0'
        id = 1
        method = 'initialize'
        params = @{
            protocolVersion = '2024-11-05'
            capabilities = @{}
            clientInfo = @{ name = 'test-client'; version = '1.0' }
        }
    }

    $line = Read-Json -timeoutSec 10
    Write-Host 'Initialize response:' $line

    Send-Json @{ jsonrpc = '2.0'; method = 'notifications/initialized' }
    Start-Sleep -Milliseconds 300

    Send-Json @{
        jsonrpc = '2.0'
        id = 2
        method = 'tools/call'
        params = @{
            name = 'read_email'
            arguments = @{ maxResults = 1 }
        }
    }

    $line = Read-Json -timeoutSec 15
    Write-Host 'read_email response:' $line

    if (-not $line) {
        Write-Host 'No response from server within timeout.'
    }
}
finally {
    $p.StandardInput.Close()
    if (!$p.HasExited) {
        $p.Kill()
        $p.WaitForExit(2000)
    }
}
