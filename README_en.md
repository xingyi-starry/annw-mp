# XingyiStarry MP

[简体中文](README.md) | [English](README_en.md)

A multiplayer plugin for Tactical Annihilation, built on BepInEx 5.

The project is still in development and currently targets game version 1.0.12. The host and all clients must use the same plugin and game versions.

## Features

- Public relay and direct TCP connections in the same room.
- A lobby based on the game's skirmish setup, with seat selection and ready checks.
- Joining ongoing matches, automatic reconnection after an unexpected disconnect, and AI takeover of disconnected seats.
- Built-in and custom maps; clients do not need to install the host's custom map.
- Hosting rooms from skirmish saves and saving multiplayer matches.
- Auto-command support, with every peer executing the concrete unit actions selected by the host.

Maps with scripts can be hosted, but their behavior has not yet been systematically verified for multiplayer determinism.

## Installation

Two downloads are available with each release:

- `XingyiStarry-MP-<version>.zip`: the standard package, without BepInEx, for games that already have a compatible BepInEx 5 installation.
- `XingyiStarry-MP-<version>-with-BepInEx.zip`: includes the official BepInEx 5.4.23.5 runtime for Windows x64.

Fully exit the game, then extract the installation files into the directory containing `AnnW.exe`, preserving the folder structure. Start the game and choose “Multiplayer Skirmish” from the skirmish menu to host or join a room.

To uninstall, double-click `Uninstall-XingyiStarry-MP.bat` in the game directory. By default, it removes only this plugin. You can also choose to remove the entire BepInEx framework; that option also deletes other BepInEx mods and their configuration files, so use it with care.

## Development environment

Requirements:

- Windows PowerShell 5.1 or PowerShell 7;
- .NET SDK 8;
- A legally installed copy of Tactical Annihilation 1.0.12;
- Network access to NuGet, GitHub Packages, and GitHub Releases.

Game assemblies are not part of this project and are not committed to the repository. Local development defaults to the game installation in the adjacent `..\Tactical Annihilation` directory. Pass `GameRoot` to use a different location.

The build script automatically downloads and verifies the official BepInEx dependencies. No separate installation is required.

### Build and test

```powershell
.\build.ps1 -Configuration Release -GameRoot 'D:\Games\Tactical Annihilation'
dotnet .\tests\XingyiStarry.Mp.Tests\bin\Release\net8.0\XingyiStarry.Mp.Tests.dll
```

`build.ps1` restores the pinned BepInEx dependencies, builds the solution, and prepares the files needed for a development installation in `dist\`.

### Packaging

```powershell
.\package.ps1 -Configuration Release `
  -GameReferencesTag game-1.0.12 `
  -OutputDirectory .\releases
```

For release packages, use `GameReferencesTag` to select the game reference version. Maintainers must sign in with `gh` using a token with `read:packages` permission, or provide a token through `GH_PACKAGES_TOKEN`.

The script produces both the standard and `with-BepInEx` packages and reports their SHA-256 hashes. Before packaging, confirm that the version number, game references, and test results are up to date.

After a game update, review and update `game-references.json`, then publish the new private reference package:

```powershell
.\tools\Publish-GameReferences.ps1 `
  -GameRoot 'D:\Games\Tactical Annihilation' `
  -Tag game-1.0.12
```

Publishing requires a GitHub token with `write:packages` permission. The reference package is used only for compilation and is not included in plugin release archives.

### Development installation

```powershell
.\install.ps1 -GameRoot 'D:\Games\Tactical Annihilation'
```

Development installation sets the relay endpoint to `127.0.0.1:24555`; run a local relay first. New configurations use direct-host port `24556` to avoid a conflict, while existing direct-host ports are preserved. Pass `-RelayEndpoint 'host:port'` to use another relay. Release packages retain the public relay default.

### Protocol and relay

The sole protobuf definition is `src\XingyiStarry.Mp.Protocol\Protos\multiplayer.proto`. After changing the protocol, run `generate.ps1` in the adjacent relay repository, commit the regenerated Go code, and complete the C#, Go, and cross-language tests.

For the connection, handshake, match entry, reconnection, and shutdown flows, see [Session and protocol lifecycle (Chinese)](docs/session-lifecycle.md).

## License

This project uses the [MIT License](LICENSE).

Third-party notices, including those for the bundled BepInEx distribution, are available in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and [licenses](licenses).
