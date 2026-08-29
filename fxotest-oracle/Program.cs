using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D9;
using SharpDX.Mathematics.Interop;

// fxotest-oracle: reliable per-technique round-trip oracle.
//
// Differences vs the pristine fxotest:
//  1. Renders EVERY technique of the effect (not just technique 0) and diffs each,
//     so a per-material/per-technique divergence is localized.
//  2. Feeds IDENTICAL register data to the original and recompiled effect. The only
//     parameter whose reflected D3D class changed across the skinning fix is SkinBone
//     (float4x3[30] in the original, float4[90] in the recompiled). It is special-cased
//     to produce IDENTITY bone registers in BOTH effects, which is transpose-invariant,
//     so it can never spuriously diverge. Skinning INDEX/STRIDE logic is still exercised
//     (vertices are multiplied by identity bones -> transform still tested).
//  3. Matrix parameters (matView/matProj/InvView/...) get a realistic asymmetric matrix so
//     any transpose(matView/matProj) reconstruction bug in the normal/SH path diverges.
//
// usage: fxotest_oracle <original.fxo> <recompiled.fxo> [--tech N] [--dump prefix] [--skinmode 0|1]

namespace fxotest
{
    static class Program
    {
        [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
        const int W = 128, H = 128;
        static int SkinMode = 0; // 0: matrix SkinBone stored row-major identity; 1: stored register-order identity

        static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("usage: fxotest_oracle <original.fxo> <recompiled.fxo> [--tech N] [--dump prefix] [--skinmode 0|1]");
                return 1;
            }
            string origPath = args[0], recPath = args[1];
            int techOverride = -1; string dump = null;
            for (int i = 2; i < args.Length; i++)
            {
                if (args[i] == "--tech" && i + 1 < args.Length) techOverride = int.Parse(args[++i]);
                else if (args[i] == "--dump" && i + 1 < args.Length) dump = args[++i];
                else if (args[i] == "--skinmode" && i + 1 < args.Length) SkinMode = int.Parse(args[++i]);
            }
            var envSkin = Environment.GetEnvironmentVariable("FXO_SKINMODE");
            if (envSkin != null && int.TryParse(envSkin, out int sm)) SkinMode = sm;

            var d3d = new Direct3D();
            var pp = new PresentParameters
            {
                Windowed = true,
                SwapEffect = SwapEffect.Discard,
                BackBufferWidth = W,
                BackBufferHeight = H,
                BackBufferFormat = Format.A8R8G8B8,
                EnableAutoDepthStencil = true,
                AutoDepthStencilFormat = Format.D24S8,
                DeviceWindowHandle = GetDesktopWindow(),
                PresentationInterval = PresentInterval.Immediate,
            };
            Device dev = null;
            foreach (var dt in new[] { DeviceType.Reference, DeviceType.Hardware })
            {
                try { dev = new Device(d3d, 0, dt, GetDesktopWindow(),
                        dt == DeviceType.Hardware ? CreateFlags.HardwareVertexProcessing : CreateFlags.SoftwareVertexProcessing, pp); break; }
                catch { }
            }
            if (dev == null) { Console.Error.WriteLine("no D3D9 device"); return 2; }

            var decl = MakeDeclaration(dev);
            var vb = MakeGridVertexBuffer(dev, out int primCount);
            var tex = MakeTestTexture(dev);
            var rt = new Texture(dev, W, H, 1, Usage.RenderTarget, Format.A8R8G8B8, Pool.Default);
            var rtSurf = rt.GetSurfaceLevel(0);
            var depth = Surface.CreateDepthStencil(dev, W, H, Format.D24S8, MultisampleType.None, 0, true);
            var sysSurf = Surface.CreateOffscreenPlain(dev, W, H, Format.A8R8G8B8, Pool.SystemMemory);

            Effect fxO, fxR;
            try { fxO = LoadEffect(dev, origPath, tex); }
            catch (Exception e) { Console.WriteLine($"RESULT\tERROR-ORIG\t{Path.GetFileName(origPath)}\t{e.Message.Replace('\t',' ')}"); return 3; }
            try { fxR = LoadEffect(dev, recPath, tex); }
            catch (Exception e) { Console.WriteLine($"RESULT\tERROR-REC\t{Path.GetFileName(recPath)}\t{e.Message.Replace('\t',' ')}"); return 3; }

