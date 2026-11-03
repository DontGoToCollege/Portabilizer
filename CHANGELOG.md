# Changelog

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
