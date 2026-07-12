using System;
using System.IO;

namespace fxo2hlsl
{
    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.Error.WriteLine("usage: fxo2hlsl <file.fxo|.o> [out.hlsl] [-a]   (-a = raw asm instead of HLSL)");
                return 1;
            }
            bool asm = false;
            string src = null, dst = null;
            foreach (var a in args)
            {
                if (a == "-a") asm = true;
                else if (src == null) src = a;
                else dst = a;
            }
            byte[] data = File.ReadAllBytes(src);
            string outText;
            try
            {
                outText = asm
                    ? DXDecompiler.DX9Shader.AsmWriter.Disassemble(data)
                    : DXDecompiler.DX9Shader.HlslWriter.Decompile(data);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("decompile failed: " + ex.Message);
                return 2;
            }
            if (dst != null) { File.WriteAllText(dst, outText); Console.Error.WriteLine($"wrote {dst} ({outText.Length} chars)"); }
            else Console.Write(outText);
            return 0;
        }
    }
}
