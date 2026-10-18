# Changelog

## 3.1.1 (security release)
Fixes found in a second security review of 3.1.0. Upgrading is recommended for every installation.

High:
- Settings folder: it is opened once without following links and held while it is checked; owner and permissions are set through that open handle, and so is everything inside it. A settings folder that another account could change, or a link in its place, is no longer repaired in place: it is renamed aside (`Watchkeeper.untrusted-<date>`), a new folder is created with the right permissions, and only configuration and state files an administrator wrote are copied into it. Second names (hard links) inside it are removed. The privileges this needs are switched on only for that moment.
- Programs and scripts that run as Local System: a start-in folder must be one only administrators can add files to (Windows loads DLLs from it, cmd.exe looks there for commands). The service's search path (PATH) for the programs it starts leaves out folders other accounts can add files to, folders that do not exist and relative entries (each is logged), and cmd.exe no longer looks for commands in the start-in folder.
- New-file triggers: the watched folder and every folder above it must not be links, and they are held open while the folder is read and while the script runs. A file in a sub-folder is only passed on when its folder is held too and the file is not a link or a second name.
- Folders and partial output files that are "held open" are now really protected against being renamed or deleted: Windows ignored the sharing mode of the handles 3.1.0 used (attribute access only).

Medium:
- ffmpeg and ffprobe run by the service as **LOCAL SERVICE** with every privilege removed (also SeImpersonatePrivilege), in a window station of their own, instead of a restricted Local System token. The conversion's partial output file is opened to that account for the duration of the encode only. New setting `general.ffmpeg_account` (Service & settings > ffmpeg runs as): `local_service` (default) or `restricted_system` (the 3.1.0 behaviour).
- Custom webhook templates: placeholders are filled in one pass, so text inside an alert (a file name containing `{server}`, say) is no longer replaced or able to break the JSON.
- A login (user name and password, or an `Authorization` header) is no longer sent over plain `http://` to another computer: health checks, GPIO HTTP actions and vMix. Over `https://` or to this computer it works as before; otherwise tick **Send the login unencrypted** for that entry (`allow_plain_login` / `health_allow_plain_login`).

Low:
- Certificates are checked for revocation (webhooks, Jira, email). A revoked certificate is refused, also when its fingerprint is trusted; an unreachable revocation server alone does not fail the connection.
- `--data-dir` is refused on a network path or under a folder other accounts can rename, delete or re-permission.
- Addresses in chat alerts (Slack, Teams, Discord, Mattermost) cannot be clicked: `https://` is shown as `https[:]//`, `www.` as `www[.]`, and names that look like a domain (`report.zip`) get an invisible break after the dot.
- `deploy-remote.ps1` also notes webhook addresses typed into the configuration in plain text.

Compatibility:
- Recordings must be readable by LOCAL SERVICE for the vertical conversion (the `Users` group can read them on most drives). If the conversion or "Test these settings" fails only in the service (hardware encoders in particular), set **ffmpeg runs as** to Local System.
- vMix on another computer with a Web Controller password, and http health checks or GPIO HTTP actions with a login to another computer, need **Send the login unencrypted**; configuration files with such entries fail `--deploy` and `--validate` until `allow_plain_login` is set.
- Scripts and programs run as Local System with a start-in folder other accounts can write to stop running until the folder is protected or the start-in folder is cleared.
- A script that renames or deletes its watched folder, or a sub-folder the current file is in, while it runs gets "access denied".
- Settings folders that 3.1.0 repaired in place are left as they are; only an unsafe one is moved aside.

## 3.1.0 (security release)
Fixes found in a security review. Upgrading is recommended for every installation.