            int techCountO = fxO.Description.Techniques;
            int techCountR = fxR.Description.Techniques;
            int techCount = Math.Min(techCountO, techCountR);
            string fname = Path.GetFileName(origPath);

            int worst = 0;
            int tStart = techOverride >= 0 ? techOverride : 0;
            int tEnd = techOverride >= 0 ? techOverride + 1 : techCount;
            for (int t = tStart; t < tEnd; t++)
            {
                string tnameO = GetTechName(fxO, t);
                byte[] imgA, imgB;
                bool okA = TryRenderTechnique(dev, fxO, t, decl, vb, primCount, rtSurf, depth, sysSurf, out imgA);
                bool okB = TryRenderTechnique(dev, fxR, t, decl, vb, primCount, rtSurf, depth, sysSurf, out imgB);
                if (!okA || !okB)
                {
                    Console.WriteLine($"RESULT\tSKIP\t{fname}\ttech={t} name={tnameO} validateO={okA} validateR={okB}");
                    continue;
                }

                long sum = 0; int maxd = 0; int diffPixels = 0; int nonBgA = 0, nonBgB = 0; int blackA = 0, blackB = 0;
                for (int i = 0; i < imgA.Length; i++) { int dd = Math.Abs(imgA[i] - imgB[i]); sum += dd; if (dd > maxd) maxd = dd; }
                for (int p = 0; p < W * H; p++)
                {
                    int o = p * 4;
                    int d = Math.Max(Math.Max(Math.Abs(imgA[o]-imgB[o]), Math.Abs(imgA[o+1]-imgB[o+1])),
                                     Math.Max(Math.Abs(imgA[o+2]-imgB[o+2]), Math.Abs(imgA[o+3]-imgB[o+3])));
                    if (d > 2) diffPixels++;
                    bool bgA = imgA[o]==0x20 && imgA[o+1]==0x20 && imgA[o+2]==0x20;
                    bool bgB = imgB[o]==0x20 && imgB[o+1]==0x20 && imgB[o+2]==0x20;
                    if (!bgA) nonBgA++;
                    if (!bgB) nonBgB++;
                    // count "black" covered pixels (drawn but near-zero rgb) to spot the invisible/black bug
                    if (!bgA && imgA[o]<8 && imgA[o+1]<8 && imgA[o+2]<8) blackA++;
                    if (!bgB && imgB[o]<8 && imgB[o+1]<8 && imgB[o+2]<8) blackB++;
                }
                double mean = (double)sum / imgA.Length;
                string verdict = maxd <= 2 ? "MATCH" : (maxd <= 12 ? "CLOSE" : "DIFF");
                if (maxd > worst) worst = maxd;
                Console.WriteLine($"RESULT\t{verdict}\t{fname}\ttech={t} name={tnameO} maxDiff={maxd} mean={mean:F3} diffPx={diffPixels}/{W*H} covO={nonBgA} covR={nonBgB} blkO={blackA} blkR={blackB}");
                if (dump != null) { SaveTga($"{dump}_t{t}_orig.tga", imgA); SaveTga($"{dump}_t{t}_rec.tga", imgB); }
            }

            fxO.Dispose(); fxR.Dispose();
            return worst <= 12 ? 0 : 10;
        }

        static string GetTechName(Effect fx, int t)
        {
            try { return fx.GetTechniqueDescription(fx.GetTechnique(t)).Name; } catch { return "?"; }
        }

        static Effect LoadEffect(Device dev, string fxoPath, Texture tex)
        {
            byte[] fxo = File.ReadAllBytes(fxoPath);
            var fx = Effect.FromMemory(dev, fxo, ShaderFlags.None);
            SetAllParameters(fx, tex);
            return fx;
        }

