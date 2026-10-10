# Packaging Branch

This repository uses the `codex/package` branch to store packaged builds.

## Create a package

Run:

```powershell
powershell -ExecutionPolicy Bypass -File .\package.ps1
```

This creates:

- `package-YYYYMMDD-latest/AudioOutputSwitcher-stage-YYYYMMDD-HHmm/`
- `package-YYYYMMDD-latest/AudioOutputSwitcher-package.zip`

The ZIP is the portable candidate. To build the protected Program Files installer, install Inno Setup 6 and run:

```powershell
powershell -ExecutionPolicy Bypass -File .\build-installer.ps1
```

The installer is written to `artifacts\installer-output` with a SHA-256 sidecar. Do not call it a release until the install, upgrade, uninstall, signature, and lifecycle matrix has passed.

## Branch policy

- Keep package outputs on the `codex/package` branch.
- Commit the packaged files together with the source changes that produced them.
- Use GitHub Desktop to review, commit, and push package updates on this branch.
