using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D9;
using SharpDX.Mathematics.Interop;

// fxotest: renders an original .fxo and a recompiled .fxo through the SAME deterministic D3D9
// REF device with IDENTICAL inputs (vertex data, textures, and name-hashed effect parameters),
// then diffs the rendered pixels. Because the reference rasterizer is a bit-exact CPU renderer
// and both effects receive the same inputs, any behavioural divergence in the reconstructed HLSL
// shows up as a pixel difference. fxc's own re-optimisation (op reordering) can introduce tiny
// (<=~2/255) differences even for a correct round-trip, so a small threshold is expected.
//
// usage: fxotest <original.fxo> <recompiled.fxo> [--tech N] [--dump <prefix>]

namespace fxotest
{
    static class Program
    {
        [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
        const int W = 128, H = 128;

        static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("usage: fxotest <original.fxo> <recompiled.fxo> [--tech N] [--dump prefix]");
                return 1;
            }
            string origPath = args[0], recPath = args[1];
            int techOverride = -1; string dump = null;
            for (int i = 2; i < args.Length; i++)
            {
                if (args[i] == "--tech" && i + 1 < args.Length) techOverride = int.Parse(args[++i]);
                else if (args[i] == "--dump" && i + 1 < args.Length) dump = args[++i];
            }

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

            // --params <fxo>: dump the D3DX-reflected effect parameter table (what the engine sees
            // to bind constants by semantic). Used to compare an original vs a recompiled effect.
            if (args[0] == "--params")
            {
                var fxp = Effect.FromMemory(dev, File.ReadAllBytes(args[1]), ShaderFlags.None);
                for (int i = 0; i < fxp.Description.Parameters; i++)
                {
                    var hh = fxp.GetParameter(null, i);
                    var dd = fxp.GetParameterDescription(hh);
                    Console.WriteLine($"P\t{dd.Name}\tClass={dd.Class}\tType={dd.Type}\tRows={dd.Rows}\tCols={dd.Columns}\tElems={dd.Elements}\tSem={dd.Semantic}\tBytes={dd.Bytes}");
                }
                // Technique + annotation dump (what Gamebryo reads to set up skinning: BonesPerPartition,
                // BlendIndicesAsD3DColor, shadername, UsesNiRenderState, Implementation).
                for (int t = 0; t < fxp.Description.Techniques; t++)
                {
                    var th = fxp.GetTechnique(t);
                    var td = fxp.GetTechniqueDescription(th);
                    Console.WriteLine($"T\t{td.Name}\tpasses={td.Passes}\tannos={td.Annotations}");
                    for (int a = 0; a < td.Annotations; a++)
                    {
                        var ah = fxp.GetAnnotation(th, a);
                        var ad = fxp.GetParameterDescription(ah);
                        string val;
                        try {
                            if (ad.Type == ParameterType.String) val = fxp.GetString(ah);
                            else if (ad.Type == ParameterType.Bool) val = fxp.GetValue<int>(ah).ToString();
                            else if (ad.Type == ParameterType.Int) val = fxp.GetValue<int>(ah).ToString();
                            else if (ad.Type == ParameterType.Float) val = fxp.GetValue<float>(ah).ToString();
                            else val = "?";
                        } catch { val = "<err>"; }
                        Console.WriteLine($"  A\t{td.Name}\t{ad.Name}\t{ad.Type}\t{val}");
                    }
                }
                return 0;
            }

            // Shared, deterministic scene resources.
            var decl = MakeDeclaration(dev);
            var vb = MakeGridVertexBuffer(dev, out int primCount);
            var tex = MakeTestTexture(dev);
            var rt = new Texture(dev, W, H, 1, Usage.RenderTarget, Format.A8R8G8B8, Pool.Default);
            var rtSurf = rt.GetSurfaceLevel(0);
            var depth = Surface.CreateDepthStencil(dev, W, H, Format.D24S8, MultisampleType.None, 0, true);
            var sysSurf = Surface.CreateOffscreenPlain(dev, W, H, Format.A8R8G8B8, Pool.SystemMemory);

            byte[] imgA, imgB; string err = null;
            int techA, techB;
            try { imgA = RenderEffect(dev, origPath, decl, vb, primCount, tex, rtSurf, depth, sysSurf, techOverride, out techA); }
            catch (Exception e) { Console.WriteLine($"RESULT\tERROR-ORIG\t{Path.GetFileName(origPath)}\t{e.Message.Replace('\t',' ')}"); return 3; }
            try { imgB = RenderEffect(dev, recPath, decl, vb, primCount, tex, rtSurf, depth, sysSurf, techOverride, out techB); }
            catch (Exception e) { Console.WriteLine($"RESULT\tERROR-REC\t{Path.GetFileName(recPath)}\t{e.Message.Replace('\t',' ')}"); return 3; }