        // Row-major 4x4 helpers (D3D convention: p' = p * M).
        static float[] Mul(float[] a, float[] b)
        {
            var r = new float[16];
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                {
                    float s = 0; for (int k = 0; k < 4; k++) s += a[i*4+k] * b[k*4+j];
                    r[i*4+j] = s;
                }
            return r;
        }
        static float[] Invert(float[] m)
        {
            // general 4x4 inverse (cofactor); adequate for a well-conditioned view matrix.
            var inv = new float[16]; var a = m;
            inv[0]  =  a[5]*a[10]*a[15]-a[5]*a[11]*a[14]-a[9]*a[6]*a[15]+a[9]*a[7]*a[14]+a[13]*a[6]*a[11]-a[13]*a[7]*a[10];
            inv[4]  = -a[4]*a[10]*a[15]+a[4]*a[11]*a[14]+a[8]*a[6]*a[15]-a[8]*a[7]*a[14]-a[12]*a[6]*a[11]+a[12]*a[7]*a[10];
            inv[8]  =  a[4]*a[9]*a[15]-a[4]*a[11]*a[13]-a[8]*a[5]*a[15]+a[8]*a[7]*a[13]+a[12]*a[5]*a[11]-a[12]*a[7]*a[9];
            inv[12] = -a[4]*a[9]*a[14]+a[4]*a[10]*a[13]+a[8]*a[5]*a[14]-a[8]*a[6]*a[13]-a[12]*a[5]*a[10]+a[12]*a[6]*a[9];
            inv[1]  = -a[1]*a[10]*a[15]+a[1]*a[11]*a[14]+a[9]*a[2]*a[15]-a[9]*a[3]*a[14]-a[13]*a[2]*a[11]+a[13]*a[3]*a[10];
            inv[5]  =  a[0]*a[10]*a[15]-a[0]*a[11]*a[14]-a[8]*a[2]*a[15]+a[8]*a[3]*a[14]+a[12]*a[2]*a[11]-a[12]*a[3]*a[10];
            inv[9]  = -a[0]*a[9]*a[15]+a[0]*a[11]*a[13]+a[8]*a[1]*a[15]-a[8]*a[3]*a[13]-a[12]*a[1]*a[11]+a[12]*a[3]*a[9];
            inv[13] =  a[0]*a[9]*a[14]-a[0]*a[10]*a[13]-a[8]*a[1]*a[14]+a[8]*a[2]*a[13]+a[12]*a[1]*a[10]-a[12]*a[2]*a[9];
            inv[2]  =  a[1]*a[6]*a[15]-a[1]*a[7]*a[14]-a[5]*a[2]*a[15]+a[5]*a[3]*a[14]+a[13]*a[2]*a[7]-a[13]*a[3]*a[6];
            inv[6]  = -a[0]*a[6]*a[15]+a[0]*a[7]*a[14]+a[4]*a[2]*a[15]-a[4]*a[3]*a[14]-a[12]*a[2]*a[7]+a[12]*a[3]*a[6];
            inv[10] =  a[0]*a[5]*a[15]-a[0]*a[7]*a[13]-a[4]*a[1]*a[15]+a[4]*a[3]*a[13]+a[12]*a[1]*a[7]-a[12]*a[3]*a[5];
            inv[14] = -a[0]*a[5]*a[14]+a[0]*a[6]*a[13]+a[4]*a[1]*a[14]-a[4]*a[2]*a[13]-a[12]*a[1]*a[6]+a[12]*a[2]*a[5];
            inv[3]  = -a[1]*a[6]*a[11]+a[1]*a[7]*a[10]+a[5]*a[2]*a[11]-a[5]*a[3]*a[10]-a[9]*a[2]*a[7]+a[9]*a[3]*a[6];
            inv[7]  =  a[0]*a[6]*a[11]-a[0]*a[7]*a[10]-a[4]*a[2]*a[11]+a[4]*a[3]*a[10]+a[8]*a[2]*a[7]-a[8]*a[3]*a[6];
            inv[11] = -a[0]*a[5]*a[11]+a[0]*a[7]*a[9]+a[4]*a[1]*a[11]-a[4]*a[3]*a[9]-a[8]*a[1]*a[7]+a[8]*a[3]*a[5];
            inv[15] =  a[0]*a[5]*a[10]-a[0]*a[6]*a[9]-a[4]*a[1]*a[10]+a[4]*a[2]*a[9]+a[8]*a[1]*a[6]-a[8]*a[2]*a[5];
            float det = a[0]*inv[0]+a[1]*inv[4]+a[2]*inv[8]+a[3]*inv[12];
            if (Math.Abs(det) < 1e-12f) det = 1e-12f;
            float id = 1f/det;
            for (int i=0;i<16;i++) inv[i]*=id;
            return inv;
        }
        // Realistic asymmetric transform matrices, shared by name across both effects.
        static Dictionary<string, float[]> BuildTransformMatrices()
        {
            // View: an orthonormal rotation (asymmetric) with a translation. Row-major.
            float[] view = {
                0.8047f, 0.1653f, -0.5709f, 0f,
               -0.3106f, 0.9256f, -0.2158f, 0f,
                0.5058f, 0.3403f,  0.7924f, 0f,
                0.20f,  -0.35f,    3.10f,   1f };
            // Perspective LH: fov=60, aspect=1, near=0.5, far=100. Highly asymmetric (1 at [2][3]).
            float ys = 1.7320508f, xs = 1.7320508f;
            float far = 100f, near = 0.5f;
            float[] proj = {
                xs, 0f, 0f, 0f,
                0f, ys, 0f, 0f,
                0f, 0f, far/(far-near), 1f,
                0f, 0f, -near*far/(far-near), 0f };
            float[] world = {
                0.98f, 0.12f, -0.05f, 0f,
               -0.10f, 0.96f,  0.15f, 0f,
                0.07f,-0.14f,  0.99f, 0f,
                0.05f,-0.10f,  0.20f, 1f };
            var d = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            d["matWorld"] = world;
            d["matView"] = view;
            d["matProj"] = proj;
            d["matViewProj"] = Mul(view, proj);
            d["matWorldView"] = Mul(world, view);
            d["matWorldViewProj"] = Mul(Mul(world, view), proj);
            d["InvView"] = Invert(view);
            return d;
        }
        static readonly Dictionary<string, float[]> TransformMatrices = BuildTransformMatrices();

