Set WshShell = CreateObject("WScript.Shell")
WshShell.Run "powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""C:\Users\vm1\vosk-kimi\vosk_watchdog.ps1""", 0, False
Set WshShell = Nothing
