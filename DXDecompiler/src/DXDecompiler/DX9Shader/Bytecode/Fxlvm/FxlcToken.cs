using DXDecompiler.DX9Shader.Bytecode.Ctab;
using DXDecompiler.Util;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace DXDecompiler.DX9Shader.Bytecode.Fxlvm
{
	public class FxlcToken
	{
		public FxlcOpcode Opcode { get; private set; }
		public bool IsScalarInstruction { get; private set; }
		public List<FxlcOperand> Operands { get; private set; }

		public FxlcToken()
		{
			Operands = new List<FxlcOperand>();
		}

		public static FxlcToken Parse(BytecodeReader reader)
		{
			var result = new FxlcToken();
			var token = reader.ReadUInt32();
			var tokenComponentCount = token.DecodeValue(0, 2);
			result.Opcode = (FxlcOpcode)token.DecodeValue(20, 30);
			result.IsScalarInstruction = token.DecodeValue<bool>(31, 31);

			Debug.Assert(Enum.IsDefined(typeof(FxlcOpcode), result.Opcode),
				$"Unknown FxlcOpcode {result.Opcode}");

			Debug.Assert(token.DecodeValue(3, 19) == 0,
				$"Unexpected data in FxlcToken bits 3-19 {token.DecodeValue(3, 19)}");

			var operandCount = reader.ReadUInt32();
			for(int i = 0; i < operandCount; i++)
			{
				var isScalarOp = i == 0 && result.IsScalarInstruction;
				result.Operands.Add(FxlcOperand.Parse(reader, tokenComponentCount, isScalarOp));
			}
			// destination operand
			result.Operands.Insert(0, FxlcOperand.Parse(reader, tokenComponentCount, false));
			return result;
		}

		/// <summary>
		/// ToString method for debugging purposes. We are not not able to properly represent
		/// the instruction's assembly without access to the ctab and cli chunks.
		/// </summary>
		/// <returns></returns>
		public override string ToString()
		{
			var operands = string.Join(", ", Operands.Select(o => o.ToString()));
			return string.Format("{0} {1}",
					Opcode.ToString().ToLowerInvariant(),
					operands);
		}

		public string ToString(ConstantTable ctab, CliToken cli)
		{
			var operands = string.Join(", ", Operands.Select(o => o.FormatOperand(ctab, cli)));
			return string.Format("{0} {1}",
					Opcode.ToString().ToLowerInvariant(),
					operands);
		}
		/// <summary>
		/// Reconstruct this FXLVM instruction as a recompilable HLSL statement so fxc can
		/// re-fold the uniform-only math back into a preshader. operands[0] is the destination.
		/// </summary>
		public string ToHlsl(ConstantTable ctab, CliToken cli)
		{
			var dest = Operands[0].ToHlslOperand(ctab, cli);
			var s = new List<string>();
			for(int i = 1; i < Operands.Count; i++)
			{
				s.Add(Operands[i].ToHlslOperand(ctab, cli));
			}
			string Fn(string fn) => string.Format("{0}({1})", fn, string.Join(", ", s));
			string Bin(string op) => string.Format("({0} {1} {2})", s[0], op, s[1]);
			string rhs;
			switch(Opcode)
			{
				case FxlcOpcode.Mov: rhs = s[0]; break;
				case FxlcOpcode.Neg: rhs = string.Format("(-{0})", s[0]); break;
				case FxlcOpcode.Rcp: rhs = string.Format("(1.0 / {0})", s[0]); break;
				case FxlcOpcode.Frc: rhs = Fn("frac"); break;
				case FxlcOpcode.Exp: rhs = Fn("exp2"); break;
				case FxlcOpcode.Log: rhs = Fn("log2"); break;
				case FxlcOpcode.Rsq: rhs = Fn("rsqrt"); break;
				case FxlcOpcode.Sqrt: rhs = Fn("sqrt"); break;
				case FxlcOpcode.Sin: rhs = Fn("sin"); break;
				case FxlcOpcode.Cos: rhs = Fn("cos"); break;
				case FxlcOpcode.Asin: rhs = Fn("asin"); break;
				case FxlcOpcode.Acos: rhs = Fn("acos"); break;
				case FxlcOpcode.Atan: rhs = Fn("atan"); break;
				case FxlcOpcode.Atan2: rhs = Fn("atan2"); break;
				case FxlcOpcode.Floor: rhs = Fn("floor"); break;
				case FxlcOpcode.Ceil: rhs = Fn("ceil"); break;
				case FxlcOpcode.Round: rhs = Fn("round"); break;
				case FxlcOpcode.Min: rhs = Fn("min"); break;
				case FxlcOpcode.Max: rhs = Fn("max"); break;
				case FxlcOpcode.Add: rhs = Bin("+"); break;
				case FxlcOpcode.Mul: rhs = Bin("*"); break;
				case FxlcOpcode.Div: rhs = Bin("/"); break;
				case FxlcOpcode.Dot: rhs = Fn("dot"); break;
				case FxlcOpcode.Lt: rhs = string.Format("(({0} < {1}) ? 1 : 0)", s[0], s[1]); break;
				case FxlcOpcode.Ge: rhs = string.Format("(({0} >= {1}) ? 1 : 0)", s[0], s[1]); break;
				case FxlcOpcode.Cmp: rhs = string.Format("(({0} >= 0) ? {1} : {2})", s[0], s[1], s[2]); break;
				case FxlcOpcode.Movc: rhs = string.Format("(({0}) ? {1} : {2})", s[0], s[1], s[2]); break;
				default: return string.Format("// unsupported preshader op {0}: {1} = {2};", Opcode, dest, string.Join(", ", s));
			}
			return string.Format("{0} = {1};", dest, rhs);
		}
		public string ToString(ConstantTable ctab, Chunks.Fxlvm.Cli4Chunk cli)
		{
			var operands = string.Join(", ", Operands.Select(o => o.FormatOperand(ctab, cli)));
			return string.Format("{0} {1}",
					Opcode.ToString().ToLowerInvariant(),
					operands);
		}
	}
}