            // Diff.
            long sum = 0; int maxd = 0; int diffPixels = 0; int nonBg = 0;
            for (int i = 0; i < imgA.Length; i++)
            {
                int d = Math.Abs(imgA[i] - imgB[i]);
                sum += d; if (d > maxd) maxd = d;
            }
            for (int p = 0; p < W * H; p++)
            {
                int o = p * 4;
                int d = Math.Max(Math.Max(Math.Abs(imgA[o]-imgB[o]), Math.Abs(imgA[o+1]-imgB[o+1])),
                                 Math.Max(Math.Abs(imgA[o+2]-imgB[o+2]), Math.Abs(imgA[o+3]-imgB[o+3])));
                if (d > 2) diffPixels++;
                if (imgA[o] != 0x20 || imgA[o+1] != 0x20 || imgA[o+2] != 0x20) nonBg++; // clear color 0x202020
            }
            double mean = (double)sum / imgA.Length;
            string verdict = maxd <= 2 ? "MATCH" : (maxd <= 12 ? "CLOSE" : "DIFF");
            Console.WriteLine($"RESULT\t{verdict}\t{Path.GetFileName(origPath)}\ttechO={techA} techR={techB} maxDiff={maxd} mean={mean:F3} diffPx={diffPixels}/{W*H} coverage={nonBg}");

            if (dump != null) { SaveTga(dump + "_orig.tga", imgA); SaveTga(dump + "_rec.tga", imgB); }
            return maxd <= 12 ? 0 : 10;
        }

        static byte[] RenderEffect(Device dev, string fxoPath, VertexDeclaration decl, VertexBuffer vb, int primCount,
            Texture tex, Surface rtSurf, Surface depth, Surface sysSurf, int techOverride, out int techUsed)
        {
            byte[] fxo = File.ReadAllBytes(fxoPath);
            var fx = Effect.FromMemory(dev, fxo, ShaderFlags.None);
            SetAllParameters(fx, tex);

            dev.SetRenderTarget(0, rtSurf);
            dev.DepthStencilSurface = depth;
            dev.SetRenderState(RenderState.ZEnable, true);
            dev.SetRenderState(RenderState.CullMode, Cull.None);
            dev.Clear(ClearFlags.Target | ClearFlags.ZBuffer, new RawColorBGRA(0x20, 0x20, 0x20, 0xFF), 1f, 0);
            dev.VertexDeclaration = decl;
            dev.SetStreamSource(0, vb, 0, VertexStride);

            // Pick the first technique that validates; fall back to technique 0.
            int techCount = fx.Description.Techniques;
            int chosen = techOverride >= 0 ? techOverride : 0;
            if (techOverride < 0)
            {
                for (int t = 0; t < techCount; t++)
                {
                    var h = fx.GetTechnique(t);
                    bool ok = true; try { fx.ValidateTechnique(h); } catch { ok = false; }
                    if (ok) { chosen = t; break; }
                }
            }
            techUsed = chosen;
            var tech = fx.GetTechnique(chosen);
            fx.Technique = tech;

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
            byte[] img = new byte[W * H * 4];
            var stream = new DataStream(dr.DataPointer, dr.Pitch * H, true, false);
            for (int y = 0; y < H; y++)
            {
                stream.Position = y * dr.Pitch;
                stream.Read(img, y * W * 4, W * 4);
            }
            sysSurf.UnlockRectangle();
            fx.Dispose();
            return img;
        }

        // Set every effect parameter to a deterministic value derived from its NAME, so the original
        // and recompiled effects (which share parameter names) receive identical inputs.
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
                    else // Float (and default)
                    {
                        var f = new float[total];
                        for (int e = 0; e < elements; e++)
                            for (int r = 0; r < rows; r++)
                                for (int c = 0; c < cols; c++)
                                {
                                    int idx = e * perElem + r * cols + c;
                                    if (isMatrix)
                                        // near-identity but asymmetric, so a transpose bug diverges
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

        // ---- scene geometry ----
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
            const int N = 12; // NxN grid of vertices -> (N-1)^2*2 triangles
            var verts = new List<byte>();
            var idx = new List<int>();
            float[,] px = new float[N, N]; // remember vertex index layout
            int vi = 0;
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
            // Expand indexed into a non-indexed triangle list (simpler stream).
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
            bw.Write(x); bw.Write(y); bw.Write(0.5f);                 // POSITION
            // BLENDWEIGHT (normalized-ish, sums ~1)
            bw.Write(0.4f); bw.Write(0.3f); bw.Write(0.2f); bw.Write(0.1f);
            // BLENDINDICES as UBYTE4N -> bytes; use small bone indices 0..3
            bw.Write((byte)0); bw.Write((byte)1); bw.Write((byte)2); bw.Write((byte)3);
            // NORMAL
            float nx = (gx / (float)N) - 0.5f, ny = (gy / (float)N) - 0.5f;
            float nl = MathF.Max(0.001f, MathF.Sqrt(nx*nx + ny*ny + 0.25f));
            bw.Write(nx/nl); bw.Write(ny/nl); bw.Write(0.5f/nl);
            // TEXCOORD0, TEXCOORD1
            bw.Write(gx / (float)(N - 1)); bw.Write(gy / (float)(N - 1));
            bw.Write(gy / (float)(N - 1)); bw.Write(gx / (float)(N - 1));
            // COLOR0 (D3DCOLOR ARGB)
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

        // Minimal 32-bit BGRA TGA for eyeballing dumps.
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