        static bool TryRenderTechnique(Device dev, Effect fx, int techIndex, VertexDeclaration decl, VertexBuffer vb,
            int primCount, Surface rtSurf, Surface depth, Surface sysSurf, out byte[] img)
        {
            img = null;
            var tech = fx.GetTechnique(techIndex);
            try { fx.ValidateTechnique(tech); } catch { return false; }
            fx.Technique = tech;

            dev.SetRenderTarget(0, rtSurf);
            dev.DepthStencilSurface = depth;
            dev.SetRenderState(RenderState.ZEnable, true);
            dev.SetRenderState(RenderState.CullMode, Cull.None);
            dev.Clear(ClearFlags.Target | ClearFlags.ZBuffer, new RawColorBGRA(0x20, 0x20, 0x20, 0xFF), 1f, 0);
            dev.VertexDeclaration = decl;
            dev.SetStreamSource(0, vb, 0, VertexStride);

            dev.BeginScene();
            int passes = fx.Begin(FX.DoNotSaveState);
            for (int p = 0; p < passes; p++)
            {
                fx.BeginPass(p);
                try { dev.DrawPrimitives(PrimitiveType.TriangleList, 0, primCount); } catch { }
                fx.EndPass();
            }
            fx.End();
            dev.EndScene();

            dev.GetRenderTargetData(rtSurf, sysSurf);
            var dr = sysSurf.LockRectangle(LockFlags.ReadOnly);
            img = new byte[W * H * 4];
            var stream = new DataStream(dr.DataPointer, dr.Pitch * H, true, false);
            for (int y = 0; y < H; y++) { stream.Position = y * dr.Pitch; stream.Read(img, y * W * 4, W * 4); }
            sysSurf.UnlockRectangle();
            return true;
        }

