# End-to-end check with a throwaway fake app. Touches only things it creates itself and removes them at the end.
param([string]$Exe, [string]$Work)
$ErrorActionPreference = 'Stop'
$uninstall = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\PortabilizerSmoke'
$vendor = 'HKCU:\Software\SmokePub'
$key = "$vendor\Smoke App"
$data = "$env:APPDATA\Smoke App"
$state = "$env:LOCALAPPDATA\Portabilizer"
$pass = 0; $fail = 0
function Check($label, $ok) { if ($ok) { $script:pass++; "  ok   $label" } else { $script:fail++; "  FAIL $label" } }

function Cleanup {
    foreach ($k in $uninstall, $vendor) { if (Test-Path $k) { Remove-Item $k -Recurse -Force } }
    if (Test-Path "$data.portabilizer-aside") { Remove-Item "$data.portabilizer-aside" -Recurse -Force }
    if (Test-Path $data) {
        if ((Get-Item $data).Attributes -band [IO.FileAttributes]::ReparsePoint) { (Get-Item $data).Delete() } else { Remove-Item $data -Recurse -Force }
    }
    if (Test-Path $state) { Get-ChildItem $state -Directory -Filter 'Smoke-App*' | Remove-Item -Recurse -Force }
}

Cleanup
if (Test-Path $Work) { Remove-Item $Work -Recurse -Force }
$inst = "$Work\install\SmokeApp"
New-Item -ItemType Directory -Force $inst, $data | Out-Null
Copy-Item C:\Windows\System32\cmd.exe "$inst\SmokeApp.exe"
New-Item $uninstall -Force | Out-Null
Set-ItemProperty $uninstall DisplayName 'Smoke App 1.2'
Set-ItemProperty $uninstall DisplayVersion '1.2'
Set-ItemProperty $uninstall Publisher 'SmokePub Inc'
Set-ItemProperty $uninstall InstallLocation $inst
New-Item $key -Force | Out-Null
Set-ItemProperty $key Greeting 'from-capture'
Set-ItemProperty $key InstallDir $inst
Set-ItemProperty $key HomeDir "$env:USERPROFILE\SmokeDocs"
'from-capture' | Set-Content "$data\settings.txt" -Encoding ascii

@'
@echo off
> "%~dp0result.txt" (
  echo SETTINGS
  type "%APPDATA%\Smoke App\settings.txt"
  echo REGISTRY
  reg query "HKCU\Software\SmokePub\Smoke App"
  echo CWD %CD%
)
>> "%APPDATA%\Smoke App\settings.txt" echo changed-by-run
reg add "HKCU\Software\SmokePub\Smoke App" /v Added /d yes /f > nul
'@ | Set-Content "$Work\probe.cmd" -Encoding ascii
'@ping -n 25 127.0.0.1 > nul' | Set-Content "$Work\slow.cmd" -Encoding ascii

