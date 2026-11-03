# Changelog

## 0.1.6

- New icon.
- The version number shows in the title bar.
- Every app is checked in the background and marked with a coloured dot, and buttons above the list filter it by verdict.
- Save to picks the place, and a folder named after the app is made there. Untick **Make a folder for the app here** to save straight into the chosen folder.
- Saving into a folder that already has other things in it is safe: nothing there is overwritten, and a failed copy removes only what Portabilizer added.

## 0.1.5

- Steam games are found even when Steam leaves their install location blank in Windows' app list, which often happens after reinstalling a game. Before, they showed "This app's folder can't be found."

## 0.1.4

- **Fixes a bug that could damage an installed app's settings.** If an app handed over to another copy of itself, the starter could decide it had closed and swap the settings back while it was still running. With Discord on a PC that also has Discord installed, this reset the installed copy's settings and sign-in. The starter now waits for every copy of the app, and won't start while one is already open. Remake any portable folders made with an earlier version.
- Portable Steam games now stay connected to each PC's Steam, so Steam only restarts the first time, and stays open when you finish.
- You can press Play in Steam itself, and your portable saves are used.
- Added `Disconnect from this PC`, which puts everything back on that PC.

## 0.1.3

- Portable Steam games now run through Steam using the portable files, instead of Steam offering to reinstall them. The portable file connects the game to Steam, starts it, and disconnects it afterwards, keeping any update Steam applied.
- A Steam game that hasn't finished installing or updating now says so, instead of being judged by all of Steam's folder.

## 0.1.2

Internal test build, not released.

## 0.1.1

First public release.

- Lists installed apps and gives each one a plain-language verdict before anything is copied.
- Finds an app's folder, settings folders and registry settings, and flags drivers, services, anti-cheat and store-launcher games.
- Makes a portable folder, including any Visual C++ runtime files the app needs.
- Runs the portable app, saves changes back into the folder, and puts the PC's own app settings back afterwards, including after a crash. The starter's support files, about 30 MB, stay in the PC's temp folder.

## 0.1.0

Internal test build, not released.
