using DXDecompiler.DX9Shader.Bytecode.Ctab;
using DXDecompiler.Util;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DXDecompiler.DX9Shader.FX9
{
	public class Parameter
	{
		public ParameterType ParameterType { get; private set; }
		public ParameterClass ParameterClass { get; private set; }
		public string Name { get; private set; }
		public string Semantic { get; private set; }
		public uint ElementCount { get; private set; }
		public uint Rows { get; private set; }
		public uint Columns { get; private set; }
		public uint StructMemberCount { get; private set; }
		public List<Parameter> StructMembers = new List<Parameter>();

		public static Parameter Parse(BytecodeReader reader, BytecodeReader variableReader)
		{
			var result = new Parameter();
			result.ParameterType = (ParameterType)variableReader.ReadUInt32();
			result.ParameterClass = (ParameterClass)variableReader.ReadUInt32();
			var nameOffset = variableReader.ReadUInt32();
			var semanticOffset = variableReader.ReadUInt32();
			if(result.ParameterClass == ParameterClass.Scalar ||
				result.ParameterClass == ParameterClass.Vector ||
				result.ParameterClass == ParameterClass.MatrixRows ||
				result.ParameterClass == ParameterClass.MatrixColumns)
			{
				result.ElementCount = variableReader.ReadUInt32();
				result.Rows = variableReader.ReadUInt32();
				result.Columns = variableReader.ReadUInt32();
			}
			if(result.ParameterClass == ParameterClass.Struct)
			{
				result.ElementCount = variableReader.ReadUInt32();
				result.StructMemberCount = variableReader.ReadUInt32();
				for(int i = 0; i < result.StructMemberCount; i++)
				{
					result.StructMembers.Add(Parameter.Parse(reader, variableReader));
				}
			}
			if(result.ParameterClass == ParameterClass.Object)
			{
				result.ElementCount = variableReader.ReadUInt32();
			}

			var nameReader = reader.CopyAtOffset((int)nameOffset);
			result.Name = nameReader.TryReadString();

			var semanticReader = reader.CopyAtOffset((int)semanticOffset);
			result.Semantic = semanticReader.TryReadString();
			return result;
		}
		public uint GetSize()
		{
			var elementCount = Math.Max(1, ElementCount);
			switch(ParameterClass)
			{
				case ParameterClass.Object:
					return 4 * elementCount;
				case ParameterClass.Scalar:
					return 4 * elementCount;
				case ParameterClass.Vector:
					return Rows * 4 * elementCount;
				case ParameterClass.MatrixColumns:
				case ParameterClass.MatrixRows:
					return Rows * Columns * 4 * elementCount;
				case ParameterClass.Struct:
					return (uint)StructMembers.Sum(m => m.GetSize()) * elementCount;
				default:
					return 0;
			}
		}
		public string GetDecleration(int indentLevel = 0)
		{
			string arrayDecl = "";
			string semanticDecl = "";
			if(ElementCount > 0)
			{
				arrayDecl = string.Format("[{0}]", ElementCount);
			}
			if(!string.IsNullOrEmpty(Semantic))
			{
				semanticDecl = string.Format(" : {0}", Semantic);
			}
			return string.Format("{0} {1}{2}{3}", GetTypeName(indentLevel), Name, arrayDecl, semanticDecl);
		}
		public string GetTypeName(int indentLevel = 0)
		{
			var sb = new StringBuilder();
			string indent = new string(' ', indentLevel * 4);
			sb.Append(indent);
			switch(ParameterClass)
			{
				case ParameterClass.Scalar:
					sb.Append(ParameterType.ToString().ToLower());
					break;
				case ParameterClass.Vector:
					sb.Append(ParameterType.ToString().ToLower());
					sb.Append(Rows);
					break;
				case ParameterClass.MatrixColumns:
					sb.Append("column_major ");
					sb.Append(ParameterType.ToString().ToLower());
					sb.Append(string.Format("{0}x{1}", Columns, Rows));
					break;
				case ParameterClass.MatrixRows:
					// A relatively-addressed matrix ARRAY (skinning bone matrices) is tight-packed by
					// D3DX with a Columns-sized register stride (e.g. float4x3[30] -> 90 regs, 3/bone);
					// emitting row_major makes fxc allocate a Rows-sized stride (120 regs, 4/bone) and
					// mis-fetch. column_major yields the Columns-sized stride matching the original and
					// still reflects as MatrixRows{Rows}x{Columns}. Only flip array matrices; scalar
					// transforms (matView/matProj/InvView, Elements 0) stay row_major.
					sb.Append(ElementCount > 1 ? "column_major " : "row_major ");
					sb.Append(ParameterType.ToString().ToLower());
					sb.Append(string.Format("{0}x{1}", Rows, Columns));
					break;
				case ParameterClass.Struct:
					{
						sb.AppendLine("struct {");
						foreach(var member in StructMembers)
						{
							sb.AppendLine(string.Format("{0};", member.GetDecleration(indentLevel + 1)));
						}
						sb.Append(indent);
						sb.Append("}");
					}
					break;
				case ParameterClass.Object:
					// HLSL sampler/texture object types are case-sensitive keywords;
					// a blanket ToLower() produces invalid tokens like "sampler2d".
					switch(ParameterType.ToString())
					{
						// D3D9 has no 1D sampling opcode: 1D textures are sampled through the same
						// texld/tex instruction as 2D, and HlslWriter always reconstructs that as
						// tex2D(sampler, float2). A sampler1D declaration is therefore never
						// compatible with the decompiled body, so declare 1D samplers as sampler2D.
						case "Sampler1D": sb.Append("sampler2D"); break;
						case "Sampler2D": sb.Append("sampler2D"); break;
						case "Sampler3D": sb.Append("sampler3D"); break;
						case "SamplerCube": sb.Append("samplerCUBE"); break;
						case "Texture1D": sb.Append("texture1D"); break;
						case "Texture2D": sb.Append("texture2D"); break;
						case "Texture3D": sb.Append("texture3D"); break;
						case "TextureCube": sb.Append("textureCUBE"); break;
						default: sb.Append(ParameterType.ToString().ToLower()); break;
					}
					break;
				default:
					break;
			}
			return sb.ToString();
		}
	}
}
