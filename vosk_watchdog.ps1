# Watchdog: restarts VoskKimiBridge if the process is not running.
# ASCII-only on purpose: Windows PowerShell 5.1 misreads UTF-8 files
# without BOM and breaks on Cyrillic (parse error, script never runs).
$exe = 'C:\Users\vm1\vosk-kimi\VoskKimiBridge\bin\Release\net8.0-windows\VoskKimiBridge.exe'
$log = 'C:\Users\vm1\vosk-kimi\watchdog.log'
while ($true) {
    if (-not (Get-Process VoskKimiBridge -ErrorAction SilentlyContinue)) {
        Add-Content -Path $log -Value ("{0} bridge not running - restarting" -f (Get-Date -Format 'dd.MM.yyyy HH:mm:ss'))
        Start-Process -FilePath $exe
    }
    Start-Sleep -Seconds 30
}
