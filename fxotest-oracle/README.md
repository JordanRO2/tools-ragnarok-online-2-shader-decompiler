# fxotest-oracle

Reliable per-technique round-trip oracle for the fx_2_0 effect decompiler. A copy of
`../fxotest` with three changes that make the CHARACTER effects comparable (the pristine
`fxotest` gives contaminated results for char effects after the SkinBone flattening fix).

## What it does differently vs fxotest
1. **Renders every technique** and diffs each one (pristine fxotest only rendered technique 0).
   Prints one `RESULT` line per technique with `covO`/`covR` (covered pixel counts for
   original/recompiled) and `blkO`/`blkR` (drawn-but-near-black pixels).
2. **Feeds identical register data to both effects.** The only parameter whose reflected D3D
   class changed across the skinning fix is `SkinBone` (`float4x3[30]` original vs `float4[90]`
   recompiled). It is forced to **identity bones**, which is transpose-invariant, so it can never
   spuriously diverge. Skinning index/stride logic is still exercised (verts × identity bone).
   `--skinmode 0` (default) is validated correct: mode 1 collapses the original's geometry.
3. **Feeds a realistic asymmetric perspective + view** to the named transform matrices
   (`matView`, `matProj`, `matViewProj`, `matWorld*`, `InvView`) so a transpose/orientation bug
   in the vertex transform diverges instead of hiding behind a symmetric identity.

## Usage
```
dotnet build fxotest-oracle -c Release
fxotest_oracle.exe <original.fxo> <recompiled.fxo> [--tech N] [--dump prefix] [--skinmode 0|1]
```
Verdict per technique: `maxDiff<=2` MATCH, `<=12` CLOSE, else DIFF.

## Validation
- `Rag2ObjectShader_Default` (non-skinned baseline): all 8 techniques MATCH (maxDiff=0) under both
  near-identity and realistic-perspective matrices — proves the harness + `transpose(M)`
  reconstruction are faithful when the decompile is correct.
- A faithful round-trip reads ~0; the broken char main technique stands out at maxDiff>120.

## Caveat
ZOnly / transparent techniques write no color to the RT here (depth-only / blend), so they show
`covO=0 covR=0` and match vacuously — the harness cannot observe their VS defect. Only the main
color-writing techniques are actually verified.
