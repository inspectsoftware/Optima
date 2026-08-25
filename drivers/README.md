# Bundled virtual display driver

Drop a virtual display driver package in this folder and Optima installs it for the user
automatically — one click on the Display page, one administrator prompt, no Device Manager,
no `devcon`, no manual INF right-click.

## What to put here

The complete driver package, flat or in a subfolder:

```
drivers/
  MttVDD.inf     <- required
  MttVDD.cat     <- required in practice; Windows refuses an unsigned package
  MttVDD.dll     <- and whatever else the .inf references
  ...
```

Optima picks the first `.inf` it can read a hardware id from, so keep exactly one driver
package in here.

## Why this folder is empty in the repo

The driver is third-party software with its own licence, so it is not vendored into this
repository. Download the package yourself, satisfy yourself that it is signed and that its
licence permits redistribution, then place it here before building a release. From that
point on it travels inside the build and every end user gets it automatically.

## What Optima does with it

On install (`IDriverInstaller.InstallAsync`, all inside the elevated helper):

1. `pnputil /add-driver <inf> /install` — stages the package into the Windows DriverStore.
2. Creates the **root-enumerated device node** via SetupAPI. This step is what people
   normally need `devcon` for: an IddCx display is enumerated by ROOT rather than by a bus,
   so staging the package alone installs a driver that never produces a device.
3. Writes a default `vdd_settings.xml` if none exists. An existing file is never overwritten.

Uninstall reverses steps 2 and 1.

## Signing

Windows will not load a driver package whose catalog is not signed by a trusted publisher.
If the package here has no `.cat`, or is signed by a certificate this machine does not
trust, the install fails and Optima reports it — it does not silently continue. Optima will
not disable driver signature enforcement or install certificates on your behalf; those are
deliberate security decisions that belong to whoever runs the machine.

## Without a package

Optima still runs. The Display page reports that no package is bundled, virtual-display
features fall back to the fully functional mock provider, and every other feature —
detection, launching, performance profiles, monitoring, recovery — is unaffected.
