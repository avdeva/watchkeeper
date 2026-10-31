# Security

Watchkeeper runs as a Windows service (Local System) and starts programs, so its settings decide what runs with high rights.

How it is protected (set at installation by `deploy.cmd` / `Watchkeeper.exe --install`):
- The program is installed in `C:\Program Files\Watchkeeper`, where only administrators can change files.
- `C:\ProgramData\Watchkeeper` (settings, backups, logs, status, commands) is open only to SYSTEM and Administrators. Running the installation again resets these permissions; the self-test reports any other account that has access.
- The manager needs administrator rights.
- Passwords and tokens in the settings are encrypted with Windows DPAPI (machine key). Exported settings leave them out.
- E-mail passwords are never sent over an unencrypted connection.

Whoever can administer the server can make Watchkeeper run any program; protect administrator accounts accordingly. Programs that Watchkeeper starts should be kept in folders only administrators can change (such as Program Files).

Report security problems through the repository's private vulnerability reporting, or to the maintainer by e-mail, not in a public issue.