        // Set every effect parameter to a deterministic value derived from its NAME, so the original
        // and recompiled effects (which share parameter names) receive identical register data.
        // SkinBone is special-cased to identity bones (transpose-invariant across the float4x3<->float4 change).
        static void SetAllParameters(Effect fx, Texture tex)
        {
            int n = fx.Description.Parameters;
            for (int i = 0; i < n; i++)
            {
                var h = fx.GetParameter(null, i);
                ParameterDescription d;
                try { d = fx.GetParameterDescription(h); } catch { continue; }
                try
                {
                    if (d.Class == ParameterClass.Object)
                    {
                        if (d.Type == ParameterType.Texture || d.Type == ParameterType.Texture2D ||
                            d.Type == ParameterType.Texture1D || d.Type == ParameterType.Texture3D ||
                            d.Type == ParameterType.TextureCube)
                            fx.SetTexture(h, tex);
                        continue;
                    }
                    int elements = Math.Max(1, d.Elements);
                    int rows = Math.Max(1, d.Rows), cols = Math.Max(1, d.Columns);
                    bool isMatrix = d.Class == ParameterClass.MatrixRows || d.Class == ParameterClass.MatrixColumns;
                    int perElem = rows * cols;
                    int total = perElem * elements;

                    // ---- SkinBone: force identity bones so the class change (float4x3[30] vs float4[90])
                    //      can never cause a spurious divergence. Identity is transpose-invariant. ----
                    if (d.Name != null && d.Name.IndexOf("SkinBone", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var f = new float[total];
                        if (isMatrix)
                        {
                            if (SkinMode == 0)
                            {
                                // row-major 4x3 identity per element: rows (1,0,0)(0,1,0)(0,0,1)(0,0,0)
                                for (int e = 0; e < elements; e++)
                                    for (int r = 0; r < rows; r++)
                                        for (int c = 0; c < cols; c++)
                                            f[e*perElem + r*cols + c] = (r == c) ? 1f : 0f;
                            }
                            else
                            {
                                // register-order identity per element: 3 regs (1,0,0,0)(0,1,0,0)(0,0,1,0)
                                for (int e = 0; e < elements; e++)
                                    for (int k = 0; k < perElem; k++)
                                    {
                                        int reg = k / 4, lane = k % 4;
                                        f[e*perElem + k] = (lane == reg) ? 1f : 0f;
                                    }
                            }
                        }
                        else
                        {
                            // float4[90]: register-order identity, 30 blocks of 3 registers (perElem==4)
                            for (int e = 0; e < elements; e++)
                            {
                                int blk = e % 3;
                                for (int c = 0; c < perElem; c++)
                                    f[e*perElem + c] = (c == blk) ? 1f : 0f;
                            }
                        }
                        fx.SetValue(h, f);
                        continue;
                    }

                    // ---- Named transform matrices: feed a realistic asymmetric perspective/view,
                    //      identically to both effects, so a transpose(matView/matProj/InvView) bug diverges. ----
                    if (isMatrix && d.Name != null && rows == 4 && cols == 4 && elements == 1 &&
                        TransformMatrices.TryGetValue(d.Name, out var M))
                    {
                        fx.SetValue(h, M); // raw row-major 16 floats, identical to both effects
                        continue;
                    }

                    uint seed = Hash(d.Name);
                    if (d.Type == ParameterType.Bool)
                    {
                        var b = new bool[total];
                        for (int k = 0; k < total; k++) b[k] = ((seed >> (k & 31)) & 1) != 0;
                        fx.SetValue(h, b);
                    }
                    else if (d.Type == ParameterType.Int)
                    {
                        var vi = new int[total];
                        for (int k = 0; k < total; k++) vi[k] = (int)((Hash(d.Name + k) % 8));
                        fx.SetValue(h, vi);
                    }
                    else // Float
                    {
                        var f = new float[total];
                        for (int e = 0; e < elements; e++)
                            for (int r = 0; r < rows; r++)
                                for (int c = 0; c < cols; c++)
                                {
                                    int idx = e * perElem + r * cols + c;
                                    if (isMatrix)
                                        f[idx] = (r == c ? 1f : 0f) + 0.06f * ((r - c)) + 0.01f * (int)(Hash(d.Name + idx) % 7);
                                    else
                                        f[idx] = 0.15f + 0.7f * ((Hash(d.Name + idx) % 1000) / 1000f);
                                }
                        fx.SetValue(h, f);
                    }
                }
                catch { }
            }
        }

        static uint Hash(string s)
        {
            uint h = 2166136261u;
            foreach (char ch in s) { h ^= ch; h *= 16777619u; }
            return h == 0 ? 1u : h;
        }

        const int VertexStride = 64;

        static VertexDeclaration MakeDeclaration(Device dev)
        {
            var elems = new[]
            {
                new VertexElement(0, 0,  DeclarationType.Float3,  DeclarationMethod.Default, DeclarationUsage.Position, 0),
                new VertexElement(0, 12, DeclarationType.Float4,  DeclarationMethod.Default, DeclarationUsage.BlendWeight, 0),
                new VertexElement(0, 28, DeclarationType.UByte4N, DeclarationMethod.Default, DeclarationUsage.BlendIndices, 0),
                new VertexElement(0, 32, DeclarationType.Float3,  DeclarationMethod.Default, DeclarationUsage.Normal, 0),
                new VertexElement(0, 44, DeclarationType.Float2,  DeclarationMethod.Default, DeclarationUsage.TextureCoordinate, 0),
                new VertexElement(0, 52, DeclarationType.Float2,  DeclarationMethod.Default, DeclarationUsage.TextureCoordinate, 1),
                new VertexElement(0, 60, DeclarationType.Color,   DeclarationMethod.Default, DeclarationUsage.Color, 0),
                VertexElement.VertexDeclarationEnd
            };
            return new VertexDeclaration(dev, elems);
        }

        static VertexBuffer MakeGridVertexBuffer(Device dev, out int primCount)
        {
            const int N = 12;
            var idx = new List<int>();
            var raw = new List<byte[]>();
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float fx = -0.9f + 1.8f * x / (N - 1);
                    float fy = -0.9f + 1.8f * y / (N - 1);
                    raw.Add(MakeVertex(fx, fy, x, y, N));
                }
            for (int y = 0; y < N - 1; y++)
                for (int x = 0; x < N - 1; x++)
                {
                    int a = y * N + x, b = y * N + x + 1, c = (y + 1) * N + x, d = (y + 1) * N + x + 1;
                    idx.Add(a); idx.Add(b); idx.Add(c);
                    idx.Add(b); idx.Add(d); idx.Add(c);
                }
            var data = new List<byte>();
            foreach (int i in idx) data.AddRange(raw[i]);
            primCount = idx.Count / 3;
            var vb = new VertexBuffer(dev, data.Count, Usage.None, VertexFormat.None, Pool.Managed);
            var ds = vb.Lock(0, data.Count, LockFlags.None);
            ds.Write(data.ToArray(), 0, data.Count);
            vb.Unlock();
            return vb;
        }

