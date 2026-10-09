# vendor/shield

`Optima.Shield.exe`, the protected play module, goes here before an official build.

It is built from a private repository and is never committed to this one (see `.gitignore`). When
the file is here, the build copies it next to `Optima.exe` and writes its SHA-256 into the elevated
helper, which starts no other file. When it is not here, which is the case for any build made from
this repository alone, Optima builds and runs without protected play and its Settings page says so.
