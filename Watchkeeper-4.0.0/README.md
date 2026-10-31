# Watchkeeper

Watchkeeper keeps a Windows broadcast or recording server working without anyone watching it:

- **Watchdog**: keeps programs (vMix, Virtual Video Director, ...) and Windows services running. It starts them in the signed-in user's session, starts them again when they stop, can restart them every day, and can check that they keep answering.
- **Folder tasks**: move, copy or delete recordings on a schedule. For example, it can move them from the recording drive to the storage drive, or delete them after 30 days.
- **Vertical conversion**: turns recordings on their side (portrait) with ffmpeg, on the GPU when it can.
- **GPIO**: Axia GPIO pins (LWRP) run vMix functions or web requests.
- **Alerts**: problems go to e-mail, Jira, webhooks (Teams, Slack, ...) and the Windows event log, followed by "back to normal" when they are over.

It is one file, `Watchkeeper.exe` (.NET Framework 4.5 or later, which is included in Windows 10, 11 and Server 2016 and later). It runs as a Windows service. The same file, opened as administrator, is the manager window.

## Install

1. Put `Watchkeeper.exe` and `deploy.cmd` in one folder on the server.
2. Right-click `deploy.cmd` > **Run as administrator**.
3. Open `C:\Program Files\Watchkeeper\Watchkeeper.exe` (it asks for administrator rights) and set it up.

`deploy.cmd` does the following:
- copies the program to `C:\Program Files\Watchkeeper`
- creates `C:\ProgramData\Watchkeeper`, open only to SYSTEM and Administrators
- registers the service: it starts with Windows, runs as Local System, and Windows restarts it if it ever stops
- starts the service

Running it again with a newer `Watchkeeper.exe` updates the program and keeps the server's settings.

**Same settings on several servers**: set up one server, then use **Settings > Export settings...** to get a file. Then on each other server, put that file next to `deploy.cmd` and run `deploy.cmd settings.json` as administrator. That replaces the server's settings; its previous settings are kept in the backups folder. Passwords are not exported, because they are encrypted for each computer. Type them in the manager, or into the file in plain text; the service encrypts them when it reads the file.

**Desktop programs need a signed-in user.** vMix and VVD have windows, so they run in the session of the user Windows signs in automatically, or else the user signed in at the console. Set up Windows auto-logon on the server so a user is always signed in after a restart.

## The manager

The manager window has one page per part, plus the following:
- **Overview**: one line for everything Watchkeeper does, in plain words. Green is fine, orange is waiting or busy, red needs attention, grey is switched off.
- **Settings**: general settings, the service, backups of the settings, export and import, and the self-test.
- **Log**: what the service did.

Changes take effect when you click **Save changes**, which saves all pages at once. The service picks up the saved file within two seconds.

When something is set up in a way that cannot work, the manager says so before saving: a missing path, a time that is not HH:MM, a destination inside the folder it copies from. The service skips that entry and shows why on the Overview.

## Watchdog

| Setting | What it does |
|---|---|
| What | A program, or a Windows service (by its service name). |
| Program, Arguments, Start in | The program file to start. For a launcher, this is the launcher. |
| Run | **On the signed-in user's desktop** (programs with a window), or **hidden, in the background**. |
| Start with full administrator rights | Only for a desktop program that asks for administrator rights; it skips the consent prompt. |
| Application to watch | Only when the program above is a launcher that opens the real application (VVD), or when a Windows service starts an application. Give its full path. `*` may stand for part of a folder name that changes with each version: `...\app-*\VVD.exe`. **Choose from running programs...** lists what is running, with each process's session and Windows service name, and offers the `*` itself. |
| Running means | What has to run for the entry to count as running (see below). |
| Wait to appear | How long a start gets before it counts as failed. |
| First start, Start again | Delays after Windows starts or a user signs in, and after it stops. |
| At most N starts per hour | Then Watchkeeper pauses for the rest of the hour and raises an alert, instead of restarting a broken program forever. |
| Daily restart | Close and start again at a time of day, on chosen days. A program with a window is asked to close first, then ended after 10 seconds. |
| Health check | `http://...` must answer below HTTP 500 (vMix: `http://127.0.0.1:8088/api`), or `tcp://host:port` must accept a connection. If it does not answer for N seconds after the loading time, Watchkeeper alerts, and restarts the program if you choose that. |

**Running means** (only matters with an application to watch):

