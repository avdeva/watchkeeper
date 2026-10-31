# Changelog

## 4.0.0
A rewrite of version 3, made simpler, more reliable and easier to follow. Version 3 settings are converted on the first start (see README, "Updating from version 3").

**Parts**
- Kept: Watchdog, Folder tasks (formerly folder policies), Vertical conversion, GPIO to vMix / web requests, Alerts (e-mail, Jira, webhooks, Windows event log).
- Removed: Scripts, Studios / Pathfinder.

**Reliability**
- Security is set once, at installation: the program in Program Files, and the data folder open only to SYSTEM and Administrators. The runtime checks of version 3 that refused to start programs, refused hard-linked files, set settings aside or refused to run from some folders are gone. They were behind most field problems: settings set aside or replaced at boot, "reinstall needed", deploy refusing to run, recordings left behind.
- Settings:
  - One file, saved in one step, with a copy of every version kept.
  - A missing file is put back from the newest copy.
  - A file that cannot be read keeps the service running with the settings it had, raises an alert, and is never overwritten by the service.
  - Unknown or mistyped settings are named, with the line number for JSON errors.
- The service reports "running" to Windows at once and starts its work in the background, so a slow boot never fails it. Windows restarts it if it stops.
- Every part runs on its own thread, and an error in one never stops the others.
- Watchdog:
  - The state of each entry is shown in plain words with its process IDs.
  - Closing for a restart runs on its own thread, so one slow program never holds up the others.
  - A service stuck stopping or starting is ended and started again.
  - A program already running in another user's session is not started a second time.
- Folder tasks:
  - A move to another drive copies to a temporary name, checks the size, renames, and only then deletes the original.
  - Files still being written, or open in another program, are always left alone.
  - Tasks run one after another, so two tasks never touch the same file at once.
  - A copy task copies each file once; a file already at the destination with the same content is recognised, also when a move was interrupted after copying.
  - Files with the same name from different sub-folders never overwrite each other.
  - Folders on mapped drive letters, which the service cannot see, are warned about.
- Vertical conversion:
  - The encoder order is GPU with hardware decoding, GPU with software decoding, the same in the signed-in session, then the CPU.
  - The way that worked is remembered for the next file.
  - The output is checked (size and length) before the original is touched.
- GPIO:
  - Each connection starts by asking for every pin's state; that answer, and changes made while disconnected, run nothing.
  - Each trigger has its own queue, so a slow trigger never delays another.
- Settings: entries added by hand without an id are given one and the file is saved, so the id stays the same. A settings file that cannot be read raises one alert (repeated every 30 minutes at most), and is read again every minute in case it was only locked.

**Clearer**
- The manager has one page per part, an Overview in plain words, one Save for everything, and the problems in the settings listed before saving.
- New self-test (`--selftest`, or a button in the manager). The service checks, as Local System, that it can start a program in the signed-in session, find it, and end it, plus encryption, folder permissions and ffmpeg.
- `--check settings.json` lists the problems in a settings file; `--example` writes an example.
- GitHub Actions builds and tests on Windows, installs the service and runs the self-test.
