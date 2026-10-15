# Building Skill Cap 100

This repository contains the complete source code for the **Skill Cap 100** mod for
SPT 4.1.6 (EFT 0.16.9.5.40743), exactly as it was used to build the binaries in the
published release `v1.0.5`.

> Note on layout: the original working tree used `src/` and `tools/` folders
> (`src/SkillCap100.Server`, `src/SkillCap100.Client`, `tools/SkillCap100.Verify`).
> In this repository the three projects live at the repository root instead, so the
> `dotnet build` commands below differ from the paths hard-coded inside `build.ps1`
> (`build.ps1` is kept for reference; use the commands below, or place the projects
> back under `src/` and `tools/` to run it unchanged).

## Contents

| Path | What it is | Output |
|---|---|---|
| `SkillCap100.Server/` | Server mod (`net10.0`), SPT `IOnLoad` module + Harmony patches for the skill XP/level persistence and the hideout gym | `SkillCap100.Server.dll` |
| `SkillCap100.Client/` | BepInEx 5 / HarmonyX client plugin (`netstandard2.1`), skill cap, elite rules, UI state, stimulator bounds, Skill Distribution compatibility | `SkillCap100.Client.dll` |
| `SkillCap100.Verify/` | Offline verifier: checks every Harmony patch target and IL constant against the game assemblies before deploying | `SkillCap100.Verify.exe` |
| `SkillCap100.Server/config.json` | Default server-side configuration shipped in the release | - |

## Requirements

* SPT 4.1.6 (EFT 0.16.9.5.40743) installation, including `EscapeFromTarkov_Data/Managed`
  and the SPT server assemblies.
* .NET SDK 10 (for the server mod) - the client plugin targets `netstandard2.1` and is
  built with the same SDK.
* BepInEx 5.4.x (for the client plugin reference assemblies).

Both `.csproj` files resolve their game references from the `GameRoot` MSBuild property,
which defaults to the author's install path. Pass your own path:

```powershell
dotnet build .\SkillCap100.Server\SkillCap100.Server.csproj -c Release /p:GameRoot="D:\SPT"
dotnet build .\SkillCap100.Client\SkillCap100.Client.csproj -c Release /p:GameRoot="D:\SPT"
dotnet build .\SkillCap100.Verify\SkillCap100.Verify.csproj -c Release /p:GameRoot="D:\SPT"
```

A full build (compile all three, run the static verification, then deploy to the game
folder) is what `build.ps1` does:

```powershell
pwsh -File .\build.ps1 -GameRoot "D:\SPT"            # build + verify + deploy
pwsh -File .\build.ps1 -GameRoot "D:\SPT" -NoDeploy  # build + verify only
```

The verifier is optional but recommended: it fails the build if a Harmony patch target
or a hard-coded IL constant no longer matches the game assemblies (this is what protects
the mod against SPT updates silently breaking the patches).

## Build outputs and the shipped release

Release `v1.0.5` (`SkillCap100-v1.0.5-SPT4.1.6.zip`) contains:

```
BepInEx/plugins/SkillCap100/SkillCap100.Client.dll
SPT_Runtime/user/mods/SkillCap100/SkillCap100.Server.dll
SPT_Runtime/user/mods/SkillCap100/config.json
```

| Shipped file | Built from | Size | SHA-256 |
|---|---|---|---|
| `BepInEx/plugins/SkillCap100/SkillCap100.Client.dll` | `SkillCap100.Client/SkillCap100.Client.csproj` (Release, `netstandard2.1`) | 32768 | `322FA79034207A44BA9EAF70B2F3156DD26D431B9FD921D7D7D32D8515EDE326` |
| `SPT_Runtime/user/mods/SkillCap100/SkillCap100.Server.dll` | `SkillCap100.Server/SkillCap100.Server.csproj` (Release, `net10.0`) | 21504 | `9C51514BC5E771AEBA087DD6C0A0AA10EB270A8DA806BD88C7AA40A3537AB2A4` |

`config.json` is copied unchanged from `SkillCap100.Server/config.json`.

Verify a local build against the shipped binaries with:

```powershell
Get-FileHash .\SkillCap100.Client\bin\Release\netstandard2.1\SkillCap100.Client.dll -Algorithm SHA256
Get-FileHash .\SkillCap100.Server\bin\Release\net10.0\SkillCap100.Server.dll -Algorithm SHA256
```

## What the code does (short version)

* Server: raises the persisted skill experience cap from 5100 to 10000 (level 51 -> 100)
  in `CommonSkill.Progress` / session counters, in the profile-add path and in the gym
  workout XP filter; a profile migration patch keeps existing profiles consistent.
* Client: raises the client-side clamps (`BaseSkill.ClampCurrent`, `SummaryLevel`,
  `SkillsDescriptor`), rewrites `Skill.UpdateRules` so elite effects unlock at the
  configured level, keeps the level number visible from 51 to 99, patches the four UI
  elite-state methods, fixes the stimulator level-buff bounds (the vanilla bounds assume
  a 60 level cap, which clamps a positive buff into a negative one above level 60), and
  patches Skill Distribution's hard-coded `5100` "maxed" threshold when that mod is
  installed.

Configuration lives in `SkillCap100.Server/config.json` (server) and
`BepInEx/config/com.skillcap100.client.cfg` (client, also editable in-game with F12).

## License

MIT - see `LICENSE`.
