# End-to-end check with a throwaway fake app. Touches only things it creates itself and removes them at the end.
param([string]$Exe, [string]$Work)
$ErrorActionPreference = 'Stop'
$uninstall = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\PortabilizerSmoke'
$steamUninstall = "$uninstall-Steam"
$vendor = 'HKCU:\Software\SmokePub'
$key = "$vendor\Smoke App"
$data = "$env:APPDATA\Smoke App"
$saves = "$env:APPDATA\Fake Game"
$state = "$env:LOCALAPPDATA\Portabilizer"
$pass = 0; $fail = 0
function Check($label, $ok) { if ($ok) { $script:pass++; "  ok   $label" } else { $script:fail++; "  FAIL $label" } }

function Cleanup {
    Remove-Item Env:\PORTABILIZER_STEAM_EXE -ErrorAction SilentlyContinue
    foreach ($k in $uninstall, $steamUninstall, $vendor) { if (Test-Path $k) { Remove-Item $k -Recurse -Force } }
    foreach ($d in $data, $saves) {
        if (Test-Path "$d.portabilizer-aside") { Remove-Item "$d.portabilizer-aside" -Recurse -Force }
        if (Test-Path $d) {
            if ((Get-Item $d).Attributes -band [IO.FileAttributes]::ReparsePoint) { (Get-Item $d).Delete() } else { Remove-Item $d -Recurse -Force }
        }
    }
    if (Test-Path $state) { Get-ChildItem $state -Directory | Where-Object Name -match '^(Smoke|Fake)-' | Remove-Item -Recurse -Force }
    # .NET unpacks each package's support files here, named after its exe.
    $cache = Join-Path $env:TEMP '.net'
    if (Test-Path $cache) { Get-ChildItem $cache -Directory | Where-Object Name -match '^(Smoke App|Fake Game)' | Remove-Item -Recurse -Force }
}

