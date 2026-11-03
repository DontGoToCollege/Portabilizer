# End-to-end check with a throwaway fake app. Touches only things it creates itself and removes them at the end.
param([string]$Exe, [string]$Work)
$ErrorActionPreference = 'Stop'
$uninstall = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\PortabilizerSmoke'
$steamUninstall = "$uninstall-Steam"
$vendor = 'HKCU:\Software\SmokePub'
$key = "$vendor\Smoke App"
$data = "$env:APPDATA\Smoke App"
$state = "$env:LOCALAPPDATA\Portabilizer"
$pass = 0; $fail = 0
function Check($label, $ok) { if ($ok) { $script:pass++; "  ok   $label" } else { $script:fail++; "  FAIL $label" } }

function Cleanup {
    Remove-Item Env:\PORTABILIZER_STEAM_EXE -ErrorAction SilentlyContinue
    foreach ($k in $uninstall, $steamUninstall, $vendor) { if (Test-Path $k) { Remove-Item $k -Recurse -Force } }
    if (Test-Path "$data.portabilizer-aside") { Remove-Item "$data.portabilizer-aside" -Recurse -Force }
    if (Test-Path $data) {
        if ((Get-Item $data).Attributes -band [IO.FileAttributes]::ReparsePoint) { (Get-Item $data).Delete() } else { Remove-Item $data -Recurse -Force }
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

# Plays the fake game for a few seconds, then waits for the launcher to finish tidying up.
function PlayFakeGame($exe) {
    Start-Process $exe -ArgumentList '-n 5 127.0.0.1' -WindowStyle Hidden
    Wait-Process -Name 'Fake Game Portable' -Timeout 60 -ErrorAction SilentlyContinue
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
Set-ItemProperty $steamUninstall InstallLocation $game
$out = "$Work\steamout"
$p = Start-Process $Exe -ArgumentList "--capture `"Fake Game`" `"$out`"" -PassThru -Wait
Check "capture exit 0 (got $($p.ExitCode))" ($p.ExitCode -eq 0)
$launcher = "$out\Fake Game Portable.exe"
$saved = "$out\Data\Steam\appmanifest_999.acf"
$json = Get-Content "$out\portable.json" -Raw | ConvertFrom-Json
Check "package knows the Steam game" (($json.SteamAppId -eq '999') -and ($json.SteamInstallDir -eq 'Fake Game'))
Check "Steam's record copied" (Test-Path $saved)
Check "Steam game needs no admin" (-not $json.NeedsAdmin)

"== Steam game: PC without it"
$env:PORTABILIZER_STEAM_EXE = "$fake\fakesteam.exe"
Rename-Item $game 'Fake Game gone'
Move-Item $record "$record.real"
Start-Process $launcher
Check "game connected to Steam" (WaitFor { (IsLink $game) -and (Test-Path $record) })
Check "Steam sees the game's files" ((Get-Content "$game\content.txt" -Raw) -match 'original-content')
(Get-Content $record) -replace '"buildid"\s+"1"', "`"buildid`"`t`t`"2`"" | Set-Content $record -Encoding ascii
'updated-by-steam' | Add-Content "$game\content.txt" -Encoding ascii
PlayFakeGame "$game\FakeGame.exe"
Check "launcher finished" ($null -eq (Get-Process 'Fake Game Portable' -ErrorAction SilentlyContinue))
Check "link removed" (-not (Test-Path $game))
Check "record removed" (-not (Test-Path $record))
Check "update stayed in the package" ((Get-Content "$out\App\content.txt" -Raw) -match 'updated-by-steam')
Check "new version number saved" ((Get-Content $saved -Raw) -match '"buildid"\s+"2"')
Check "game files survived the unlink" (Test-Path "$out\App\FakeGame.exe")

"== Steam game: PC with its own copy"
Rename-Item "$game gone" 'Fake Game'
Move-Item "$record.real" $record
$before = (Get-FileHash $record).Hash
Start-Process $launcher
Start-Sleep -Seconds 3
Check "own copy not replaced" (-not (IsLink $game))
PlayFakeGame "$game\FakeGame.exe"
Check "own files untouched" ((Get-Content "$game\content.txt" -Raw) -notmatch 'updated-by-steam')
Check "own record untouched" ((Get-FileHash $record).Hash -eq $before)

"== Steam game: leftover record, launcher killed mid-run"
Rename-Item $game 'Fake Game gone'
Set-Content $record '"AppState" { "installdir" "Fake Game" "buildid" "7" }' -Encoding ascii
Start-Process $launcher
Check "connected despite the leftover" (WaitFor { (IsLink $game) -and ((Get-Content $record -Raw) -match '"buildid"\s+"2"') })
Get-Process 'Fake Game Portable' | Stop-Process -Force
Start-Sleep -Seconds 1
Check "after kill: still connected" (IsLink $game)
Start-Process $launcher
Check "next launch: connected again" (WaitFor { (IsLink $game) -and (Test-Path $record) -and -not (Get-ChildItem $state -Recurse -Filter session.json | Where-Object { (Get-Content $_.FullName -Raw) -match 'SteamRecordLive":false' }) })
PlayFakeGame "$game\FakeGame.exe"
Check "leftover record put back" ((Get-Content $record -Raw) -match '"buildid" "7"')
Check "no aside record left" (-not (Test-Path "$record.portabilizer-aside"))
Check "link removed after recovery" (-not (Test-Path $game))
Check "package intact after recovery" (Test-Path "$out\App\FakeGame.exe")

"== cleanup"
Cleanup
Check "all test keys gone" (-not ((Test-Path $uninstall) -or (Test-Path $vendor)))
Check "test folder gone" (-not (Test-Path $data))
"RESULT: $pass passed, $fail failed"
