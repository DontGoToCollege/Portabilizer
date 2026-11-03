# Portabilizer

Pick a program installed on your computer, and Portabilizer turns it into a portable folder. Put that folder on a USB drive or another PC and run the program from there, with your settings coming along.

This is an early version. It already works on simple apps, but it can't move everything yet, and it tells you what it expects before it copies anything.

## How to use it

1. Download `Portabilizer.exe` from the [Releases page](https://github.com/DontGoToCollege/Portabilizer/releases). There's nothing to install. Windows may warn you the first time because the program isn't signed yet.
2. Run it and pick an app from the list. Every app is checked in the background and gets a coloured dot. The buttons above the list show only the apps in one group.
3. Read the verdict:
   - **Should work well:** nothing was found that would get in the way.
   - **Should work, with limits:** it explains what to expect, such as needing administrator permission.
   - **Can't be made portable:** it explains why, and the button is turned off.
4. Choose where to save it and click **Make portable**. Portabilizer makes a folder named after the app inside the place you pick, such as `Desktop\Audacity 3.7.8 Portable`. Untick **Make a folder for the app here** to put the files straight into the chosen folder instead. It never overwrites anything already there.
5. Copy the folder anywhere and run the program inside it, named like `Audacity Portable.exe`.

Close the app before you make it portable.

## What ends up in the folder

- `App`: a copy of the program. The original is never changed. Any Visual C++ or older DirectX files it needs are added, so the other PC doesn't need them installed.
- `Data`: your settings for that app, if you left the box ticked, and a copy of .NET if the app is built on it.
- A small starter program and `portable.json`, which tells it what to do.

**Your settings can include saved sign-ins, so don't give the folder to other people if you included them.**

## What happens when you run it

The starter briefly connects the app's settings to Windows, runs the app, saves any changes back into the folder, and takes the app's settings out of Windows again. If the PC has its own copy of that app's settings, they're set aside first and put back afterwards.

The portable app won't start while the same app is open on that PC, including hidden in the system tray, because swapping settings under a running app would damage them. Close it fully first.

One thing does stay behind: about 30 MB of the starter's own support files in the PC's temp folder, for each portable app you run there. They hold none of your data and can be deleted safely.

If the PC crashes or loses power while the app is open, starting the portable app again on that PC finishes the tidying up. If the portable drive is unplugged while the app is running, plug it back in and do the same.

## The library

Apps often need extra pieces before they'll run, such as .NET or Visual C++ files. Portabilizer keeps its own copies in a `Portabilizer-Library` folder beside it, and puts the ones each app needs into its portable folder. The other PC then needs nothing installed.

Click **Library** to choose where these pieces come from:

- **.NET:** by default Portabilizer uses its library and downloads anything missing from Microsoft when you make a portable app. Every download is checked against Microsoft's published fingerprint. You can also download versions ahead of time, add the versions installed on this PC, or turn downloads off.
- **Visual C++ and DirectX:** Microsoft only offers these as installers, so Portabilizer can't download them. Click **Add this PC's files** to keep this PC's copies in the library for good.

For .NET 3.1 and 5.0, Microsoft offers the part desktop apps need only as an installer. Those come from this PC if it has them.

## Steam games

A portable Steam game still runs through Steam, so Steam must be installed and signed in to the account that owns the game. What Portabilizer saves you is the download.

Run the portable file once on each PC. It connects the game and your saves to that PC's Steam, and Steam restarts once if it's open. After that Steam treats the game as installed, so you can press Play in Steam or run the portable file, whichever you like. Steam updates the game as usual, and the updates go into your portable folder.

- The game stays connected until you run `Disconnect from this PC` in the portable folder, which puts everything back. **Do this before deleting the portable folder.**
- The folder has to be there when you play. If you move it or its drive letter changes, run the portable file once to reconnect. Steam won't need to restart.
- If the PC already has the game installed, Steam plays its own copy with its own saves.
- Steam Workshop mods don't come with the game.
- Don't choose Uninstall in Steam while the game is connected, as that could delete the portable copy.

This is new and hasn't been proven with every game.

## What it can't do yet

- **Apps that need a driver**, such as anti-cheat, dongles and VPNs. These must be installed on each PC.
- **Apps that rely on a background Windows service.** The service isn't carried over, and many updaters work this way.
- **Licences and accounts.** Portabilizer moves apps but never gets around licensing. Adobe apps still need Adobe's sign-in.
- **Saved sign-ins.** Windows ties many of these to one PC, so you may need to sign in again.
- **File associations, right-click menus and Start menu entries.** These aren't set up on the other PC.
- **Microsoft Store apps.** They aren't in the list.
- **Some apps ask for administrator permission every time**, because they keep settings in a PC-wide part of Windows.
- **One app at a time.** Only one copy of a portable app can be open on a PC, and each package carries its own 60 MB starter.
- **One folder only.** Single-file packages aren't available yet.

Whether updates work inside a portable app depends on the app and hasn't been tested widely.

## License

MIT: free for anyone to use, change and share. See [LICENSE](LICENSE).
