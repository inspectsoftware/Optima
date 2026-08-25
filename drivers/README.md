# Bundled virtual display driver

This folder ships inside the build. Optima installs the driver from here on demand from
the Display page: one click, one administrator prompt, no Device Manager, no `devcon`, no
manual INF right-click.

## What is bundled

```
drivers/VirtualDisplayDriver/
  x64/    MttVDD.inf  mttvdd.cat  MttVDD.dll
  arm64/  MttVDD.inf  mttvdd.cat  MttVDD.dll
```

| | |
|---|---|
| Driver | Virtual Display Driver (IddCx indirect display) |
| Provider | MikeTheTech |
| Version | 11.30.4.434 (12/24/2024) |
| Hardware id | `Root\MttVDD` |
| Device class | Display |
| Signed by | SignPath Foundation (catalog verifies as Valid) |
| Size | about 550 KB total |

Taken from the upstream *VDD Control* distribution. Only the driver payload is kept. The
`VDD Control.exe` GUI (171 MB) and the bundled `devcon.exe` were deliberately left out:
Optima performs the install itself, so neither is needed, and `devcon` comes from the WDK
whose licence restricts redistribution. The distribution's virtual *audio* driver is also
excluded, being unrelated to this application.

**Before distributing a build, confirm the upstream licence permits redistribution and add
the required attribution.** This repository vendors the binaries for convenience; it does
not grant you any rights to them.

## How the package is chosen

A distribution can hold several INFs, so selection is explicit rather than "first file
found". `VddDriverInstaller.FindBundledPackage` requires the package to be **Display
class** and to **target the running architecture**, read from the INF's `Manufacturer`
decoration (`NTamd64`, `NTARM64`). Where an INF lists several hardware ids, the
root-enumerated one wins, since that is the only kind the installer can create.

## What Optima does on install

All of this runs inside the elevated helper:

1. `pnputil /add-driver <inf> /install` stages the package into the Windows DriverStore.
2. Creates the **root-enumerated device node** via SetupAPI. This is the step people
   normally need `devcon` for: an IddCx display is enumerated by ROOT rather than by a bus,
   so staging the package alone installs a driver that never produces a device.
3. Writes a default `vdd_settings.xml` if none exists. An existing file is never overwritten.

Uninstall reverses steps 2 and 1, and is available from the Display page once installed.

The helper writes its own log to `%LOCALAPPDATA%\Optima\logs\optima-elevated-<date>.log`.
It runs elevated with no console, so without that log every failure inside it is invisible
to the caller.

## Signing

Windows will not load a driver package whose catalog is not signed by a publisher the
machine trusts. If a package here has no `.cat`, or is signed by an untrusted certificate,
the install fails and Optima reports it rather than continuing silently. Optima will not
disable driver signature enforcement or install certificates on your behalf; those are
deliberate security decisions belonging to whoever runs the machine.

## Replacing or removing the package

Drop a different package in and Optima picks it up on the next launch, provided it is
Display class and targets the right architecture. Remove the folder entirely and Optima
still runs: the Display page says nothing is bundled, virtual display features fall back to
the fully functional mock provider, and everything else is unaffected.