"== capture"
$p = Start-Process $Exe -ArgumentList "--capture `"Smoke App 1.2`" `"$Work\out`"" -PassThru -Wait
Check "capture exit 0 (got $($p.ExitCode))" ($p.ExitCode -eq 0)
$launcher = "$Work\out\Smoke App 1.2 Portable.exe"
if (-not (Test-Path $launcher)) { $launcher = (Get-ChildItem "$Work\out" -Filter '*.exe' | Select-Object -First 1).FullName }
Check "launcher exists: $(Split-Path $launcher -Leaf)" (Test-Path $launcher)
Check "app copied" (Test-Path "$Work\out\App\SmokeApp.exe")
Check "settings folder copied" (Test-Path "$Work\out\Data\Folders\0\settings.txt")
$reg = Get-Content "$Work\out\Data\Registry\0.reg" -Encoding Unicode -Raw
Check "install path tokenised" ($reg -match '\{\{APP\}\}')
Check "profile path tokenised" ($reg -match '\{\{PROFILE\}\}\\\\SmokeDocs')
Check "no absolute install path left" (-not $reg.Contains($inst.Replace('\','\\')))
Check "original app untouched" ((Get-ItemProperty $key).Greeting -eq 'from-capture')
"manifest:"; Get-Content "$Work\out\portable.json"

"== other PC: nothing installed"
Remove-Item $uninstall -Recurse -Force; Remove-Item $vendor -Recurse -Force; Remove-Item $data -Recurse -Force
Rename-Item $inst 'gone'
Start-Process $launcher -ArgumentList "/c `"$Work\probe.cmd`"" -Wait
$r = Get-Content "$Work\result.txt" -Raw
Check "app saw portable settings file" ($r -match 'from-capture')
Check "app saw portable registry" ($r -match 'Greeting\s+REG_SZ\s+from-capture')
Check "registry path points at package" ($r.Contains("$Work\out\App"))
Check "profile path rewritten" ($r.Contains("$env:USERPROFILE\SmokeDocs"))
Check "ran from the app folder" ($r -match [regex]::Escape("CWD $Work\out\App"))
Check "PC clean: no settings folder" (-not (Test-Path $data))
Check "PC clean: no vendor key" (-not (Test-Path $vendor))
Check "PC clean: install link gone" (-not (Test-Path $inst))
Check "file change kept in package" ((Get-Content "$Work\out\Data\Folders\0\settings.txt" -Raw) -match 'changed-by-run')
Check "registry change kept in package" ((Get-Content "$Work\out\Data\Registry\0.reg" -Encoding Unicode -Raw) -match '"Added"="yes"')
Check "journal removed" (-not (Get-ChildItem $state -Recurse -Filter session.json -ErrorAction SilentlyContinue | Where-Object FullName -match 'Smoke-App'))

"== this PC has its own copy"
New-Item -ItemType Directory -Force $data | Out-Null
'HOST-FILE' | Set-Content "$data\settings.txt" -Encoding ascii
New-Item $key -Force | Out-Null
Set-ItemProperty $key Greeting 'HOST-REG'
Start-Process $launcher -ArgumentList "/c `"$Work\probe.cmd`"" -Wait
$r = Get-Content "$Work\result.txt" -Raw
Check "app saw portable data, not host" (($r -notmatch 'HOST-') -and ($r -match 'from-capture'))
Check "host file restored" ((Get-Content "$data\settings.txt" -Raw) -match '^HOST-FILE\s*$')
Check "host registry restored" ((Get-ItemProperty $key).Greeting -eq 'HOST-REG')
Check "host key has no portable values" ($null -eq (Get-ItemProperty $key).Added)
Check "no aside folder left" (-not (Test-Path "$data.portabilizer-aside"))

"== launcher killed mid-run"
Start-Process $launcher -ArgumentList "/c `"$Work\slow.cmd`""
Start-Sleep -Seconds 5
$item = Get-Item $data -Force
Check "during run: folder is a link" ([bool]($item.Attributes -band [IO.FileAttributes]::ReparsePoint))
Check "during run: portable registry live" ((Get-ItemProperty $key).Greeting -eq 'from-capture')
Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($launcher)) | Stop-Process -Force
Get-Process SmokeApp -ErrorAction SilentlyContinue | Stop-Process -Force
Get-CimInstance Win32_Process -Filter "Name='PING.EXE'" | Where-Object CommandLine -match '-n 25' | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Start-Sleep -Seconds 1
Check "after kill: PC left in portable state" (-not ((Get-ItemProperty $key).Greeting -eq 'HOST-REG'))
Start-Process $launcher -ArgumentList "/c `"$Work\probe.cmd`"" -Wait
Check "next launch: host file restored" ((Get-Content "$data\settings.txt" -Raw) -match '^HOST-FILE\s*$')
Check "next launch: host registry restored" ((Get-ItemProperty $key).Greeting -eq 'HOST-REG')
Check "next launch: no aside folder" (-not (Test-Path "$data.portabilizer-aside"))

"== package moved to a path with spaces"
Remove-Item $vendor -Recurse -Force; Remove-Item $data -Recurse -Force
Move-Item "$Work\out" "$Work\moved here"
$launcher = (Get-ChildItem "$Work\moved here" -Filter '*.exe' | Select-Object -First 1).FullName
Start-Process $launcher -ArgumentList "/c `"$Work\probe.cmd`"" -Wait
$r = Get-Content "$Work\result.txt" -Raw
Check "registry follows the new location" ($r.Contains("$Work\moved here\App"))
Check "edits from earlier runs survived" ($r -match 'changed-by-run')

"== cleanup"
Cleanup
Check "all test keys gone" (-not ((Test-Path $uninstall) -or (Test-Path $vendor)))
Check "test folder gone" (-not (Test-Path $data))
"RESULT: $pass passed, $fail failed"
