# tools-ragnarok-online-2-shader-decompiler

Decompiles the legacy Direct3D 9 effect shaders (`.fxo`, `fx_2_0`) shipped with the Ragnarok
Online 2 client back into **recompilable HLSL**. All 39 of the client's effect shaders — including
the character shaders (GPU skinning + preshaders), terrain, water, sky and post-processing —
round-trip: `.fxo` → HLSL → `fxc /T fx_2_0` → a valid, client-loadable `.fxo`.

This is a fork of [spacehamster/DXDecompiler](https://github.com/spacehamster/DXDecompiler)
(MIT — see `DXDecompiler/LICENSE`) with a set of fixes to the D3D9 / FX9 code paths so its output
actually recompiles. `fxo2hlsl` is a small CLI wrapper around the library.

## Layout
- `DXDecompiler/` — the forked decompiler library (patched).
- `fxo2hlsl/` — CLI runner: `.fxo`/`.o` → `.hlsl` (or raw D3D9 assembly).
- `fxotest/` — a D3D9 render-diff harness that validates *behavioural* equivalence: it renders
  the original and the recompiled `.fxo` through the same deterministic reference (REF) device
  with identical inputs and diffs the pixels (needs `SharpDX.Direct3D9`). Usage:
  `fxotest <original.fxo> <recompiled.fxo>`.

## Build
Requires the .NET SDK (net10.0).

```
dotnet build fxo2hlsl -c Release
```

## Usage
Decompile a shader to HLSL:

```
dotnet fxo2hlsl/bin/Release/net10.0/fxo2hlsl.dll <input.fxo> <output.hlsl>
```

Raw D3D9 assembly instead of HLSL (append `-a`):

```
dotnet fxo2hlsl/bin/Release/net10.0/fxo2hlsl.dll <input.fxo> <output.asm> -a
```

Recompile the HLSL back to a client-loadable `.fxo` with the Windows SDK `fxc`:

```
fxc /nologo /T fx_2_0 output.hlsl /Fo output.fxo
```

> On Git Bash, run `fxc` from PowerShell or cmd — MSYS rewrites the `/T` and `/Fo` flags into paths.

## Round-trip semantics
The round-trip is **semantic, not byte-identical**. The recompiled `.fxo` is a valid, loadable,
functionally-equivalent effect, but its bytes differ from the original: `fxc` re-runs register
allocation, regenerates preshaders from the reconstructed HLSL, and re-optimizes with a newer
compiler than the one that built the original assets. This is intentional — the goal is *editable*
HLSL you can recompile, not a bit-preserving copy.

## Fixes over upstream (what enables the round-trip)
Emission / structure:
- Empty texture initializer; sampler `<<>>` double-wrap; duplicate global declarations across an
  effect's shaders; raw `PRSI` metadata dump; sampler/texture type casing (`sampler2D`,
  `samplerCUBE`); per-shader input/output struct names; zero-initialized output structs and
  temporaries.
- Constant-register vs sampler disambiguation; `int` params that live in the float (`c`) register
  file.
- Pass render-states driven by preshader expressions; `string` annotations misclassified as shaders
  (`compile ps_0_0`); null shader blobs.

Reconstruction:
- **Preshaders** (FXLVM) reconstructed inline as HLSL — `fxc` re-folds the uniform-only math back
  into a preshader.
- **GPU skinning**: the vertex address register `a0` (aliased to the texture register in the D3D9
  enum) and relative constant addressing `c[a0.x]` for bone matrices
  (`transpose(SkinBone[a0.x / 3])[col]`).
- Mask-aligned source swizzles; scalar `cmp` / `if` conditions; pixel-shader input semantics
  (`v#` → `COLOR`); `RastOut` fog / point-size outputs remapped off the illegal `POSITION1`.

## Status
- **Compile round-trip:** 39/39 RO2 client effect shaders recompile with `fxc /T fx_2_0`.
- **Behavioural round-trip** (`fxotest`, REF device, pixel-exact diff): 26/39 render bit-identically
  to the original. The `fxotest` harness drove five correctness fixes: lrp argument order; dropped
  `_sat` result modifier; `def`-constant vectors truncated in `dp3`/`dp4`; `dp3` emitted as a
  4-component `dot()` (fxc widened it to `dp4`, pulling in a live `.w`); and `def`-constant vectors
  read at the first-N swizzle slots instead of the destination write-mask channels (non-prefix masks
  like `.xz`). The remaining 13 are the GPU-skinning character shaders, whose bone-matrix relative
  addressing (`c[a0.x]`) fxc re-lowers into a numerically-divergent sequence — a known limitation of
  reconstructing low-level register addressing back into HLSL matrix-array indexing. Every
  non-skinning effect (all pixel-shading: colours, lighting, post-processing, terrain, water) is
  behaviour-exact.