function IsLink($path) { (Test-Path -LiteralPath $path) -and [bool]((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) }

function WaitFor($condition) {
    for ($i = 0; $i -lt 60; $i++) { if (& $condition) { return $true }; Start-Sleep -Milliseconds 500 }
    $false
}

# A portable Steam game connects, hands over to Steam and exits. A question box would stop it finishing, so that counts as failing.
function RunPackage($exe) {
    $p = Start-Process $exe -PassThru
    if ($p.WaitForExit(20000)) { return $true }
    Stop-Process $p -Force
    $false
}

function Disconnect($exe) {
    $p = Start-Process $exe -ArgumentList '--disconnect' -PassThru
    $p.WaitForExit(20000)
}

Cleanup
$env:PORTABILIZER_STEAM_EXE = 'none' # also keeps message boxes off the screen during the test
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

"== app hands over to a copy of itself outside the package"
# Discord did this: the launcher tidied up while a copy it wasn't watching still used the settings, which scrambled them.
$twin = "$Work\install\gone\SmokeApp.exe"
'@start "" /b "%~1" /c ping -n 9 127.0.0.1 > nul' | Set-Content "$Work\handoff.cmd" -Encoding ascii
$session = Start-Process $launcher -ArgumentList "/c `"$Work\handoff.cmd`" `"$twin`"" -PassThru
Start-Sleep -Seconds 5
Check "first program gone" (@(Get-CimInstance Win32_Process -Filter "Name='SmokeApp.exe'" | Where-Object ExecutablePath -like "$Work\out\*").Count -eq 0)
Check "other copy still running" (@(Get-Process SmokeApp -ErrorAction SilentlyContinue).Count -gt 0)
Check "settings stay connected for it" (IsLink $data)
Check "launcher still watching" (-not $session.HasExited)
Check "tidied up once it closed" ($session.WaitForExit(30000))
Check "host file restored after handover" ((Get-Content "$data\settings.txt" -Raw) -match '^HOST-FILE\s*$')
Check "no aside folder after handover" (-not (Test-Path "$data.portabilizer-aside"))

"== app already open on this PC"
$open = Start-Process $twin -ArgumentList '/c ping -n 30 127.0.0.1 > nul' -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 1
$p = Start-Process $launcher -ArgumentList "/c `"$Work\probe.cmd`"" -PassThru -Wait
Check "refuses to start (exit $($p.ExitCode))" ($p.ExitCode -eq 1)
Check "nothing was swapped" ((-not (IsLink $data)) -and ((Get-Content "$data\settings.txt" -Raw) -match '^HOST-FILE\s*$'))
Stop-Process $open -Force
Get-CimInstance Win32_Process -Filter "Name='PING.EXE'" | Where-Object CommandLine -match '-n (9|30) ' | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 1

"== package moved to a path with spaces"
Remove-Item $vendor -Recurse -Force; Remove-Item $data -Recurse -Force
Move-Item "$Work\out" "$Work\moved here"
$launcher = (Get-ChildItem "$Work\moved here" -Filter '*.exe' | Select-Object -First 1).FullName
Start-Process $launcher -ArgumentList "/c `"$Work\probe.cmd`"" -Wait
$r = Get-Content "$Work\result.txt" -Raw
Check "registry follows the new location" ($r.Contains("$Work\moved here\App"))
Check "edits from earlier runs survived" ($r -match 'changed-by-run')

"== Steam game: capture"
# A fake Steam: a copy of ping that exits at once, under a name that can't match the real client.
$fake = "$Work\fakesteam"
$apps = "$fake\steamapps"
$game = "$apps\common\Fake Game"
$record = "$apps\appmanifest_999.acf"
New-Item -ItemType Directory -Force $game | Out-Null
Copy-Item C:\Windows\System32\PING.EXE "$fake\fakesteam.exe"
Copy-Item C:\Windows\System32\PING.EXE "$game\FakeGame.exe"
'original-content' | Set-Content "$game\content.txt" -Encoding ascii
New-Item -ItemType Directory -Force $saves | Out-Null
'captured-save' | Set-Content "$saves\settings.txt" -Encoding ascii
@'
"AppState"
{
	"appid"		"999"
	"StateFlags"		"4"
	"installdir"		"Fake Game"
	"buildid"		"1"
}
'@ | Set-Content $record -Encoding ascii
New-Item $steamUninstall -Force | Out-Null
Set-ItemProperty $steamUninstall DisplayName 'Fake Game'
Set-ItemProperty $steamUninstall Publisher 'SmokePub Inc'
# Like Steam's real entries after a reinstall: no InstallLocation, so the game is found through Steam's own record.
Set-ItemProperty $steamUninstall UninstallString "`"$fake\fakesteam.exe`" steam://uninstall/999"
$out = "$Work\steamout"
$p = Start-Process $Exe -ArgumentList "--capture `"Fake Game`" `"$out`"" -PassThru -Wait
Check "capture exit 0 (got $($p.ExitCode))" ($p.ExitCode -eq 0)
$launcher = "$out\Fake Game Portable.exe"
$saved = "$out\Data\Steam\appmanifest_999.acf"
$json = Get-Content "$out\portable.json" -Raw | ConvertFrom-Json
Check "package knows the Steam game" (($json.SteamAppId -eq '999') -and ($json.SteamInstallDir -eq 'Fake Game'))
Check "Steam's record copied" (Test-Path $saved)
Check "Steam game needs no admin" (-not $json.NeedsAdmin)
Check "disconnect file included" (Test-Path "$out\Disconnect from this PC.cmd")

"== Steam game: PC without it"
$env:PORTABILIZER_STEAM_EXE = "$fake\fakesteam.exe"
Rename-Item $game 'Fake Game gone'
Move-Item $record "$record.real"
Remove-Item $saves -Recurse -Force
Check "connects and hands over to Steam" (RunPackage $launcher)
Check "game stays connected to Steam" ((IsLink $game) -and (Test-Path $record))
Check "Steam sees the game's files" ((Get-Content "$game\content.txt" -Raw) -match 'original-content')
Check "saves stay connected, for Steam's Play button" ((IsLink $saves) -and ((Get-Content "$saves\settings.txt" -Raw) -match 'captured-save'))
(Get-Content $record) -replace '"buildid"\s+"1"', "`"buildid`"`t`t`"2`"" | Set-Content $record -Encoding ascii
'updated-by-steam' | Add-Content "$game\content.txt" -Encoding ascii
'saved-in-game' | Add-Content "$saves\settings.txt" -Encoding ascii

"== Steam game: next launch with Steam open"
$steamOpen = Start-Process "$fake\fakesteam.exe" -ArgumentList '-n 120 127.0.0.1' -WindowStyle Hidden -PassThru
Check "no restart question" (RunPackage $launcher)
Check "Steam left open" (-not $steamOpen.HasExited)
Check "still connected" ((IsLink $game) -and (Test-Path $record))
Check "update stayed in the package" ((Get-Content "$out\App\content.txt" -Raw) -match 'updated-by-steam')
Check "new version number saved" ((Get-Content $saved -Raw) -match '"buildid"\s+"2"')
Check "save stayed in the package" ((Get-Content "$out\Data\Folders\0\settings.txt" -Raw) -match 'saved-in-game')

"== Steam game: package moved, Steam still open"
Move-Item $out "$out moved"
$out = "$out moved"
$launcher = "$out\Fake Game Portable.exe"
$saved = "$out\Data\Steam\appmanifest_999.acf"
Check "no restart question after a move" (RunPackage $launcher)
Check "game link follows the package" ((Test-Path "$game\content.txt") -and ((Get-Content "$game\content.txt" -Raw) -match 'updated-by-steam'))
Check "saves link follows the package" ((Test-Path "$saves\settings.txt") -and ((Get-Content "$saves\settings.txt" -Raw) -match 'saved-in-game'))
Stop-Process $steamOpen -Force

"== Steam game: disconnect"
Check "disconnect finished" (Disconnect $launcher)
Check "game link removed" (-not (Test-Path $game))
Check "record removed" (-not (Test-Path $record))
Check "saves link removed" (-not (Test-Path $saves))
Check "game files survived" (Test-Path "$out\App\FakeGame.exe")
Check "saves survived" ((Get-Content "$out\Data\Folders\0\settings.txt" -Raw) -match 'saved-in-game')

"== Steam game: PC with its own copy"
Rename-Item "$game gone" 'Fake Game'
Move-Item "$record.real" $record
New-Item -ItemType Directory -Force $saves | Out-Null
'PC-SAVE' | Set-Content "$saves\settings.txt" -Encoding ascii
$before = (Get-FileHash $record).Hash
Check "plays and exits" (RunPackage $launcher)
Check "own copy not replaced" (-not (IsLink $game))
Check "own files untouched" ((Get-Content "$game\content.txt" -Raw) -notmatch 'updated-by-steam')
Check "own record untouched" ((Get-FileHash $record).Hash -eq $before)
Check "own saves untouched" ((-not (IsLink $saves)) -and ((Get-Content "$saves\settings.txt" -Raw) -match 'PC-SAVE'))

"== Steam game: leftover record from Steam"
Rename-Item $game 'Fake Game gone'
Set-Content $record '"AppState" { "installdir" "Fake Game" "buildid" "7" }' -Encoding ascii
Check "connects despite the leftover" (RunPackage $launcher)
Check "this PC's saves set aside" ((IsLink $saves) -and ((Get-Content "$saves.portabilizer-aside\settings.txt" -Raw) -match 'PC-SAVE'))
Check "disconnect finished" (Disconnect $launcher)
Check "leftover record put back" ((Get-Content $record -Raw) -match '"buildid" "7"')
Check "no aside record left" (-not (Test-Path "$record.portabilizer-aside"))
Check "this PC's saves put back" ((-not (IsLink $saves)) -and ((Get-Content "$saves\settings.txt" -Raw) -match 'PC-SAVE'))
Remove-Item $record

"== Steam game: leftover folder, Steam drops the game, interrupted setup"
New-Item -ItemType Directory -Force $game | Out-Null
'leftover-file' | Set-Content "$game\leftover.txt" -Encoding ascii
Check "connects over the leftover folder" (RunPackage $launcher)
Check "leftover folder set aside" ((IsLink $game) -and (Test-Path "$game.portabilizer-aside\leftover.txt"))
(Get-Item $game).Delete()
Remove-Item $record
Check "reconnects after Steam dropped it" (RunPackage $launcher)
Check "connected again" ((IsLink $game) -and (Test-Path $record))
$journal = Get-ChildItem $state -Recurse -Filter session.json | Where-Object FullName -match 'Fake-Game'
(Get-Content $journal.FullName -Raw) -replace '"Complete":true', '"Complete":false' | Set-Content $journal.FullName -Encoding utf8
Check "recovers from an interrupted setup" (RunPackage $launcher)
Check "saves connected after recovery" (IsLink $saves)
Check "disconnect finished" (Disconnect $launcher)
Check "leftover folder put back" ((-not (IsLink $game)) -and (Test-Path "$game\leftover.txt"))
Check "no aside folder left" (-not (Test-Path "$game.portabilizer-aside"))
Check "package intact" (Test-Path "$out\App\FakeGame.exe")

"== cleanup"
Cleanup
Check "all test keys gone" (-not ((Test-Path $uninstall) -or (Test-Path $vendor)))
Check "test folder gone" (-not (Test-Path $data))
"RESULT: $pass passed, $fail failed"
