using DXDecompiler.DX9Shader.Bytecode.Ctab;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DXDecompiler.DX9Shader
{
	public sealed class RegisterState
	{
		public readonly bool ColumnMajorOrder = true;

		private readonly CultureInfo _culture = CultureInfo.InvariantCulture;

		private ICollection<Constant> _constantDefinitions { get; } = new List<Constant>();
		private ICollection<ConstantInt> _constantIntDefinitions { get; } = new List<ConstantInt>();
		public IDictionary<RegisterKey, RegisterDeclaration> _registerDeclarations = new Dictionary<RegisterKey, RegisterDeclaration>();
		// Main-shader constant registers that are written by the preshader (offset..offset+count-1).
		// Set by HlslWriter so references resolve to the reconstructed `_poN` locals instead of "Error ConstN".
		public HashSet<uint> PreshaderOutputs = null;
		// Needed to disambiguate register type 3 (Addr in a vertex shader == Texture in a pixel shader).
		private ShaderType _shaderType;

		public RegisterState(ShaderModel shader)
		{
			Load(shader);
		}

		public ICollection<ConstantDeclaration> ConstantDeclarations { get; private set; }

		public IDictionary<RegisterKey, RegisterDeclaration> MethodInputRegisters { get; } = new Dictionary<RegisterKey, RegisterDeclaration>();
		public IDictionary<RegisterKey, RegisterDeclaration> MethodOutputRegisters { get; } = new Dictionary<RegisterKey, RegisterDeclaration>();

		private void Load(ShaderModel shader)
		{
			_shaderType = shader.Type;
			ConstantDeclarations = shader.ConstantTable.ConstantDeclarations;
			foreach(var constantDeclaration in ConstantDeclarations)
			{
				RegisterType registerType;
				switch(constantDeclaration.RegisterSet)
				{
					case RegisterSet.Bool:
						registerType = RegisterType.ConstBool;
						break;
					case RegisterSet.Float4:
						registerType = RegisterType.Const;
						break;
					case RegisterSet.Int4:
						registerType = RegisterType.ConstInt;
						break;
					case RegisterSet.Sampler:
						registerType = RegisterType.Sampler;
						break;
					default:
						throw new InvalidOperationException();
				}
				if(registerType == RegisterType.Sampler)
				{
					// Use declaration from declaration instruction instead
					continue;
				}
				for(uint r = 0; r < constantDeclaration.RegisterCount; r++)
				{
					var registerKey = new RegisterKey(registerType, constantDeclaration.RegisterIndex + r);
					var registerDeclaration = new RegisterDeclaration(registerKey);
					_registerDeclarations.Add(registerKey, registerDeclaration);
				}
			}

			foreach(var instruction in shader.Tokens.OfType<InstructionToken>().Where(i => i.HasDestination))
			{
				if(instruction.Opcode == Opcode.Dcl)
				{
					var registerDeclaration = new RegisterDeclaration(instruction);
					RegisterKey registerKey = registerDeclaration.RegisterKey;

					_registerDeclarations.Add(registerKey, registerDeclaration);

					switch(registerKey.Type)
					{
						case RegisterType.Input:
						case RegisterType.MiscType:
						case RegisterType.Texture when shader.Type == ShaderType.Pixel:
							MethodInputRegisters.Add(registerKey, registerDeclaration);
							break;
						case RegisterType.Output:
						case RegisterType.ColorOut:
						case RegisterType.AttrOut when shader.MajorVersion == 3 && shader.Type == ShaderType.Vertex:
							MethodOutputRegisters.Add(registerKey, registerDeclaration);
							break;
						case RegisterType.Sampler:
						case RegisterType.Addr:
							break;
						default:
							throw new Exception($"Unexpected dcl {registerKey.Type}");
					}
				}
				else if(instruction.Opcode == Opcode.Def)
				{
					var constant = new Constant(
						instruction.GetParamRegisterNumber(0),
						instruction.GetParamSingle(1),
						instruction.GetParamSingle(2),
						instruction.GetParamSingle(3),
						instruction.GetParamSingle(4));
					_constantDefinitions.Add(constant);
				}
				else if(instruction.Opcode == Opcode.DefI)
				{
					var constantInt = new ConstantInt(instruction.GetParamRegisterNumber(0),
						instruction.Data[1],
						instruction.Data[2],
						instruction.Data[3],
						instruction.Data[4]);
					_constantIntDefinitions.Add(constantInt);
				}
				else
				{
					// Find all assignments to color outputs, because pixel shader outputs are not declared.
					int destIndex = instruction.GetDestinationParamIndex();
					RegisterType registerType = instruction.GetParamRegisterType(destIndex);
					var registerNumber = instruction.GetParamRegisterNumber(destIndex);
					var registerKey = new RegisterKey(registerType, registerNumber);
					if(_registerDeclarations.ContainsKey(registerKey) == false)
					{
						var reg = new RegisterDeclaration(registerKey);
						_registerDeclarations[registerKey] = reg;
						switch(registerType)
						{	
							case RegisterType.AttrOut:
							case RegisterType.ColorOut:
							case RegisterType.DepthOut:
							case RegisterType.Output:
							case RegisterType.RastOut:
								MethodOutputRegisters[registerKey] = reg;
								break;

						}
					}

				}
			}

			// In vs_2_0 the RastOut register file holds oPos(0)=POSITION, oFog(1)=FOG and
			// oPts(2)=PSIZE. Only POSITION0 is a legal vertex-shader output semantic, so naming
			// oFog/oPts "POSITION"+number yields POSITION1/POSITION2 which fxc rejects (X4502).
			// These feed fixed-function stages (never a PS input), so re-route each onto the
			// lowest unused TEXCOORDn (keeping float4 width so the body is emitted unchanged).
			var usedTexCoords = new HashSet<uint>();
			foreach(var outputKey in MethodOutputRegisters.Keys)
			{
				if(outputKey.Type == RegisterType.TexCoordOut)
				{
					usedTexCoords.Add(outputKey.Number);
				}
			}
			foreach(var rastKey in MethodOutputRegisters.Keys
				.Where(k => k.Type == RegisterType.RastOut && k.Number != 0).ToList())
			{
				uint freeIndex = 0;
				while(usedTexCoords.Contains(freeIndex))
				{
					freeIndex++;
				}
				usedTexCoords.Add(freeIndex);
				var remapped = new RegisterDeclaration(
					new RegisterKey(RegisterType.TexCoordOut, freeIndex));
				MethodOutputRegisters[rastKey] = remapped;
				_registerDeclarations[rastKey] = remapped;
			}
		}

		public string GetDestinationName(InstructionToken instruction)
		{
			int destIndex = instruction.GetDestinationParamIndex();
			RegisterKey registerKey = instruction.GetParamRegisterKey(destIndex);

			string registerName = GetRegisterName(registerKey);
			registerName = registerName ?? instruction.GetParamRegisterName(destIndex);
			var registerLength = GetRegisterFullLength(registerKey);
			string writeMaskName = instruction.GetDestinationWriteMaskName(registerLength, true);

			return string.Format("{0}{1}", registerName, writeMaskName);
		}

		// Relative addressing (c[a0.x]) inserts an extra operand after the source it modifies,
		// so the caller's "dense" source index must be mapped to the real operand slot.
		private int ResolveOperandIndex(InstructionToken instruction, int denseIndex)
		{
			int actual = 0;
			for(int d = 0; d < denseIndex; d++)
			{
				actual += instruction.IsRelativeAddressMode(actual) ? 2 : 1;
			}
			return actual;
		}

		public string GetSourceName(InstructionToken instruction, int srcIndex)
		{
			srcIndex = ResolveOperandIndex(instruction, srcIndex);
			string sourceRegisterName;
			RegisterKey registerKey = instruction.GetParamRegisterKey(srcIndex);
			var registerType = instruction.GetParamRegisterType(srcIndex);
			bool relativeAddr = instruction.IsRelativeAddressMode(srcIndex);
			string relAddrReg = relativeAddr
				? $"{instruction.GetParamRegisterName(srcIndex + 1)}{instruction.GetSourceSwizzleName(srcIndex + 1)}"
				: "";
			string relativeIndex = relativeAddr ? $"[{relAddrReg}]" : "";
			switch(registerType)
			{
				case RegisterType.Const:
				case RegisterType.Const2:
				case RegisterType.Const3:
				case RegisterType.Const4:
				case RegisterType.ConstBool:
				case RegisterType.ConstInt:
					sourceRegisterName = GetSourceConstantName(instruction, srcIndex);
					if(sourceRegisterName != null)
					{
						return sourceRegisterName;
					}

					ParameterType parameterType;
					switch(registerType)
					{
						case RegisterType.Const:
						case RegisterType.Const2:
						case RegisterType.Const3:
						case RegisterType.Const4:
							parameterType = ParameterType.Float;
							break;
						case RegisterType.ConstBool:
							parameterType = ParameterType.Bool;
							break;
						case RegisterType.ConstInt:
							parameterType = ParameterType.Int;
							break;
						default:
							throw new NotImplementedException();
					}
					var registerNumber = instruction.GetParamRegisterNumber(srcIndex);
					// Prefer the type-specific match, but fall back to any non-sampler constant:
					// e.g. an `int` param (g_dwHeightFogCon) can live in the float (c) register file.
					ConstantDeclaration decl = FindConstant(parameterType, registerNumber) ?? FindConstant(registerNumber);
					if(decl == null)
					{
						if(PreshaderOutputs != null && PreshaderOutputs.Contains(registerNumber))
						{
							// Preshader-computed constant: use the reconstructed local; swizzle/modifier appended below.
							sourceRegisterName = $"_po{registerNumber}";
							break;
						}
						// Constant register not found in def statements nor the constant table
						//TODO:
						return $"Error {registerType}{registerNumber}";
						//throw new NotImplementedException();
					}
					var totalOffset = registerNumber - decl.RegisterIndex;
					var data = decl.GetRegisterTypeByOffset(totalOffset);
					var offsetFromMember = registerNumber - data.RegisterIndex;
					sourceRegisterName = decl.GetMemberNameByOffset(totalOffset);
					if(relativeAddr &&
						(data.Type.ParameterClass == ParameterClass.MatrixColumns ||
						 data.Type.ParameterClass == ParameterClass.MatrixRows))
					{
						// Relative addressing into a matrix ARRAY (e.g. bone skinning: c2[a0.x] -> SkinBone[a0.x/3]).
						// a0.x holds the register offset (element * registersPerMatrix); recover the array index.
						uint regsPerElem = data.Type.ParameterClass == ParameterClass.MatrixColumns
							? data.Type.Columns : data.Type.Rows;
						if(regsPerElem == 0) regsPerElem = 1;
						uint baseElem = totalOffset / regsPerElem;
						// a0 holds the register offset (element * regsPerElem, always an exact multiple).
						// vs_2_0 has no integer ops, so fxc emulates the divide as floor(a0 * (1/N)) with
						// a rounded-DOWN reciprocal (0.3333333 < 1/3): an exact 3*b yields 0.9999997*b and
						// floors to b-1, shifting every bone down by one. The +1 lands the floor on b and
						// cannot cross into the next element (offset is a multiple of regsPerElem).
						string idx = baseElem == 0
							? $"({relAddrReg} + 1) / {regsPerElem}"
							: $"{baseElem} + ({relAddrReg} + 1) / {regsPerElem}";
						sourceRegisterName = data.Type.ParameterClass == ParameterClass.MatrixColumns
							? string.Format("transpose({0}[{1}])[{2}]", decl.Name, idx, offsetFromMember)
							: string.Format("{0}[{1}][{2}]", decl.Name, idx, offsetFromMember);
					}
					else if(data.Type.ParameterClass == ParameterClass.MatrixRows)
					{
						sourceRegisterName = string.Format("{0}[{1}]", decl.Name, offsetFromMember);
					}
					else if(data.Type.ParameterClass == ParameterClass.MatrixColumns)
					{
						sourceRegisterName = string.Format("transpose({0})[{1}]", decl.Name, offsetFromMember);
					}
					else if(relativeAddr)
					{
						// Relative addressing into a non-matrix constant array.
						sourceRegisterName += relativeIndex;
					}
					break;
				default:
					sourceRegisterName = GetRegisterName(registerKey);
					break;
			}

			sourceRegisterName = sourceRegisterName ?? instruction.GetParamRegisterName(srcIndex);

			sourceRegisterName += instruction.GetSourceSwizzleName(srcIndex, true);
			return ApplyModifier(instruction.GetSourceModifier(srcIndex), sourceRegisterName);
		}

		public uint GetRegisterFullLength(RegisterKey registerKey)
		{
			if(registerKey.Type == RegisterType.Const)
			{
				var constant = FindConstant(registerKey.Number);
				var data = constant.GetRegisterTypeByOffset(registerKey.Number - constant.RegisterIndex);
				if(data.Type.ParameterType != ParameterType.Float)
				{
					throw new NotImplementedException();
				}
				if(data.Type.ParameterClass == ParameterClass.MatrixColumns)
				{
					return data.Type.Rows;
				}
				return data.Type.Columns;
			}

			RegisterDeclaration decl = _registerDeclarations[registerKey];
			switch(decl.TypeName)
			{
				case "float":
					return 1;
				case "float2":
					return 2;
				case "float3":
					return 3;
				case "float4":
					return 4;
				default:
					throw new InvalidOperationException();
			}
		}

		public string GetRegisterName(RegisterKey registerKey)
		{
			var decl = _registerDeclarations[registerKey];
			switch(registerKey.Type)
			{
				case RegisterType.Texture:
				case RegisterType.Input:
					// RegisterType.Addr == Texture: in a vertex shader, register type 3 is the address register a0.
					if(_shaderType == ShaderType.Vertex && registerKey.Type == RegisterType.Texture)
					{
						return $"a{registerKey.Number}";
					}
					return (MethodInputRegisters.Count == 1) ? decl.Name : ("i." + decl.Name);
				case RegisterType.RastOut:
				case RegisterType.Output:
				case RegisterType.AttrOut:
				case RegisterType.ColorOut:
					if(MethodOutputRegisters.Count > 1)
					{
						return "o." + decl.Name;
					}
					// A single input + single output can derive the SAME semantic-based name
					// (e.g. a ps_2_0 COLOR input v0 and the ColorOut oC0 both -> "color"); with
					// no i./o. prefixes the output local redefines the input parameter (fxc X3036).
					string outputName = decl.Name;
					if(MethodInputRegisters.Count == 1 &&
						MethodInputRegisters.Values.First().Name == outputName)
					{
						outputName = "o_" + outputName;
					}
					return outputName;
				case RegisterType.Const:
					var constDecl = FindConstant(registerKey.Number);
					var relativeOffset = registerKey.Number - constDecl.RegisterIndex;
					var name = constDecl.GetMemberNameByOffset(relativeOffset);
					var data = constDecl.GetRegisterTypeByOffset(relativeOffset);

					// sanity check
					var registersOccupied = data.Type.ParameterClass == ParameterClass.MatrixColumns
						? data.Type.Columns
						: data.Type.Rows;
					if(registersOccupied == 1 && data.RegisterIndex != registerKey.Number)
					{
						throw new InvalidOperationException();
					}

					switch(data.Type.ParameterType)
					{
						case ParameterType.Float:
							if(registersOccupied == 1)
							{
								return name;
							}
							var subElement = (registerKey.Number - data.RegisterIndex).ToString();
							return ColumnMajorOrder
								? $"transpose({name})[{subElement}]" // subElement = col
								: $"{name}[{subElement}]"; // subElement = row;
						default:
							throw new NotImplementedException();
					}
				case RegisterType.Sampler:
					ConstantDeclaration samplerDecl = FindConstant(RegisterSet.Sampler, registerKey.Number);
					if(samplerDecl != null)
					{
						var offset = registerKey.Number - samplerDecl.RegisterIndex;
						return samplerDecl.GetMemberNameByOffset(offset);
					}
					else
					{
						throw new NotImplementedException();
					}
				case RegisterType.MiscType:
					switch(registerKey.Number)
					{
						case 0:
							return "vFace";
						case 1:
							return "vPos";
						default:
							throw new NotImplementedException();
					}
				case RegisterType.Temp:
					return registerKey.ToString();
				default:
					throw new NotImplementedException();
			}
		}

		public ConstantDeclaration FindConstant(RegisterInputNode register)
		{
			if(register.RegisterComponentKey.Type != RegisterType.Const)
			{
				return null;
			}
			return FindConstant(ParameterType.Float, register.RegisterComponentKey.Number);
		}

		public ConstantDeclaration FindConstant(RegisterSet set, uint index)
		{
			return ConstantDeclarations.FirstOrDefault(c =>
				c.RegisterSet == set &&
				c.ContainsIndex(index));
		}

		public ConstantDeclaration FindConstant(ParameterType type, uint index)
		{
			return ConstantDeclarations.FirstOrDefault(c =>
				c.ParameterType == type &&
				c.ContainsIndex(index));
		}


		public ConstantDeclaration FindConstant(uint index)
		{
			// c-registers (float/int/bool) live in a different register file than samplers;
			// never resolve a numeric constant index to a sampler declaration.
			return ConstantDeclarations.FirstOrDefault(c =>
				c.RegisterSet != RegisterSet.Sampler && c.ContainsIndex(index));
		}

		private string GetSourceConstantName(InstructionToken instruction, int srcIndex)
		{
			var registerType = instruction.GetParamRegisterType(srcIndex);
			var registerNumber = instruction.GetParamRegisterNumber(srcIndex);

			switch(registerType)
			{
				case RegisterType.ConstBool:
					//throw new NotImplementedException();
					return null;
				case RegisterType.ConstInt:
					{
						var constantInt = _constantIntDefinitions.FirstOrDefault(x => x.RegisterIndex == registerNumber);
						if(constantInt == null)
						{
							return null;
						}
						byte[] swizzle = instruction.GetSourceSwizzleComponents(srcIndex);
						// Same non-prefix-write-mask alignment as the float def-constant path below:
						// a masked destination reads the source at the write-mask channels, not the
						// first N swizzle slots.
						int[] intSlots = { 0, 1, 2, 3 };
						if(instruction.HasDestination &&
							instruction.Opcode != Opcode.Dp3 &&
							instruction.Opcode != Opcode.Dp4)
						{
							ComponentFlags intMask = instruction.GetDestinationWriteMask();
							var s = new List<int>();
							for(int c = 0; c < 4; c++)
								if((intMask & (ComponentFlags)(1 << c)) != ComponentFlags.None)
									s.Add(c);
							while(s.Count < 4) s.Add(s.Count == 0 ? 0 : s[s.Count - 1]);
							intSlots = s.ToArray();
						}
						uint[] constant = {
								constantInt[swizzle[intSlots[0]]],
								constantInt[swizzle[intSlots[1]]],
								constantInt[swizzle[intSlots[2]]],
								constantInt[swizzle[intSlots[3]]] };

						switch(instruction.GetSourceModifier(srcIndex))
						{
							case SourceModifier.None:
								break;
							case SourceModifier.Negate:
								throw new NotImplementedException();
							/*
							for (int i = 0; i < 4; i++)
							{
								constant[i] = -constant[i];
							}
							break;*/
							default:
								throw new NotImplementedException();
						}

						int destLength = instruction.GetDestinationMaskLength();
						switch(destLength)
						{
							case 1:
								return constant[0].ToString();
							case 2:
								if(constant[0] == constant[1])
								{
									return constant[0].ToString();
								}
								return $"int2({constant[0]}, {constant[1]})";
							case 3:
								if(constant[0] == constant[1] && constant[0] == constant[2])
								{
									return constant[0].ToString();
								}
								return $"int3({constant[0]}, {constant[1]}, {constant[2]})";
							case 4:
								if(constant[0] == constant[1] && constant[0] == constant[2] && constant[0] == constant[3])
								{
									return constant[0].ToString();
								}
								return $"int4({constant[0]}, {constant[1]}, {constant[2]}, {constant[3]})";
							default:
								throw new InvalidOperationException();
						}
					}

				case RegisterType.Const:
				case RegisterType.Const2:
				case RegisterType.Const3:
				case RegisterType.Const4:
					{
						var constantRegister = _constantDefinitions.FirstOrDefault(x => x.RegisterIndex == registerNumber);
						if(constantRegister == null)
						{
							return null;
						}

						byte[] swizzle = instruction.GetSourceSwizzleComponents(srcIndex);
						// A masked ALU destination is evaluated per channel, so the source components
						// that survive are those at the destination write-mask channels -- NOT the first
						// N swizzle slots (mirrors GetSourceSwizzleName's HLSL path). Without this a
						// non-prefix mask like .xz reads slots 0,1 instead of 0,2, so e.g. "c.yyxw" under
						// .xz collapses to "1" (slots 0,1 both == y) instead of the correct float2(1, 0).
						int[] sourceSlots = { 0, 1, 2, 3 };
						if(instruction.HasDestination &&
							instruction.Opcode != Opcode.Dp3 &&
							instruction.Opcode != Opcode.Dp4)
						{
							ComponentFlags writeMask = instruction.GetDestinationWriteMask();
							var slots = new List<int>();
							for(int c = 0; c < 4; c++)
							{
								if((writeMask & (ComponentFlags)(1 << c)) != ComponentFlags.None)
								{
									slots.Add(c);
								}
							}
							while(slots.Count < 4) slots.Add(slots.Count == 0 ? 0 : slots[slots.Count - 1]);
							sourceSlots = slots.ToArray();
						}
						float[] constant = {
							constantRegister[swizzle[sourceSlots[0]]],
							constantRegister[swizzle[sourceSlots[1]]],
							constantRegister[swizzle[sourceSlots[2]]],
							constantRegister[swizzle[sourceSlots[3]]] };

						switch(instruction.GetSourceModifier(srcIndex))
						{
							case SourceModifier.None:
								break;
							case SourceModifier.Negate:
								for(int i = 0; i < 4; i++)
								{
									constant[i] = -constant[i];
								}
								break;
							default:
								throw new NotImplementedException();
						}

						int destLength;
						if(instruction.HasDestination)
						{
							destLength = instruction.GetDestinationMaskLength();
						}
						else
						{
							if(instruction.Opcode == Opcode.If || instruction.Opcode == Opcode.IfC)
							{
								// TODO
							}
							destLength = 4;
						}
						// dp3/dp4 read 3/4 source components regardless of the destination write mask,
						// so a def-constant vector must not be truncated to the (often scalar) dest width.
						if(instruction.Opcode == Opcode.Dp3) destLength = 3;
						else if(instruction.Opcode == Opcode.Dp4) destLength = 4;
						switch(destLength)
						{
							case 1:
								return constant[0].ToString(_culture);
							case 2:
								if(constant[0] == constant[1])
								{
									return constant[0].ToString(_culture);
								}
								return string.Format("float2({0}, {1})",
									constant[0].ToString(_culture),
									constant[1].ToString(_culture));
							case 3:
								if(constant[0] == constant[1] && constant[0] == constant[2])
								{
									return constant[0].ToString(_culture);
								}
								return string.Format("float3({0}, {1}, {2})",
									constant[0].ToString(_culture),
									constant[1].ToString(_culture),
									constant[2].ToString(_culture));
							case 4:
								if(constant[0] == constant[1] && constant[0] == constant[2] && constant[0] == constant[3])
								{
									return constant[0].ToString(_culture);
								}
								return string.Format("float4({0}, {1}, {2}, {3})",
									constant[0].ToString(_culture),
									constant[1].ToString(_culture),
									constant[2].ToString(_culture),
									constant[3].ToString(_culture));
							default:
								throw new InvalidOperationException();
						}
					}
				default:
					return null;
			}
		}


		private static string ApplyModifier(SourceModifier modifier, string value)
		{
			switch(modifier)
			{
				case SourceModifier.None:
					return value;
				case SourceModifier.Negate:
					return $"-{value}";
				case SourceModifier.Bias:
					return $"{value}_bias";
				case SourceModifier.BiasAndNegate:
					return $"-{value}_bias";
				case SourceModifier.Sign:
					return $"{value}_bx2";
				case SourceModifier.SignAndNegate:
					return $"-{value}_bx2";
				case SourceModifier.Complement:
					throw new NotImplementedException();
				case SourceModifier.X2:
					return $"(2 * {value})";
				case SourceModifier.X2AndNegate:
					return $"(-2 * {value})";
				case SourceModifier.DivideByZ:
					return $"{value}_dz";
				case SourceModifier.DivideByW:
					return $"{value}_dw";
				case SourceModifier.Abs:
					return $"abs({value})";
				case SourceModifier.AbsAndNegate:
					return $"-abs({value})";
				case SourceModifier.Not:
					throw new NotImplementedException();
				default:
					throw new NotImplementedException();
			}
		}
	}
}