| Choice | Counts as running when | Use it when |
|---|---|---|
| Automatic | The application runs. For a Windows service: the service and the application both run. | Most setups. |
| The application to watch is running | The application runs, whatever the launcher does. | — |
| What Watchkeeper started (its PID) is running | The launcher runs, followed by the process ID it got when started. After a restart of Watchkeeper it is found by its file. | The launcher stays open and the application cannot be found. |
| Either | One of them runs. | — |
| Both | Both run. When one runs without the other for longer than "wait to appear", everything is closed and started again. | Only for a launcher, or a service, that stays running. |

The state line shows the process IDs Watchkeeper found. Use it to check that it watches the right process.

- **Which desktop:** programs run in the session of the user Windows signs in automatically (when auto sign-in is set up), otherwise the user at the console. A program already running in another user's session counts as running; no second copy is started.
- **A service stuck stopping or starting** for longer than "wait to appear" (at least a minute) has its process ended, raises an alert, and is started again.
- **Full administrator rights:** when ticked, the close request of a daily restart is also sent with full rights, as Windows does not let a limited program close the window of one with full rights.

## Folder tasks

Each task handles the files of one folder. Tasks run one after another, in list order.

**Which files are touched:**
- files matching the patterns (`*.mp4;*.mov`) and not the exceptions
- optionally, only files older than a given age
- optionally, all but the newest N files

**Files that are never touched:**
- files changed in the last N seconds (a recording still being written)
- files another program has open for writing

**How each action works:**
- **Move**: on the same drive, the file is renamed in one step. To another drive, it is copied under a temporary name, the copy's size is checked, the copy gets its real name, and only then is the original deleted. If the original cannot be deleted, both copies are kept and the problem is reported.
- **Copy** with "if the name is taken: leave the file where it is" copies each file once.
- **Delete** deletes the matching files.

**Folders on other computers:** give them as `\\server\share\folder`. Drive letters mapped by a user (`Z:`) belong to that user; the service runs as Local System and does not see them. The manager warns about a folder on a mapped drive, or on a drive this computer does not have.

**Preview** lists what the task would do now, without changing anything. **Run now** asks the service to run it once.

Schedules:
- **Every N minutes**.
- **Once a day at HH:MM**. A time missed while the computer was off runs when it is back. A new daily task waits for its first time instead of running at once.
- **Only with Run now**.

## Vertical conversion

Watchkeeper watches a folder for new recordings:

1. A file is converted once it has not changed for a few seconds.
2. The output name comes from the first **source** whose text is in the file name. `Recorder 7 - Cam1 - 1 May 2026 - 10-00-00 AM.mp4` with the prefix `Vertical PGM` becomes `Vertical PGM - 1 May 2026 - 10-00-00 AM.mp4`. A source that is switched off leaves its files alone.
3. The output is written under a temporary name, then checked: its size must be the source's size turned on its side, and its length within 2 seconds (or 2%) of the source's. Only then is it renamed and the original deleted, moved or kept.
4. If an output with that name already exists, the original is left alone.

Ways of encoding are tried in order:
1. the GPU with hardware decoding
2. the GPU with software decoding
3. the same two again in the signed-in user's session (Intel Quick Sync often only works there on servers)
4. the CPU

The way that worked is tried first for the next file. **Test the encoders...** converts a 2-second clip each way and says which ones work.

A failed file is tried again after 1, 5, 15 and 60 minutes, then given up with an alert. **Try failed files again** resets that.

## GPIO

On the **Devices and vMix** tab, add the Axia devices (address, LWRP port 93, and a password only if the device asks for one) and vMix (Web Controller on, port 8088).

On the **Triggers** tab, choose a device, GPI or GPO, a port and a pin, then the actions for when the pin turns on and for when it turns off:
- a vMix function (`OverlayInput1In`, `Cut`, `SetText`, ...)
- a web request

A pin is on when the contact is closed (low), unless the trigger is inverted. Each time Watchkeeper connects (also after the connection was lost), it asks the device for every pin's current state. That answer only sets the known state and runs nothing: a pin that changed while the connection was down does not run its actions. Only a change reported after that runs them.

Each trigger runs its actions in order, one change after another. A trigger that waits (an action with a delay, a slow vMix) does not hold up the other triggers.

**Test** runs a trigger's actions at once. The **Live** tab shows each pin and every change.

## Alerts

