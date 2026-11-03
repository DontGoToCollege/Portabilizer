# Portabilizer

Pick a program installed on your computer, and Portabilizer turns it into a portable folder. Put that folder on a USB drive or another PC and run the program from there, with your settings coming along.

This is an early version. It already works on simple apps, but it can't move everything yet, and it tells you what it expects before it copies anything.

## How to use it

1. Download `Portabilizer.exe` from the [Releases page](https://github.com/DontGoToCollege/Portabilizer/releases). There's nothing to install. Windows may warn you the first time because the program isn't signed yet.
2. Run it and pick an app from the list.
3. Read the verdict:
   - **Should work well:** nothing was found that would get in the way.
   - **Should work, with limits:** it explains what to expect, such as needing Steam on the other PC.
   - **Can't be made portable:** it explains why, and the button is turned off.
4. Choose where to save it and click **Make portable**.
5. Copy the folder anywhere and run the program inside it, named like `Audacity Portable.exe`.

Close the app before you make it portable.

## What ends up in the folder

- `App`: a copy of the program. The original is never changed.
- `Data`: your settings for that app, if you left the box ticked.
- A small starter program and `portable.json`, which tells it what to do.

**Your settings can include saved sign-ins, so don't give the folder to other people if you included them.**

## What happens when you run it

The starter briefly connects the app's settings to Windows, runs the app, saves any changes back into the folder, and removes everything again. If the PC has its own copy of that app's settings, they're set aside first and put back afterwards.

If the PC crashes or loses power while the app is open, starting the portable app again on that PC finishes the tidying up. If the portable drive is unplugged while the app is running, plug it back in and do the same.

## What it can't do yet

- **Apps that need a driver**, such as anti-cheat, dongles and VPNs. These must be installed on each PC.
- **Apps that rely on a background Windows service.** The service isn't carried over, and many updaters work this way.
- **Licences and accounts.** Portabilizer moves apps but never gets around licensing. Adobe apps still need Adobe's sign-in, and Steam games still need Steam.
- **Saved sign-ins.** Windows ties many of these to one PC, so you may need to sign in again.
- **File associations, right-click menus and Start menu entries.** These aren't set up on the other PC.
- **Microsoft Store apps.** They aren't in the list.
- **Some apps ask for administrator permission every time**, because they keep settings in a PC-wide part of Windows.
- **One app at a time.** Only one copy of a portable app can be open on a PC, and each package carries its own 60 MB starter.
- **One folder only.** Single-file packages aren't available yet.

Whether updates work inside a portable app depends on the app and hasn't been tested widely.

## License

MIT: free for anyone to use, change and share. See [LICENSE](LICENSE).
