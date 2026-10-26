<p align="center"><img src="assets/app.png" width="128" alt="Watchkeeper logo"></p>

<h1 align="center">Watchkeeper</h1>

<p align="center">A Windows service that keeps a server's programs running, its folders tidy, and the right people informed.<br>
One file, no installer, no dependencies beyond the .NET Framework that ships with Windows.</p>

---

Watchkeeper started as the housekeeping service for a broadcast studio (recordings moved off a cache drive, vMix kept alive, Axia GPIO wired to vMix) and grew into a general server watchdog with rule-based alerting. Each server switches on only the parts it needs; a plain application server can run just the watchdog and alerts.

| Part | What it does |
|---|---|
| **Folder policies** | Move, copy or delete files per folder on a schedule: a simple choice, or an ordered list of rules (age, name, size, time of day, keep newest N). Retention. |
| **Watchdog** | Keeps programs and Windows services running. Start delays, restart limits, scheduled restarts, HTTP/TCP health checks that catch a hung program. Starts desktop programs in the signed-in user's session. |
| **Scripts** | Any `.ps1` / `.bat` / `.cmd` / `.vbs` / `.exe` on a schedule, at service start, or once for every new file in a folder. |
| **Vertical conversion** | Portrait versions of recordings with ffmpeg on the GPU (Intel Quick Sync, NVIDIA NVENC, AMD AMF) or CPU. |
| **GPIO (Axia)** | Axia Livewire GPIO pins and command messages to vMix functions or any HTTP request. |
| **Studios** | Studio configurations (which vMix, VVD... with which settings) a server can switch between. Reports a heartbeat, its status and the active studio to Pathfinder Core PRO (memory slots) and/or as GPIO pins, and switches studio when Pathfinder says so: failover between studio servers, decided by Pathfinder. |
| **Alerts** | Rule-based routing to the Windows Event Log, email (SMTP), webhooks (Teams, Slack, Discord, Google Chat, Jira Automation, custom JSON) and Jira issues. "Back to normal" messages when a problem clears. |

Requirements: Windows 10/11 or Windows Server 2016+ (.NET Framework 4.6 or later, included). Administrator rights to install the service.

