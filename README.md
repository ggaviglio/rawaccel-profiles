# rawaccel-profiles — per-app profile switching for Raw Accel

Applies a different [Raw Accel](https://github.com/RawAccelOfficial/rawaccel)
preset, sensitivity and Windows pointer speed depending on which application has focus,
and switches back when you leave. It can also help compute those settings, so a new
profile matches the feel of your main setup.

Windows 10/11 (64-bit) · no installer · MIT licensed

## Getting started

1. Download the latest `RawAccelProfiles_v*.zip` from Releases.
2. Extract into your Raw Accel folder for automatic detection, or extract elsewhere and
   set it manually in Settings.
3. **Set up Default** — the profile you start with. This is your baseline: your normal
   sensitivity and pointer speed.
4. **Add a profile** with **+**.
5. **Give it a rule.** Click **Manage…** under *App Rules* and pick from your open
   windows, or add a rule manually.
6. **Switch to that app.** The profile applies, and reverts to Default once all matching
   windows are closed.

## How it works

Switch to an app and it loads that profile's Raw Accel preset and sensitivity, and
sets Windows' pointer speed to go with it. Leave the app and everything returns to your
Default.

You can set those values yourself, or let the app work them out for you.

It adjusts three layers, in this order:

| Layer | What it does |
|---|---|
| **Raw Accel** | Swaps the profile's preset into Raw Accel's folder as `settings.json`, patches in the sensitivity multiplier and Y/X ratio, then applies it with `writer.exe`. Raw Accel's driver forgets its config on reboot — turn on **Apply Raw Accel Settings on Startup** and it gets reapplied on launch. |
| **Windows Pointer Speed** | The OS's own 1–20 slider, computed or set by hand per profile. |
| **Registry curve** | Closes the gap Windows' integer pointer-speed steps can't, using a [MarkC no-acceleration curve fix](https://donewmouseaccel.blogspot.com/2010/03/markc-windows-7-mouse-acceleration-fix.html). **Needs a logoff to take effect** — the other two switch live. |

Each profile decides which layers to compute automatically, set by hand, or skip.

## Advanced fields

Y/X ratio, EPP and target screen size are hidden until you turn them on in Settings.

Give a profile a target screen size and it can compute Y/X ratio compensation for a
stretched or non-native resolution. That value only feeds the math — your actual
resolution is never changed.

## Building from source

Requires the .NET 10 SDK. Windows only — the app uses `user32.dll`,
`SystemParametersInfo` and the registry directly.

```powershell
# Build
dotnet build src/RawAccelProfiles/RawAccelProfiles.csproj -c Debug

# Run
dotnet run --project src/RawAccelProfiles/RawAccelProfiles.csproj

# Publish a self-contained single-file win-x64 release build
./scripts/publish.ps1

# Publish and zip into dist/
./scripts/release.ps1
```

Also wired up as VS Code tasks (`build`, `clean`, `publish`, `release`). There is no test
suite; behavioural changes are verified by running the app.

If you're modifying the compensation math, read `CLAUDE.md` first — it documents the
formulas, the precision edge cases, and the bugs already found and fixed in that code.

## Credits

- **[Raw Accel](https://github.com/RawAccelOfficial/rawaccel)** — the acceleration driver
  this app drives for its sensitivity and curve layer.
- **[MarkC's mouse acceleration fix](https://donewmouseaccel.blogspot.com/2010/03/markc-windows-7-mouse-acceleration-fix.html)**
  — the source of the registry curve math.
- **[Avalonia](https://avaloniaui.net/)** — the UI framework.

## License

MIT — see [LICENSE](LICENSE).
