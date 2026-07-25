---
description: Build, publish, and package a release zip via scripts/package.ps1
---

Run `scripts/package.ps1` using the PowerShell tool from the repo root:

```powershell
./scripts/package.ps1
```

This cleans the project, publishes a self-contained single-file win-x64
Release build (`scripts/publish.ps1`), then stages and zips it into
`dist/MouseApp_v<version>.zip`.

- If the build or publish step fails, surface the compiler/publish errors
  verbatim — do not attempt to fix code issues unless asked.
- On success, report the final zip path and version number.