Critical and high:
- The settings folder is created owned by Administrators with access for Administrators and Local System only, before anything is written; a folder or files planted by another account are taken back or set aside (`*.untrusted`). The service runs nothing (safe mode) while the folder is not protected or only the backup configuration can be read.
- Settings of the earlier name are no longer taken over automatically; `--migrate-from-studiomanager` imports them after checking who could have written them. The earlier service is only removed when it really is that program.
- Folder policies and the vertical conversion never follow junctions, symbolic links or mount points, refuse hard links, and hold every folder open while working in it.
- File names no longer reach command lines unquoted; `.bat`/`.cmd` scripts cannot use file placeholders (use `WK_FILE`); dangerous file names are skipped with an alert.
- Programs, scripts, ffmpeg and ffprobe run as Local System only from places only administrators can change. Programs are no longer elevated without the consent prompt unless "Allow elevated start" is ticked, and never start in another user's Remote Desktop session.
- ffmpeg and ffprobe read only local files, as the container the extension names, with privileges removed; ffmpeg in the user session reports through pipes instead of files in the user's TEMP folder.
- Proxies no longer receive the computer's Windows login unless allowed; new destinations connect directly.
- The control channel cannot be taken over by another program, the manager checks the service before sending anything, and secrets are not sent over it.

Medium and low:
- Secrets are encrypted with the service account's own key (converted automatically); webhook addresses, extra headers and proxy addresses are now secrets too; exports remove them; traces, the log and the Event Log no longer contain them.
- TLS 1.2 only; `https://` required for webhooks and Jira; no email login without TLS; "Accept any certificate" replaced by trusting one certificate fingerprint; redirects are not followed.
- Alert text is escaped for Slack, Teams, Discord and Jira, so it cannot add links or mentions.
- Watchdog matches programs by file and session, so only processes of the configured program are ended.
- GPIO lines are limited to 8 KB; JSON nesting to 64 levels; control requests to 1 MB.
- New-file triggers run at most N times a minute (default 30) and alert on a backlog.
- Windows system programs are called by full path; libraries load from System32 only; Watchkeeper refuses to run elevated from a folder other accounts can change.
- Deploy: hash check and clean-up in deploy-remote.ps1, warnings for plain-text passwords, a list of what a configuration runs, refusal of unprotected paths unless `--allow-untrusted-paths`.
- Releases: pinned GitHub Actions, SHA256SUMS, build provenance, optional code signing, and a Windows job that tests the Windows-only security code.

Compatibility:
- Configuration version 4 (older files are converted).
- `.bat`/`.cmd` scripts that use `{file}`, `{name}`, `{stem}` or `{folder}` must switch to `WK_FILE`.
- Destinations using an authenticated proxy need "Send this computer's Windows login to the proxy".
- `http://` webhook and Jira addresses (other than this computer), email logins without TLS and "Accept any certificate" stop working until changed.
- Programs and scripts in folders other accounts can change stop running until moved or protected. Scripts and programs that run as Local System (or elevated) must be given by their full path.
- Watchdog entries that relied on silent elevation need "Allow elevated start".

## 3.0.0
- New name: Watchkeeper. Service `Watchkeeper`, program folder `C:\Program Files\Watchkeeper`, data folder `C:\ProgramData\Watchkeeper`.
- Alerts rebuilt around **destinations** and **rules**:
  - any number of destinations: Windows Event Log, email (SMTP), webhook, Jira issue;
  - rules pick all alerts or chosen parts and entries, a minimum severity, recoveries, destinations, a cooldown and "stop here".
- Jira destination (Cloud and Data Center). Each problem opens one issue; repeats and "back to normal" are added to it as comments.
- Webhook format for Jira Automation incoming webhooks (secret header). Extra request headers on any webhook.
- "Back to normal" alerts when:
  - a program or service runs again, or its health check answers again;
  - a GPIO device reconnects, or a GPIO action works again;
  - a folder policy or script succeeds after a failure.
- Recent alerts show severity, part and entry, the rules that took each alert, and every destination's answer.
- Scripts get `WK_SOURCE`, `WK_DESTINATION` and `WK_FILE` environment variables.
- Configuration version 3. Version 2 alert settings (one email, one webhook) are converted on load.
