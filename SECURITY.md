# Security policy

## Reporting a vulnerability
Please report security problems privately through GitHub: on this repository, open the **Security** tab and choose **Report a vulnerability**. Do not open a public issue for them. Include the Watchkeeper version, what an attacker needs (for example a standard user account on the server, or access to the network), and the steps to reproduce.

## Supported versions
Security fixes are made for the latest release. Install updates with Service & settings > Install / update service, or deploy the new exe.

## Security model
Watchkeeper's service runs as Local System. Its configuration decides what runs with those rights, so:
- only Administrators and Local System may read or change `C:\ProgramData\Watchkeeper`;
- programs and scripts run as Local System only from places only administrators can change, with a start-in folder and a search path other accounts cannot add files to;
- ffmpeg and ffprobe, which read files other accounts provide, run as LOCAL SERVICE without privileges (unless configured otherwise) and may write only the output file of the current conversion;
- folders that other accounts can write to (recording drives, ingest and watched folders) are treated as hostile: links are never followed, file names never reach a command interpreter;
- secrets are encrypted with the service's own key and never leave the service in clear text.

An account with administrator rights on the server is trusted: it can change the configuration and therefore what runs as Local System.

## Verifying a download
Each release has `SHA256SUMS` and build provenance:

```
Get-FileHash .\Watchkeeper.exe -Algorithm SHA256
gh attestation verify .\Watchkeeper.exe --repo <owner>/<repo>
```
