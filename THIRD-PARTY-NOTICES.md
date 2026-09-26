# Third-party notices

XingyiStarry MP redistributes the following runtime libraries in both release
archives:

- Google.Protobuf 3.25.3 — BSD 3-Clause license. Source:
  <https://github.com/protocolbuffers/protobuf/tree/v25.3>
- System.Buffers, System.Memory, System.Numerics.Vectors and
  System.Runtime.CompilerServices.Unsafe — MIT license, .NET Foundation and
  Contributors. Source: <https://github.com/dotnet/runtime>

The applicable license texts are stored in `licenses/` in the source repository
and in the `licenses/` directory of the outer release archives. The nested
installer archives contain runtime files only.

## BepInEx convenience bundle

The `with-BepInEx` archive additionally redistributes the unmodified official
`BepInEx_win_x64_5.4.23.5.zip` package:

- Release: <https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5>
- Asset SHA-256: `82F9878551030F54657792C0740D9D51A09500EEAE1FBA21106B0C441E6732C4`
- Corresponding source: <https://github.com/BepInEx/BepInEx/tree/v5.4.23.5>

The upstream package includes these components:

| Component | Version | License | Source |
|---|---:|---|---|
| BepInEx | 5.4.23.5 | MIT | <https://github.com/BepInEx/BepInEx/tree/v5.4.23.5> |
| Unity Doorstop | 4.5.0 | LGPL-2.1 | <https://github.com/NeighTools/UnityDoorstop/tree/v4.5.0> |
| HarmonyX | 2.9.0 | MIT | <https://github.com/BepInEx/HarmonyX/tree/v2.9.0> |
| MonoMod | 22.01.29.01 | MIT | <https://github.com/MonoMod/MonoMod/tree/v22.01.29.01> |
| Mono.Cecil | 0.10.4 | MIT | <https://github.com/jbevain/cecil/tree/0.10.4> |

The outer `with-BepInEx` release archive contains the corresponding license texts
in its `licenses/` directory. The BepInEx and Unity Doorstop binaries are unmodified;
the source links above provide the corresponding source code.