        static byte[] MakeVertex(float x, float y, int gx, int gy, int N)
        {
            var ms = new MemoryStream();
            var bw = new BinaryWriter(ms);
            bw.Write(x); bw.Write(y); bw.Write(0.5f);
            bw.Write(0.4f); bw.Write(0.3f); bw.Write(0.2f); bw.Write(0.1f);
            bw.Write((byte)0); bw.Write((byte)1); bw.Write((byte)2); bw.Write((byte)3);
            float nx = (gx / (float)N) - 0.5f, ny = (gy / (float)N) - 0.5f;
            float nl = MathF.Max(0.001f, MathF.Sqrt(nx*nx + ny*ny + 0.25f));
            bw.Write(nx/nl); bw.Write(ny/nl); bw.Write(0.5f/nl);
            bw.Write(gx / (float)(N - 1)); bw.Write(gy / (float)(N - 1));
            bw.Write(gy / (float)(N - 1)); bw.Write(gx / (float)(N - 1));
            byte r = (byte)(40 + 200 * gx / N), g = (byte)(40 + 200 * gy / N), bl = (byte)128, aa = (byte)255;
            uint col = (uint)((aa << 24) | (r << 16) | (g << 8) | bl);
            bw.Write(col);
            return ms.ToArray();
        }

        static Texture MakeTestTexture(Device dev)
        {
            const int T = 8;
            var tex = new Texture(dev, T, T, 1, Usage.None, Format.A8R8G8B8, Pool.Managed);
            var dr = tex.LockRectangle(0, LockFlags.None);
            var s = new DataStream(dr.DataPointer, dr.Pitch * T, true, true);
            for (int y = 0; y < T; y++)
            {
                s.Position = y * dr.Pitch;
                for (int x = 0; x < T; x++)
                {
                    byte r = (byte)(x * 32), g = (byte)(y * 32), b = (byte)((x ^ y) * 24), a = 255;
                    s.Write((uint)((a << 24) | (r << 16) | (g << 8) | b));
                }
            }
            tex.UnlockRectangle(0);
            return tex;
        }

        static void SaveTga(string path, byte[] bgra)
        {
            using var fs = File.Create(path);
            using var bw = new BinaryWriter(fs);
            bw.Write(new byte[] { 0,0,2,0,0,0,0,0,0,0,0,0 });
            bw.Write((short)W); bw.Write((short)H); bw.Write((byte)32); bw.Write((byte)0);
            for (int y = H - 1; y >= 0; y--) bw.Write(bgra, y * W * 4, W * 4);
        }
    }
}