## Contents
- [How it runs](#how-it-runs)
- [Install](#install)
- [Deploy to other servers](#deploy-to-other-servers)
- [Alerts](#alerts)
- [Folder policies](#folder-policies)
- [Watchdog](#watchdog-programs-and-windows-services)
- [Scripts](#scripts)
- [Vertical conversion](#vertical-conversion)
- [GPIO (Axia to vMix)](#gpio-axia-to-vmix)
- [Studios and Pathfinder failover](#studios-and-pathfinder-failover)
- [Command line](#command-line)
- [Configuration file](#configuration-file)
- [Security](#security)
- [Build and test](#build-and-test)
- [Status](#status)
- [License](#license)

## How it runs
- **Service** `Watchkeeper`: runs as Local System, starts automatically at boot, needs no sign-in. Process priority High (configurable). Windows restarts it after 5 s, 10 s, then every 30 s after any unexpected stop. All work happens here.
- **Manager window**: `Watchkeeper.exe` without arguments. Asks for administrator rights, edits the configuration and talks to the service over a local named pipe (Administrators and Local System only, not reachable from the network). Closing it changes nothing; the service keeps running.
- **Configuration**: one JSON file, `C:\ProgramData\Watchkeeper\config.json`. The service re-reads it within 2 seconds of any change (Save in the manager, Import, or a hand edit). A file that cannot be parsed is rejected, the previous settings stay in force and an alert is raised. Each save keeps the previous file as `config.json.bak`. The folder is owned by Administrators and only Administrators and Local System can open it; this is set before anything is written there (see [Security](#security)).
- **Log**: `C:\ProgramData\Watchkeeper\watchkeeper.log` (rotating, 10 x 5 MB), also on the manager's Log page with level, source and text filters.
- Passwords, tokens, webhook addresses and extra headers are stored encrypted with the service account's own key (Windows DPAPI, user scope of Local System): only the service can decrypt them.

## Install
1. Download `Watchkeeper.exe` from the [releases](../../releases) and check it against `SHA256SUMS` (`Get-FileHash Watchkeeper.exe`). Keep it in a folder only administrators can change, for example your own Downloads folder or `C:\Program Files`, and run it (accept the administrator prompt). Watchkeeper refuses to run with administrator rights from a folder other accounts can write to, because a file planted next to it would run with those rights.
2. On first start it asks what the server does: **Studio server** (switched-off templates for recordings, vMix and GPIO) or **Watchdog and alerts** (watchdog, scripts and alerts only). Parts can be switched on or off later under Service & settings > **Parts used on this server**.
3. Service & settings > **Install / update service**. This copies the exe to `C:\Program Files\Watchkeeper\`, registers the service, sets its recovery options, creates the Event Log source and starts it. Use the same button to update after downloading a newer exe.
4. Turn on **Dry-run mode** while trying settings: every task then only logs what it would do.
5. Programs that need the desktop (vMix and similar): set up Windows auto-logon for the operator account so a user is signed in after every reboot. The service starts desktop programs inside that session.
6. Switch on each folder policy, program and script you want. New entries start switched off.

## Deploy to other servers
All three end with the configuration written, its passwords encrypted for that computer, and the service installed and running.
1. **deploy.cmd**: copy `Watchkeeper.exe`, `deploy.cmd` and a configuration file to the server, right-click `deploy.cmd` > Run as administrator. `deploy.cmd` alone uses `config.json` next to it, or else the watchdog starter; `deploy.cmd studio` / `deploy.cmd watchdog` pick a starter; `deploy.cmd file.json` uses that file.
2. **deploy-remote.ps1**: from one computer to many over PowerShell remoting:
   `.\deploy-remote.ps1 -ComputerName APP1,APP2 -Config .\config-watchdog.json -Sha256 <value from SHA256SUMS>`
   Each copy is checked against the hash before it runs, and the copied files are deleted from every server afterwards.
3. **Command line**: `Watchkeeper.exe --deploy --config file.json` (or `--profile watchdog`), optional `--server-name "App 2"`, as administrator. Output also goes to `C:\ProgramData\Watchkeeper\deploy.log`.

Service & settings > **Export for another server** (or `--export-template file.json`) writes the current settings without passwords, tokens, webhook addresses, extra headers and server name. They may be typed into a deploy file as plain text (`"smtp_password"`, `"jira_token"`, `"secret"`, `"url"`, `"headers"`); deploy encrypts them and reminds you to delete the file. An existing configuration on the target is kept as `config.json.before-deploy`. `config-studio.json` and `config-watchdog.json` in the release are the two starters.

A configuration decides what runs as Local System, so deploy and Import list every program and script it can run. Deploy refuses a configuration whose programs or scripts are in folders other accounts can change, unless `--allow-untrusted-paths` is given (they still do not run until they are protected).

## Alerts
Alerts are built from two lists on the Alerts page.

**Destinations** are where an alert can go. Add as many as needed, each with its own settings:

| Type | Settings |
|---|---|
| Windows Event Log | Application log, source `Watchkeeper`. Errors as Error, warnings as Warning, the rest as Information. |
| Email (SMTP) | Server and port; STARTTLS (587), implicit TLS (465) or none (internal relay on 25, without a login); user and password, sent only over TLS 1.2 (empty user = relay by IP address); "trust this certificate" with its SHA-256 fingerprint for an internal server with its own certificate; from; recipients. |
| Webhook | `https://` address (stored encrypted: the address is the key to the channel) and format: Slack / Google Chat / Mattermost (text), Microsoft Teams (Workflows, Adaptive Card), Discord, Jira Automation, plain JSON, or custom JSON with placeholders. Extra headers (e.g. `Authorization: Bearer ...`, stored encrypted). Proxy. |
| Jira issue | Jira Cloud or Data Center over the REST API (`https://` only): address, login, project key, issue type, priority, labels. Proxy. |

**Rules** decide which alerts go to which destinations, the same way folder policies use rules. Each rule takes:
- **All alerts**, or only alerts from the **parts** ticked (folder policies, watchdog, scripts, vertical conversion, GPIO, configuration file, service start), optionally narrowed to **individual entries** (one program, one folder policy, one script, one GPIO device or trigger);
- a **minimum severity**: errors (something failed, stopped or cannot be reached), warnings and errors (also restarts and service starts), or everything;
- whether to send **"back to normal"** when the problem clears;
- one or more **destinations**;
- a **cooldown**: the same alert at most once per N minutes (suppressed repeats are counted in the next one);
- **stop here**: alerts this rule takes are not checked against the rules below it.

Rules are checked from the top. Every rule that takes an alert sends it; a destination receives each alert once even when several rules name it. A new configuration starts with one rule, "All alerts", sending everything to the Event Log.

Example:

| # | Rule | Takes | Sends to |
|---|---|---|---|
| 1 | Playout | Watchdog: vMix only, errors, with recoveries, stop here | Jira OPS, Teams #playout |
| 2 | Everything | All alerts, warnings and errors | Event Log, email to IT |

Alert text is escaped for each format, so file names, script output or error text cannot add links, mentions (`@channel`, `@everyone`) or formatting. What a failed script printed is left out of alerts unless **Include what a failed script printed** is ticked. Redirects are never followed: the login, the secret header and the alert would otherwise go wherever the answer points.

**Recent alerts** lists what was raised since the service started, which rules took each one and what every destination answered. **Tests** are sent by the service with the settings on screen, saved or not, and come back with a step-by-step trace (name lookup, connection, TLS, login, HTTP status and answer) that opens on failure and names the step that failed.

### Jira
A Jira destination opens one issue per problem. While that issue is still open, repeats of the same alert and its "back to normal" message are added as comments instead of opening more issues. The alert and its issue are tied together by a label `wk-<hash>` (server name + alert), so each server gets its own issue.
- **Jira Cloud**: address `https://yoursite.atlassian.net`, login *User + API token*, user = the Atlassian account's email, token from id.atlassian.com > Security > API tokens.
- **Jira Data Center / Server**: user + password, or *Personal access token* (sent as a bearer token).
- The account needs permission to browse the project, create issues and add comments. **Check login and project** verifies the login and the project and lists the project's issue types; **Create a test issue** opens a real issue.

### Jira Automation (incoming webhook)
For teams that prefer to build the issue in Jira Automation: create a rule with the **Incoming webhook** trigger, paste its address into a webhook destination with format *Jira Automation*, and its secret into *Webhook secret* (sent as `X-Automation-Webhook-Token`). The JSON body carries `summary`, `body`, `title`, `message`, `severity`, `recovered`, `kind`, `part`, `item`, `server`, `time` and `dedupe` (the same value for every repeat of one problem), available in the rule as `{{webhookData.summary}}` and so on.

### Microsoft Teams
In the channel choose **Workflows** > *Post to a channel when a webhook request is received* and paste its address into a webhook destination with format *Microsoft Teams*. Old Office 365 connector links no longer work.

### Proxy
Destinations connect directly by default. The service runs as Local System and does not see a proxy configured for a user; if the server only reaches the internet through a proxy, choose **This proxy** (or Windows proxy settings) on the destination. The computer's Windows login is sent to the proxy only when **Send this computer's Windows login to the proxy** is ticked: a proxy found automatically on the network (WPAD) could otherwise be an impostor that relays that login.

## Folder policies
Each policy has a source folder, a destination, a schedule (every N minutes, daily, weekly, or manual), optional sub-folders, a "leave files changed in the last N seconds alone" guard, and one choice for its files:
- **Moved to the destination**: every finished file goes.
- **Copied to the destination**: the original stays.
- **Deleted once older than** N minutes/hours/days/weeks (retention, by Date Modified).
- **Handled by custom rules**: an ordered list; each file is checked from the top and the first rule that matches decides. A rule can be narrowed by name wildcards and exceptions, age, time of day (Date Modified, or a recording time in the file name), size, and "always leave the newest N". Actions: delete, move, copy (sub-folder structure; replace / skip / keep both), keep.

"Finished" means not modified for 60 seconds and not open for writing. Moves and copies keep Date Modified; a move between drives is a copy under a temporary name, a size check, then deletion of the original. **Preview** shows what each file would get right now, using unsaved settings, and changes nothing. A daily or weekly run missed while the server was off runs about a minute after the service starts.

Never touched: hidden and system files, folder links (junctions, symbolic links, mount points) anywhere on the path, files with more than one name (hard links), drive roots, Windows / Program Files / user-profile folders. Every folder from the drive root down is opened without following links and held open while it is used, so it cannot be swapped for a link during a run; deletes and renames act on the open file. A policy whose folder is, or passes through, a link is refused.

The studio starter contains switched-off example policies for a recorder workflow: recordings moved every minute from a cache drive (`T:`) to a data drive (`D:`), retention of 7 days on the data drive only, and a sort by recording time. Adjust or delete them.

## Watchdog (programs and Windows services)
Add a program or a Windows service. Per entry: first-start delay, restart delay, at most N starts per hour (then paused for the rest of the hour with an alert), scheduled daily or weekly restart.
- **Program**: executable, arguments, start-in folder, process name, where it runs (the signed-in desktop or hidden in the service), priority. A program run hidden in the service runs as Local System and is only started from a folder only administrators can change. A program that asks for administrator rights is started with the signed-in user's full rights only when **Allow elevated start** is ticked (that skips the Windows consent prompt); it must then also be in a protected folder. A program already running (the same file, in the same session) is left alone; only processes of that file are ended on a restart.
- **Windows service**: the service name (Choose... lists them). Stopped or paused: started again.
- **Health check** (optional): an address that must keep answering while it runs: `http(s)://...` (any answer below HTTP 500 counts; a login goes in the User name and Password fields, never in the address, and is sent over plain `http://` to another computer only when **Send the login unencrypted** is ticked) or `tcp://host:port`. No good answer for N seconds after the start-up grace period = an alert and optionally a restart. Catches a program that runs but hangs.
- Recovery (running again, health check answering again) sends "back to normal" to rules that ask for it.

## Scripts
Command (`.ps1` through PowerShell with execution policy bypassed, `.bat/.cmd` through cmd, `.vbs` through cscript, anything else run directly), arguments, start-in folder, and a trigger: on a schedule, when the service starts, or once for each new file in a folder (pattern, sub-folders, unchanged for N seconds, at most N runs a minute). Arguments can use `{source}`, `{destination}` and, for new files, `{file}`, `{name}`, `{stem}`, `{folder}` (always quoted); the environment variables `WK_SOURCE`, `WK_DESTINATION` and `WK_FILE` carry the same values. `.bat` and `.cmd` scripts cannot use the file placeholders, because cmd.exe would read characters in a file name as commands: they read `WK_FILE` (use `!WK_FILE!` with delayed expansion). New files whose names start with `-` or `/` or contain `% ! ^ & | < >` are skipped with an alert. Runs in the service (as Local System, only from a folder only administrators can change and named by its full path; output goes to the log) or in the signed-in user's session. Timeout, success exit codes and priority per script.

## Vertical conversion
Watches a folder and makes a portrait version of each recording with ffmpeg (set the ffmpeg and ffprobe paths under Service & settings). Codec, profile, rotation, resolution, frame rate, bitrate and audio are set on the page; the GPU is detected. The new file is verified (size and length) before the original is deleted, kept or moved, and gets the original's Date Modified so retention counts from the recording. Intel Quick Sync often fails inside a service, so by default ffmpeg is tried in the service, then in the signed-in user's session, then (if enabled) on the CPU. "Test these settings" runs the same attempts and shows each command and its output.

ffmpeg and ffprobe read files other accounts can put in the ingest folder, so they are restricted: only local files (`-protocol_whitelist file`), read as the container the extension names (`.mp4 .mov .m4v .mkv .webm .mxf .ts .m2ts .mts .avi .flv .wmv`; anything else is refused), and, in the service, as the LOCAL SERVICE account with every privilege removed. That account can only write the partial output file of the current conversion (permission granted for the encode and taken away afterwards), so recordings must be readable by it (the `Users` group can read them on most drives). If an encode works in the signed-in session but not in the service, set **ffmpeg runs as** (Service & settings) to *Local System, privileges removed*: the 3.1.0 behaviour, which keeps the Local System account itself.

## GPIO (Axia to vMix)
The service connects to each Axia device (GPIO node, xNode, console engine, or a PC with the IP-Audio driver) over the Livewire Routing Protocol (TCP 93), subscribes to GPI and GPO ports and reconnects on its own.
- **Triggers**: device, GPI / GPO / command message, port and pin. Two action lists, *when it turns on* and *when it turns off*; each action is a vMix function (with the inputs loaded from vMix) or any HTTP request, with an optional wait. *Learn from the device* fills port and pin from the next change. *Ignore repeats within* guards against contact bounce.
- **Live**: every port's pins, recent changes and their results, and *Simulate* to test a trigger without hardware.
- The state reported right after connecting only sets the pins; a reconnect never fires a trigger on its own.

## Studios and Pathfinder failover
A server can run the programs of different studios: for example vMix with `Studio1.vmix` and VVD with the Studio 1 profile, or the same programs with the Studio 2 settings. Pathfinder decides which studio each server runs; Watchkeeper reports back and switches when told. It never fails over on its own.

**Set up on each server** (Service & settings: switch on **Studios**):
1. **Studios** page, *Studios* tab: add each studio with a short code (`S1`, `S2`: what Pathfinder sends).
2. **Watchdog** page: add one entry per program and studio (e.g. *vMix - Studio 1* with `Studio1.vmix`, *vMix - Studio 2* with `Studio2.vmix`) and tick the studio it belongs to. Untagged entries run in every studio.
3. Choose how Pathfinder hears from the server, either or both:
   - **Pathfinder** tab (SapV2, Pathfinder Core PRO port 9600, or 9602 with TLS): a Pathfinder user, and the names of four memory slots: *heartbeat* (a counter written every interval), *status* (`ok`, `degraded`, `switching`, `error`), *active studio* (its code, `none` or `switching`) and *studio to run* (read every interval: the code of the studio Pathfinder wants on this server, or `none`; empty = no instruction). Create the slots in Pathfinder first; **Test connection** reads them. The connection is made by Watchkeeper; nothing listens on the server.
   - **GPIO status** tab: pins on an Axia device, normally the IP-Audio driver on this server (add it on the GPIO page, `127.0.0.1` port 93): a heartbeat pin that changes every interval, an OK pin (low while every program of the active studio runs and answers) and a pin per studio (low while that studio is active).
4. Commands from Pathfinder: the *studio to run* memory slot, or a GPIO trigger (pin or GPO command message) with the action **Switch this server to a studio**.

**A switch**: the old studio's programs are asked to close as if their window were closed, ended after the time-out (default 20 s), then the new studio's programs start at once and the new studio is reported. Switches asked for by Pathfinder or GPIO are at least 30 s apart (setting); an operator can switch on the Studios page at any time. The active studio is kept across restarts. Every switch raises an alert (part *Studios*).

**In Pathfinder** (your logic, not part of Watchkeeper): a Logic Flow per server that restarts a timer whenever its heartbeat slot (or pin) changes; when the timer runs out, write the failed server's studio code into the other server's *studio to run* slot, and show the slots on the studio displays. When the failed server is back, write its studio back where it belongs. Because the slot holds the desired studio rather than a one-off command, a server that restarts picks up what Pathfinder wants.

The SapV2 commands used: `Login <user> <password>`, `set MemorySlots#0.MemorySlot#<name> SlotValue="<value>"`, `get MemorySlots#0.MemorySlot#<name> SlotValue`. The GPIO lines: `LOGIN [password]` then `GPI <port> <pins>` (`l` = low = on, `h` = high, `x` = left alone).

## Command line
```
Watchkeeper.exe                         manager window
Watchkeeper.exe --install | --uninstall install or remove the service (configuration is kept)
Watchkeeper.exe --console               run the engine in a console window (stop the service first)
Watchkeeper.exe --validate <file.json>  check a configuration file; exit code 0 = fine
Watchkeeper.exe --deploy [--config <file.json>] [--profile studio|watchdog] [--server-name <name>] [--allow-untrusted-paths]
Watchkeeper.exe --migrate-from-studiomanager   import the configuration of the earlier name (checked first)
Watchkeeper.exe --export-template <file.json>   current settings without passwords and server name
Watchkeeper.exe --write-starter <file.json> [--profile studio|watchdog]
Watchkeeper.exe --service               used by Windows to run the service
  --data-dir <folder>                     use another configuration folder
```

## Configuration file
snake_case JSON (version 4) with sections `general` (including `modules`, the parts in use), `folders`, `programs`, `scripts`, `vertical`, `gpio`, `studios` and `alerts` (`destinations`, `rules`). A part that is switched off is not validated. Hand edits are allowed; out-of-range values are clamped and unknown keys ignored. Check an edited file with `--validate`, or use Service & settings > Import, which validates first. Version 2 and 3 files are converted on load; plain-text passwords typed into the file are encrypted by the service when it next reads it. Before every save, import, restore or deploy, and when the file is changed outside Watchkeeper, the settings as they were are kept in `backups\` next to it (the newest 50) and as `config.json.bak`; a save that cannot keep them is refused. Service & settings > **Earlier versions...** shows and restores them.

## Security
Watchkeeper runs as Local System and acts on folders, programs and network services that other accounts can reach, so it is built to keep a standard user, a crafted file name or a network attacker from using it to gain those rights. Report vulnerabilities as described in [SECURITY.md](SECURITY.md).

- **Settings folder.** `C:\ProgramData\Watchkeeper` is created owned by Administrators with access for Administrators and Local System only, before anything is written there. It is opened without following links and held open while its permissions are set, so it cannot be swapped for a link meanwhile. A folder another account created or could change (or a link in its place) is renamed aside as a whole and a new one is created; only configuration and state files an administrator wrote are copied over. Inside a protected folder, files another account wrote are renamed `*.untrusted` and never loaded; links and second names (hard links) are removed. `--data-dir` is refused on network paths and under folders other accounts can rename. If the folder cannot be protected, or only the backup copy of the configuration can be read, the service runs in **safe mode**: nothing runs, alerts still work. Settings saved under the earlier name (Studio Manager) are never taken over automatically; `--migrate-from-studiomanager` imports them after checking who could have written them.
- **What runs as Local System.** Programs and scripts are only run as Local System from a file and folders only administrators can change, and with a start-in folder only administrators can add files to; the manager shows a red note on any entry that is not. Programs the service starts get a search path (PATH) without folders other accounts can add files to, and cmd.exe does not look for commands in the start-in folder. Windows programs are called by their full System32 path, and libraries are loaded from System32 only.
- **ffmpeg and ffprobe** run as LOCAL SERVICE with no privileges at all when the service starts them (setting: Service & settings > ffmpeg runs as), and may only write the one output file of the current conversion.
- **Files in shared folders.** Folder policies, the vertical conversion and new-file triggers never follow links and never delete, overwrite, create or hand on files through them; the folders worked in are held open so they cannot be swapped meanwhile. File names never reach a command line unquoted, and never reach cmd.exe at all.
- **Secrets.** Encrypted with the service's own key, never sent between the manager and the service (references are sent instead), removed from exports, and kept out of the log, traces and the Event Log.
- **Control channel.** The local pipe between manager and service is created so another program cannot take its name, and the manager checks that the other end is the Watchkeeper service before it sends anything. Requests are limited in size and nesting.
- **Studios.** A command from Pathfinder or GPIO can only choose one of the studios set up on the server; it cannot start anything else. The Pathfinder login goes over TLS (port 9602, certificate checked or pinned by fingerprint) or, on port 9600, only when "Send the login unencrypted" is ticked. Watchkeeper connects out to Pathfinder; nothing new listens on the server.
- **Network.** TLS 1.2 with revocation checks, `https://` for webhooks and Jira, no login over unencrypted email connections, no login over plain `http://` to another computer unless allowed per entry (vMix only offers http), no redirects, no Windows login sent to proxies unless allowed. Alert text is escaped per format; addresses in chat alerts cannot be clicked.
- **Watchdog.** Programs go only to the console session or the auto-logon user's session, never to another Remote Desktop session; elevation without the consent prompt needs **Allow elevated start**; restarts end only processes of the configured file.
- **Releases.** Built on GitHub Actions with pinned actions, published with `SHA256SUMS` and build provenance (`gh attestation verify Watchkeeper.exe --repo <owner>/<repo>`); signed when a code-signing certificate is configured.

Deployment checklist: keep programs, scripts and ffmpeg in Program Files; give the operator account no administrator rights; put Axia devices and vMix control on an isolated network (the Livewire protocol and the vMix Web Controller have no encryption, so their passwords cross the network readable); use `https://` destinations; delete deploy files that contain plain-text passwords.

## Build and test
C# 5, .NET Framework 4.x, Windows Forms. No NuGet packages.
- **Windows**: `build.bat` compiles with the `csc.exe` included in Windows into `dist\Watchkeeper.exe`.
- **Linux / Mono**: `build.sh` compiles against the .NET Framework 4.5 reference assemblies and checks every referenced type with `tools/check_typerefs.py`.
- `tests/win/WinChecks.cs`: security checks that need real Windows (permissions, junctions, hard links, the control channel, reduced tokens); the GitHub Actions Windows job runs them and also installs the service on the runner.
- `test.sh`: core tests (folder rules, scheduling, watchdog, scripts, conversion with a fake ffmpeg, GPIO against a simulated Axia device, SMTP and webhook against local servers, Jira against a local fake, configuration, deploy).
- `build-uitest.sh`: drives the real manager window against the real engine over the pipe (needs Xvfb, see the script).

## Status
Developed and tested on Linux with Mono. The Pathfinder link was tested against a local imitation of Pathfinder Core PRO's SapV2 protocol built from its published documentation, not against a real Pathfinder; check it with **Test connection** before relying on it. The Windows-only security code (file permissions, link handling, the control channel, reduced tokens, LOCAL SERVICE for ffmpeg, service installation) is exercised by the Windows job in GitHub Actions; that job has not yet been run at the time of the 3.2.2 source release. Not exercised automatically: `deploy.cmd` and `deploy-remote.ps1`, starting programs in a signed-in session, real GPU encodes inside a service, high-DPI rendering. GPIO was tested against a simulated LWRP device built from the published protocol, Jira against a local imitation of its REST API, email against local SMTP servers, webhooks against a local HTTP listener. Reports and pull requests are welcome.

## License
[MIT](LICENSE)
