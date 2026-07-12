using DXDecompiler.DX9Shader.Bytecode.Ctab;
using DXDecompiler.DX9Shader.Bytecode.Fxlvm;
using DXDecompiler.Util;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DXDecompiler.DX9Shader
{
	public class HlslWriter : DecompileWriter
	{
		private readonly ShaderModel _shader;
		private readonly bool _doAstAnalysis;
		private readonly bool _emitGlobals;
		private int _iterationDepth = 0;

		public RegisterState _registers;
		string _entryPoint;

		public HlslWriter(ShaderModel shader, bool doAstAnalysis = false, string entryPoint = null, bool emitGlobals = true)
		{
			_shader = shader;
			_doAstAnalysis = doAstAnalysis;
			_emitGlobals = emitGlobals;
			if(string.IsNullOrEmpty(entryPoint))
			{
				_entryPoint = $"{_shader.Type}Main";
			}
			else
			{
				_entryPoint = entryPoint;
			}

		}

		public static string Decompile(byte[] bytecode, string entryPoint = null)
		{
			var shaderModel = ShaderReader.ReadShader(bytecode);
			return Decompile(shaderModel);
		}
		public static string Decompile(ShaderModel shaderModel, string entryPoint = null, bool emitGlobals = true)
		{
			if(shaderModel.Type == ShaderType.Effect)
			{
				return EffectHLSLWriter.Decompile(shaderModel.EffectChunk);
			}
			var hlslWriter = new HlslWriter(shaderModel, false, entryPoint, emitGlobals);
			return hlslWriter.Decompile();
		}

		// Struct type names must be unique per shader so multi-shader effects don't collide.
		private string InputStructName =>
			(_shader.Type == ShaderType.Pixel ? "PS_IN_" : "VS_IN_") + _entryPoint;
		private string OutputStructName =>
			(_shader.Type == ShaderType.Pixel ? "PS_OUT_" : "VS_OUT_") + _entryPoint;

		private string GetDestinationName(InstructionToken instruction)
		{
			return _registers.GetDestinationName(instruction);
		}

		private string GetSourceName(InstructionToken instruction, int srcIndex)
		{
			return _registers.GetSourceName(instruction, srcIndex);
		}

		private static string GetConstantTypeName(ConstantType type)
		{
			switch(type.ParameterClass)
			{
				case ParameterClass.Scalar:
					return type.ParameterType.GetDescription();
				case ParameterClass.Vector:
					return type.ParameterType.GetDescription() + type.Columns;
				case ParameterClass.Struct:
					return "struct";
				case ParameterClass.MatrixColumns:
					return $"column_major {type.ParameterType.GetDescription()}{type.Rows}x{type.Columns}";
				case ParameterClass.MatrixRows:
					return $"row_major {type.ParameterType.GetDescription()}{type.Rows}x{type.Columns}";
				case ParameterClass.Object:
					switch(type.ParameterType)
					{
						case ParameterType.Sampler1D:
						case ParameterType.Sampler2D:
						case ParameterType.Sampler3D:
						case ParameterType.SamplerCube:
							return "sampler";
						default:
							throw new NotImplementedException();
					}
			}
			throw new NotImplementedException();
		}

		private void WriteInstruction(InstructionToken instruction)
		{
			WriteIndent();
			WriteLine($"// {instruction}");
			switch(instruction.Opcode)
			{
				case Opcode.Def:
				case Opcode.DefI:
				case Opcode.Dcl:
				case Opcode.End:
					return;
				// these opcodes doesn't need indents:
				case Opcode.Else:
					Indent--;
					WriteIndent();
					WriteLine("} else {");
					Indent++;
					return;
				case Opcode.Endif:
					Indent--;
					WriteIndent();
					WriteLine("}");
					return;
				case Opcode.EndRep:
					Indent--;
					_iterationDepth--;
					WriteIndent();
					WriteLine("}");
					return;
			}
			WriteIndent();
			switch(instruction.Opcode)
			{
				case Opcode.Abs:
					WriteLine("{0} = abs({1});", GetDestinationName(instruction),
						GetSourceName(instruction, 1));
					break;
				case Opcode.Add:
					WriteLine("{0} = {1} + {2};", GetDestinationName(instruction),
						GetSourceName(instruction, 1), GetSourceName(instruction, 2));
					break;
				case Opcode.Cmp:
					// D3D9 cmp is a per-component select whose comparison operand (src0) is a
					// broadcast of a single channel. Emit the condition as a scalar so the HLSL
					// ternary is valid regardless of the value width; otherwise a broadcast such
					// as ".www" yields bool3 ? float4 : float4 -> fxc X3020 "dimension of
					// conditional does not match value".
					WriteLine("{0} = ({1} >= 0) ? {2} : {3};", GetDestinationName(instruction),
						ScalarizeCondition(GetSourceName(instruction, 1)),
						GetSourceName(instruction, 2), GetSourceName(instruction, 3));
					break;
				case Opcode.DP2Add:
					WriteLine("{0} = dot({1}, {2}) + {3};", GetDestinationName(instruction),
						GetSourceName(instruction, 1), GetSourceName(instruction, 2), GetSourceName(instruction, 3));
					break;
				case Opcode.Dp3:
					WriteLine("{0} = dot({1}, {2});", GetDestinationName(instruction),
						GetSourceName(instruction, 1), GetSourceName(instruction, 2));
					break;
				case Opcode.Dp4:
					WriteLine("{0} = dot({1}, {2});", GetDestinationName(instruction),
						GetSourceName(instruction, 1), GetSourceName(instruction, 2));
					break;
				case Opcode.Exp:
					WriteLine("{0} = exp2({1});", GetDestinationName(instruction), GetSourceName(instruction, 1));
					break;
				case Opcode.Frc:
					WriteLine("{0} = frac({1});", GetDestinationName(instruction), GetSourceName(instruction, 1));
					break;
				case Opcode.If:
					{
						// D3D9 `if bN` tests a single boolean register. A named scalar `bool`
						// constant is a valid HLSL if-condition as-is, but when bN is produced by
						// the preshader it resolves to a float4 `_poN` local, which fxc rejects
						// (X3019: conditional must evaluate to a scalar). Select the .x channel.
						string ifCondition = GetSourceName(instruction, 0);
						if(ifCondition.StartsWith("_po") && !ifCondition.Contains("."))
						{
							ifCondition += ".x";
						}
						WriteLine("if ({0}) {{", ifCondition);
						Indent++;
						break;
					}
				case Opcode.IfC:
					if((IfComparison)instruction.Modifier == IfComparison.GE &&
						instruction.GetSourceModifier(0) == SourceModifier.AbsAndNegate &&
						instruction.GetSourceModifier(1) == SourceModifier.Abs &&
						instruction.GetParamRegisterName(0) + instruction.GetSourceSwizzleName(0) ==
						instruction.GetParamRegisterName(1) + instruction.GetSourceSwizzleName(1))
					{
						WriteLine("if ({0} == 0) {{", instruction.GetParamRegisterName(0) + instruction.GetSourceSwizzleName(0));
					}
					else if((IfComparison)instruction.Modifier == IfComparison.LT &&
						instruction.GetSourceModifier(0) == SourceModifier.AbsAndNegate &&
						instruction.GetSourceModifier(1) == SourceModifier.Abs &&
						instruction.GetParamRegisterName(0) + instruction.GetSourceSwizzleName(0) ==
						instruction.GetParamRegisterName(1) + instruction.GetSourceSwizzleName(1))
					{
						WriteLine("if ({0} != 0) {{", instruction.GetParamRegisterName(0) + instruction.GetSourceSwizzleName(0));
					}
					else
					{
						string ifComparison;
						switch((IfComparison)instruction.Modifier)
						{
							case IfComparison.GT:
								ifComparison = ">";
								break;
							case IfComparison.EQ:
								ifComparison = "==";
								break;
							case IfComparison.GE:
								ifComparison = ">=";
								break;
							case IfComparison.LE:
								ifComparison = "<=";
								break;
							case IfComparison.NE:
								ifComparison = "!=";
								break;
							case IfComparison.LT:
								ifComparison = "<";
								break;
							default:
								throw new InvalidOperationException();
						}
						WriteLine("if ({0} {2} {1}) {{", GetSourceName(instruction, 0), GetSourceName(instruction, 1), ifComparison);
					}
					Indent++;
					break;
				case Opcode.Log:
					WriteLine("{0} = log2({1});", GetDestinationName(instruction), GetSourceName(instruction, 1));
					break;
				case Opcode.Lrp:
					// D3D9 lrp: dest = src2 + src0*(src1-src2) = lerp(src2, src1, src0).
					WriteLine("{0} = lerp({3}, {2}, {1});", GetDestinationName(instruction),
						GetSourceName(instruction, 1), GetSourceName(instruction, 2), GetSourceName(instruction, 3));
					break;
				case Opcode.Mad:
					WriteLine("{0} = {1} * {2} + {3};", GetDestinationName(instruction),
						GetSourceName(instruction, 1), GetSourceName(instruction, 2), GetSourceName(instruction, 3));
					break;
				case Opcode.Max:
					WriteLine("{0} = max({1}, {2});", GetDestinationName(instruction),
						GetSourceName(instruction, 1), GetSourceName(instruction, 2));
					break;
				case Opcode.Min:
					WriteLine("{0} = min({1}, {2});", GetDestinationName(instruction),
						GetSourceName(instruction, 1), GetSourceName(instruction, 2));
					break;
				case Opcode.Mov:
					WriteLine("{0} = {1};", GetDestinationName(instruction), GetSourceName(instruction, 1));
					break;
				case Opcode.MovA:
					// D3D9 mova rounds to nearest before addressing; a0 is declared int4.
					WriteLine("{0} = round({1});", GetDestinationName(instruction), GetSourceName(instruction, 1));
					break;
				case Opcode.Mul:
					WriteLine("{0} = {1} * {2};", GetDestinationName(instruction),
						GetSourceName(instruction, 1), GetSourceName(instruction, 2));
					break;
				case Opcode.Nrm:
					WriteLine("{0} = normalize({1});", GetDestinationName(instruction), GetSourceName(instruction, 1));
					break;
				case Opcode.Pow:
					WriteLine("{0} = pow({1}, {2});", GetDestinationName(instruction),
						GetSourceName(instruction, 1), GetSourceName(instruction, 2));
					break;
				case Opcode.Rcp:
					WriteLine("{0} = 1 / {1};", GetDestinationName(instruction), GetSourceName(instruction, 1));
					break;
				case Opcode.Rsq:
					WriteLine("{0} = 1 / sqrt({1});", GetDestinationName(instruction), GetSourceName(instruction, 1));
					break;
				case Opcode.Sge:
					if(instruction.GetSourceModifier(1) == SourceModifier.AbsAndNegate &&
						instruction.GetSourceModifier(2) == SourceModifier.Abs &&
						instruction.GetParamRegisterName(1) + instruction.GetSourceSwizzleName(1) ==
						instruction.GetParamRegisterName(2) + instruction.GetSourceSwizzleName(2))
					{
						WriteLine("{0} = ({1} == 0) ? 1 : 0;", GetDestinationName(instruction),
							instruction.GetParamRegisterName(1) + instruction.GetSourceSwizzleName(1));
					}
					else
					{
						WriteLine("{0} = ({1} >= {2}) ? 1 : 0;", GetDestinationName(instruction), GetSourceName(instruction, 1),
							GetSourceName(instruction, 2));
					}
					break;
				case Opcode.Slt:
					WriteLine("{0} = ({1} < {2}) ? 1 : 0;", GetDestinationName(instruction), GetSourceName(instruction, 1),
						GetSourceName(instruction, 2));
					break;
				case Opcode.SinCos:
					WriteLine("sincos({1}, {0}, {0});", GetDestinationName(instruction), GetSourceName(instruction, 1));
					break;
				case Opcode.Sub:
					WriteLine("{0} = {1} - {2};", GetDestinationName(instruction),
						GetSourceName(instruction, 1), GetSourceName(instruction, 2));
					break;
				case Opcode.Tex:
					if((_shader.MajorVersion == 1 && _shader.MinorVersion >= 4) || (_shader.MajorVersion > 1))
					{
						WriteLine("{0} = tex2D({2}, {1});", GetDestinationName(instruction),
							GetSourceName(instruction, 1), GetSourceName(instruction, 2));
					}
					else
					{
						WriteLine("{0} = tex2D();", GetDestinationName(instruction));
					}
					break;
				case Opcode.TexLDL:
					WriteLine("{0} = tex2Dlod({2}, {1});", GetDestinationName(instruction),
						GetSourceName(instruction, 1), GetSourceName(instruction, 2));
					break;
				case Opcode.Comment:
					{
						byte[] bytes = new byte[instruction.Data.Length * sizeof(uint)];
						Buffer.BlockCopy(instruction.Data, 0, bytes, 0, bytes.Length);
						var ascii = FormatUtil.BytesToAscii(bytes);
						WriteLine($"// Comment: {ascii}");
						break;
					}
				case Opcode.Rep:
					WriteLine("for (int it{0} = 0; it{0} < {1}; ++it{0}) {{", _iterationDepth, GetSourceName(instruction, 0));
					_iterationDepth++;
					Indent++;
					break;
				case Opcode.TexKill:
					WriteLine("clip({0});", GetDestinationName(instruction));
					break;
				default:
					throw new NotImplementedException(instruction.Opcode.ToString());
			}
			// D3D9 `_sat` result modifier clamps the written result to [0,1]; apply it after the op.
			if(instruction.HasDestination &&
				(instruction.GetDestinationResultModifier() & ResultModifier.Saturate) != 0)
			{
				WriteIndent();
				WriteLine("{0} = saturate({0});", GetDestinationName(instruction));
			}
		}
		// The cmp condition (src0) is a broadcast of a single channel in D3D9. Collapse a
		// replicated swizzle like ".www" to one component (".w") so the emitted ternary
		// condition is scalar. Leaves genuine multi-component or non-swizzled operands as-is.
		private static string ScalarizeCondition(string condition)
		{
			int dot = condition.LastIndexOf('.');
			if(dot < 0) return condition;
			int end = dot + 1;
			while(end < condition.Length && "xyzw".IndexOf(condition[end]) >= 0) end++;
			string swizzle = condition.Substring(dot + 1, end - dot - 1);
			if(swizzle.Length <= 1 || !swizzle.All(c => c == swizzle[0])) return condition;
			return condition.Substring(0, dot + 1) + swizzle[0] + condition.Substring(end);
		}
		void WriteTemps()
		{
			Dictionary<RegisterKey, int> tempRegisters = new Dictionary<RegisterKey, int>();
			foreach(var inst in _shader.Instructions)
			{
				foreach(var operand in inst.Operands)
				{
					if(operand is DestinationOperand dest)
					{
						if(dest.RegisterType == RegisterType.Temp)
						{

							var registerKey = new RegisterKey(dest.RegisterType, dest.RegisterNumber);
							if(!tempRegisters.ContainsKey(registerKey))
							{
								var reg = new RegisterDeclaration(registerKey);
								_registers._registerDeclarations[registerKey] = reg;
								tempRegisters[registerKey] = (int)inst.GetDestinationWriteMask();
							}
							else
							{
								tempRegisters[registerKey] |= (int)inst.GetDestinationWriteMask();
							}
						}
					}
				}
			}
			if(tempRegisters.Count == 0) return;
			foreach(IGrouping<int, RegisterKey> group in tempRegisters.GroupBy(
				kv => kv.Value,
				kv => kv.Key))
			{
				int writeMask = group.Key;
				string writeMaskName; switch(writeMask)
				{
					case 0x1:
						writeMaskName = "float";
						break;
					case 0x3:
						writeMaskName = "float2";
						break;
					case 0x7:
						writeMaskName = "float3";
						break;
					case 0xF:
						writeMaskName = "float4";
						break;
					default:
						// TODO
						writeMaskName = "float4";
						break;
						//throw new NotImplementedException();
				}
				WriteIndent();
				WriteLine("{0} {1};", writeMaskName,
					string.Join(", ", group.Select(g => string.Format("{0} = ({1})0", g, writeMaskName))));
			}


		}
		protected override void Write()
		{
			if(_shader.Type == ShaderType.Expression)
			{
				Write($"// Writing expression");
				WriteExpression(_shader);
				return;
			}
			_registers = new RegisterState(_shader);

			if(_emitGlobals) WriteConstantDeclarations();

			if(_shader.Preshader != null)
			{
				// The preshader is reconstructed inline in the function body (below); here we
				// just register its output constants so the main body resolves them to `_poN`.
				_registers.PreshaderOutputs = CollectPreshaderOutputs(_shader.Preshader.Shader);
			}
			if(_registers.MethodInputRegisters.Count > 1)
			{
				WriteInputStructureDeclaration();
			}

			if(_registers.MethodOutputRegisters.Count > 1)
			{
				WriteOutputStructureDeclaration();
			}

			string methodReturnType = GetMethodReturnType();
			string methodParameters = GetMethodParameters();
			string methodSemantic = GetMethodSemantic();
			WriteLine("{0} {1}({2}){3}",
				methodReturnType,
				_entryPoint,
				methodParameters,
				methodSemantic);
			WriteLine("{");
			Indent++;

			if(_registers.MethodOutputRegisters.Count > 1)
			{
				var outputStructType = OutputStructName;
				WriteIndent();
				WriteLine($"{outputStructType} o = ({outputStructType})0;");
			}
			else
			{
				var output = _registers.MethodOutputRegisters.First().Value;
				WriteIndent();
				WriteLine("{0} {1} = ({0})0;", methodReturnType, _registers.GetRegisterName(output.RegisterKey));
			}
			WriteTemps();
			WriteAddressRegister();
			if(_shader.Preshader != null)
			{
				WritePreshaderInline(_shader.Preshader.Shader);
			}
			HlslAst ast = null;
			if(_doAstAnalysis)
			{
				var parser = new BytecodeParser();
				ast = parser.Parse(_shader);
				ast.ReduceTree();

				WriteAst(ast);
			}
			else
			{
				WriteInstructionList();

				if(_registers.MethodOutputRegisters.Count > 1)
				{
					WriteIndent();
					WriteLine($"return o;");
				}
				else
				{
					var output = _registers.MethodOutputRegisters.First().Value;
					WriteIndent();
					WriteLine($"return {_registers.GetRegisterName(output.RegisterKey)};");
				}

			}
			Indent--;
			WriteLine("}");
		}

		private void WriteConstantDeclarations()
		{
			if(_registers.ConstantDeclarations.Count != 0)
			{
				foreach(ConstantDeclaration declaration in _registers.ConstantDeclarations)
				{
					Write(declaration);
				}
			}
		}
		private void Write(ConstantDeclaration declaration)
		{
			Write(declaration.Type, declaration.Name);
			if(!declaration.DefaultValue.All(v => v == 0))
			{
				Write(" = {{ {0} }}", string.Join(", ", declaration.DefaultValue));
			}
			WriteLine(";");
			WriteLine();
		}
		private void Write(ConstantType type, string name, bool isStructMember = false)
		{
			string typeName = GetConstantTypeName(type);
			WriteIndent();
			Write("{0}", typeName);
			if(type.ParameterClass == ParameterClass.Struct)
			{
				WriteLine("");
				WriteLine("{");
				Indent++;
				foreach(var member in type.Members)
				{
					Write(member.Type, member.Name, true);
				}
				Indent--;
				WriteIndent();
				Write("}");
			}
			Write(" {0}", name);
			if(type.Elements > 1)
			{
				Write("[{0}]", type.Elements);
			}
			if(isStructMember)
			{
				Write(";\n");
			}
		}
		private void WriteInputStructureDeclaration()
		{
			var inputStructType = InputStructName;
			WriteLine($"struct {inputStructType}");
			WriteLine("{");
			Indent++;
			foreach(var input in _registers.MethodInputRegisters.Values)
			{
				WriteIndent();
				WriteLine($"{input.TypeName} {input.Name} : {input.Semantic};");
			}
			Indent--;
			WriteLine("};");
			WriteLine();
		}

		private void WriteOutputStructureDeclaration()
		{
			var outputStructType = OutputStructName;
			WriteLine($"struct {outputStructType}");
			WriteLine("{");
			Indent++;
			foreach(var output in _registers.MethodOutputRegisters.Values)
			{
				WriteIndent();
				WriteLine($"// {output.RegisterKey} {Operand.GetParamRegisterName(output.RegisterKey.Type, (uint)output.RegisterKey.Number)}");
				WriteIndent();
				WriteLine($"{output.TypeName} {output.Name} : {output.Semantic};");
			}
			Indent--;
			WriteLine("};");
			WriteLine();
		}

		private string GetMethodReturnType()
		{
			switch(_registers.MethodOutputRegisters.Count)
			{
				case 0:
					throw new InvalidOperationException();
				case 1:
					return _registers.MethodOutputRegisters.Values.First().TypeName;
				default:
					return OutputStructName;
			}
		}

		private string GetMethodSemantic()
		{
			switch(_registers.MethodOutputRegisters.Count)
			{
				case 0:
					throw new InvalidOperationException();
				case 1:
					string semantic = _registers.MethodOutputRegisters.Values.First().Semantic;
					return $" : {semantic}";
				default:
					return string.Empty;
			}
		}

		private string GetMethodParameters()
		{
			if(_registers.MethodInputRegisters.Count == 0)
			{
				return string.Empty;
			}
			else if(_registers.MethodInputRegisters.Count == 1)
			{
				var input = _registers.MethodInputRegisters.Values.First();
				return $"{input.TypeName} {input.Name} : {input.Semantic}";
			}

			return $"{InputStructName} i";
		}

		private void WriteAst(HlslAst ast)
		{
			var compiler = new NodeCompiler(_registers);

			var rootGroups = ast.Roots.GroupBy(r => r.Key.RegisterKey);
			if(_registers.MethodOutputRegisters.Count == 1)
			{
				var rootGroup = rootGroups.Single();
				var registerKey = rootGroup.Key;
				var roots = rootGroup.OrderBy(r => r.Key.ComponentIndex).Select(r => r.Value).ToList();
				string statement = compiler.Compile(roots, 4);

				WriteLine($"return {statement};");
			}
			else
			{
				foreach(var rootGroup in rootGroups)
				{
					var registerKey = rootGroup.Key;
					var roots = rootGroup.OrderBy(r => r.Key.ComponentIndex).Select(r => r.Value).ToList();
					RegisterDeclaration outputRegister = _registers.MethodOutputRegisters[registerKey];
					string statement = compiler.Compile(roots, roots.Count);

					WriteLine($"o.{outputRegister.Name} = {statement};");
				}

				WriteLine();
				WriteLine($"return o;");
			}
		}

		private void WriteInstructionList()
		{

			foreach(InstructionToken instruction in _shader.Tokens.OfType<InstructionToken>())
			{
				WriteInstruction(instruction);
			}
			WriteLine();
		}
		// Declare the address register a0 (int4) when a vertex shader uses relative addressing.
		private void WriteAddressRegister()
		{
			if(_shader.Type != ShaderType.Vertex) return;
			bool usesAddr = _shader.Tokens.OfType<InstructionToken>().Any(i => i.Opcode == Opcode.MovA);
			if(usesAddr)
			{
				WriteIndent();
				WriteLine("int4 a0 = 0;");
			}
		}

		// The set of main-shader constant registers written by the preshader (offset + expr index).
		private HashSet<uint> CollectPreshaderOutputs(ShaderModel pre)
		{
			var outputs = new HashSet<uint>();
			if(pre.Fxlc == null) return outputs;
			foreach(var tok in pre.Fxlc.Tokens)
			{
				foreach(var op in tok.Operands)
				{
					if(op.IsArray == 0 && op.OpType == FxlcOperandType.Expr)
					{
						outputs.Add(op.OpIndex / 4);
					}
				}
			}
			return outputs;
		}

		// Emit the preshader (FXLVM) as recompilable HLSL statements at the top of the shader
		// body. The math is uniform-only (params + literals), so fxc re-folds it into a preshader.
		private void WritePreshaderInline(ShaderModel pre)
		{
			if(pre.Fxlc == null) return;
			var temps = new SortedSet<uint>();
			var outs = new SortedSet<uint>();
			foreach(var tok in pre.Fxlc.Tokens)
			{
				foreach(var op in tok.Operands)
				{
					if(op.IsArray == 1)
					{
						if(op.ArrayType == FxlcOperandType.Temp) temps.Add(op.ArrayIndex / 4);
						if(op.ArrayType == FxlcOperandType.Expr) outs.Add(op.ArrayIndex / 4);
					}
					if(op.OpType == FxlcOperandType.Temp) temps.Add(op.OpIndex / 4);
					if(op.OpType == FxlcOperandType.Expr) outs.Add(op.OpIndex / 4);
				}
			}
			WriteIndent();
			WriteLine("// preshader (reconstructed; fxc re-folds uniform-only math into a preshader)");
			foreach(var t in temps) { WriteIndent(); WriteLine("float4 _pr{0} = 0;", t); }
			foreach(var o in outs) { WriteIndent(); WriteLine("float4 _po{0} = 0;", o); }
			foreach(var tok in pre.Fxlc.Tokens)
			{
				WriteIndent();
				WriteLine(tok.ToHlsl(pre.ConstantTable, pre.Cli));
			}
		}

		private void WriteExpression(ShaderModel shader)
		{
			WriteLine("void {0}Preshader(){{", _entryPoint);
			Indent++;
			WriteLine($"// {shader.Type}_{shader.MajorVersion}_{shader.MinorVersion}");
			foreach(var token in shader.Fxlc.Tokens)
			{
				WriteLine($"// {token.ToString(shader.ConstantTable, shader.Cli)}");
			}
			if(shader.Prsi != null)
			{
				foreach(var line in shader.Prsi.Dump().Replace("\r", "").Split('\n'))
				{
					WriteLine($"// {line}");
				}
			}
			Indent--;
			WriteLine("}");
		}
	}
}
