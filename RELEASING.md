# Releasing Quickening

Quickening is distributed with [Velopack](https://velopack.io): a per-user
`Setup.exe` plus silent background auto-updates served from this repo's GitHub
Releases.

## One-time setup

1. Install the Velopack CLI: `dotnet tool install -g vpk`
2. Create a GitHub personal access token with `repo` scope and set it:
   `setx GITHUB_TOKEN <token>` (reopen the shell afterward).
3. (Optional) Set up SSH key auth to `ypemnnws@162.0.209.39` so the installer
   upload doesn't prompt for a password.

## Cut a release

From the repo root:

```powershell
.\publish.ps1 1.0.1
```

This builds a self-contained win-x64 release, packs it, creates the GitHub
Release `v1.0.1`, and uploads the installer to `quickening.app/download`.
Use `-NoServerUpload` to skip the website upload.

## Verify auto-update (do this once end-to-end)

1. `.\publish.ps1 1.0.0`, then run the resulting `artifacts\releases\*Setup.exe`
   to install Quickening (installs to `%LocalAppData%\Quickening`).
2. `.\publish.ps1 1.0.1`.
3. Launch the installed 1.0.0. It checks in the background and stages 1.0.1.
4. Quit, then relaunch. It is now 1.0.1 and shows the "Updated to v1.0.1" note once.
   (Confirm the version in Settings → About.)

## Notes

- Unsigned: first launch shows SmartScreen "Run anyway". Expected until a
  signing cert is in place.
- The GitHub Release holds the update feed (`RELEASES` + `*-full`/`*-delta`
  nupkgs). `quickening.app/download` self-hosts the `Setup.exe` for first-time
  installs; `download.php` is unchanged.
