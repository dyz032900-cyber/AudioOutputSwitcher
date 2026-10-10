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

## Branch policy

- Keep package outputs on the `codex/package` branch.
- Commit the packaged files together with the source changes that produced them.
- Use GitHub Desktop to review, commit, and push package updates on this branch.