Each destination chooses:
- which parts it hears about: Watchdog, Folder tasks, Vertical, GPIO, Service and settings
- the minimum level: warnings and errors, or errors only
- whether "back to normal" is sent
- how soon the same alert may be sent again

Destination types:
- **E-mail**: STARTTLS (587), SSL (465) or none (an internal relay on port 25). A password is never sent over an unencrypted connection.
- **Jira**: an issue per problem. While that issue is open, the same problem again and its "back to normal" are added as comments. Jira Cloud uses the account e-mail and an API token; Server or Data Center uses a personal access token.
- **Webhook**: posts `{"text": "..."}`, which Teams, Slack and Mattermost accept, or your own JSON with `{title}`, `{message}`, `{level}`, `{server}`, `{time}` and `{part}`.

A failed send is tried again after 1, 5 and 15 minutes, and the failure is shown on the Alerts page. **Send a test message** sends from the manager straight away.

## Files

Everything is in `C:\ProgramData\Watchkeeper`, which only SYSTEM and Administrators can open:

| File | What it is |
|---|---|
| `config.json` | The settings, one readable JSON file. You can edit it by hand; the service notices within two seconds. |
| `backups\` | A copy of every saved version (the newest 50), and of a hand-edited file before the next save. **Settings > Backups** shows them and restores one. If `config.json` goes missing, the newest backup is put back. If it cannot be read, the service keeps running with the settings it had (or, at start, the newest backup), raises an alert, and leaves the damaged file for you to fix. |
| `logs\` | One log file per day, kept 30 days. |
| `status.json` | Written by the service every 2 seconds; the manager shows it. |
| `commands\` | Requests from the manager to the service (restart now, run now, self-test). |
| `state.json` | When each folder task last ran. |
| `import-report.txt` | What was left out when version 3 settings were converted. |

Passwords and tokens are encrypted for the computer (Windows DPAPI, machine key). The service and an administrator's manager can read them; a copy of the file on another computer cannot.

## Updating from version 3

Run `deploy.cmd` with the new `Watchkeeper.exe`. On its first start, the service converts the version 3 `config.json` to the new format. The old file is kept in the backups folder. Everything that has a place in version 4 is carried over, including passwords where Windows can still decrypt them. The rest is listed in `import-report.txt`, shown under **Settings > Show the import report**:
- the Scripts and Studios parts (removed in version 4)
- size and time-of-day filters of folder rules
- GPIO command messages

Version 3 folder policies with several rules become one task per rule, which run one after another. Check them before switching them on.

## Self-test

**Overview > Run the self-test...** (or `Watchkeeper.exe --selftest`) asks the service to check, as Local System:
- encryption works
- the data folder is protected
- the settings can be read
- the service is installed from Program Files
- Windows services and running programs can be read
- a program can be started in the signed-in session, found, and ended, which is exactly what the watchdog does
- ffmpeg runs

Run it once on every new server.

## Command line

```
Watchkeeper.exe                          the manager (as administrator)
Watchkeeper.exe --install [--config settings.json] [--replace-config]
Watchkeeper.exe --uninstall              remove the service (settings and logs are kept)
Watchkeeper.exe --check settings.json    list problems in a settings file
Watchkeeper.exe --example settings.json  write an example settings file (everything switched off)
Watchkeeper.exe --selftest
Watchkeeper.exe --console                run in a console window, for finding a problem
--log file.txt                           also write everything printed to this file
```

## Building and testing

- **Windows**: `build.cmd` builds `dist\Watchkeeper.exe` with the C# compiler that comes with Windows. `build.cmd /test` also runs the tests.
- **Linux (Mono)**: `./build.sh`, then `./test.sh` (71 tests).
- **UI check**: `./build-uitest.sh`, then run it under Xvfb. It drives every page against a running engine and saves screenshots.
- **CI**: the GitHub Actions workflow builds and tests on Windows, installs the service on the build machine, runs the self-test and removes it again.

## Status

Version 4.0.0 is a rewrite of version 3, made simpler and easier to follow.

The engine, the settings, the import and the UI were tested on Linux with Mono. Programs and services were simulated; local stand-ins were used for the Axia device, vMix, Jira, the mail server and webhooks; ffmpeg was a fake. The Windows-only parts have not been run on Windows at the time of this release:
- starting programs in the signed-in session, with or without full rights
- Windows services and folder permissions
- telling mapped and missing drives apart
- the event log

Run the self-test on the first server. The GitHub Actions workflow exercises them on a Windows build machine.

MIT license.
