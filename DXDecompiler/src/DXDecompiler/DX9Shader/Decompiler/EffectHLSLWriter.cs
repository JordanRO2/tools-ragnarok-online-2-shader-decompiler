using DXDecompiler.DX9Shader.Bytecode.Ctab;
using DXDecompiler.DX9Shader.Decompiler;
using DXDecompiler.DX9Shader.FX9;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DXDecompiler.DX9Shader
{
	public class EffectHLSLWriter : DecompileWriter
	{
		EffectContainer EffectChunk;
		Dictionary<object, string> ShaderNames = new Dictionary<object, string>();
		// Matrix arrays (skinning bone palettes) that are relatively addressed in any of the
		// effect's shaders, mapped to their register count. These must be declared as a flat
		// float4[registerCount] instead of a float4x3[] matrix array so the reconstructed
		// relative access (SkinBone[a0.x + row]) recompiles to the correct stride. See
		// RegisterState.GetRelativeMatrixArrays / GetSourceName.
		Dictionary<string, uint> FlattenedMatrixArrays = new Dictionary<string, uint>();
		// Non-array transform matrices (matView/matProj/InvView/...) that any of the effect's
		// shaders access as column-major (their per-shader constant-table ParameterClass is
		// MatrixColumns, which is how RegisterState decides to emit `transpose(name)[k]`). These
		// must be declared `column_major` at the effect level -- regardless of the effect
		// parameter table's own reflected class (which for these scalar transforms is MatrixRows).
		// A `row_major` declaration paired with a `transpose()` body is an inconsistent pairing:
		// fxc allocates the matrix row-major and column-gathers, applying the TRANSPOSE of the
		// intended matrix, so the recompiled char VS transforms by matView^T/matProj^T and (with
		// any asymmetric view/proj) collapses geometry -> parts render black in-game. `column_major`
		// + `transpose()` recompiles to the original's register-direct dp4 (whole-register column
		// read), restoring the original MatrixColumns reflection and register orientation.
		HashSet<string> ColumnMajorMatrices = new HashSet<string>();
		// Matrices seen accessed as MatrixRows (plain `name[k]`, no transpose) in some shader; used
		// to avoid flipping a matrix to column_major that a plain access would then misread.
		HashSet<string> RowMajorAccessedMatrices = new HashSet<string>();
		// Float4 globals mapped to the ABSOLUTE constant-register index they occupy in the ORIGINAL
		// shaders, so the reconstructed declaration can pin each with `: register(cN)` and a full
		// recompile reproduces the original constant-register layout. Required because a plain
		// recompile REPACKS the vs_2_0 float-constant file: the original main char VS leaves a
		// c90-c95 preshader gap and places matProj@c96 / matView@c100 / InvView@c103, but every
		// recompile tight-packs them lower (matProj@c90 / matView@c94 / InvView@c97). The engine
		// binds several char VS transform constants by ABSOLUTE register, so a repacked shader reads
		// the transform matrices from the wrong registers -> garbage transform -> the character
		// renders fully transparent. Pinning restores the original register layout.
		Dictionary<string, ushort> OriginalRegisterIndex = new Dictionary<string, ushort>();
		// Names whose recorded register came from a VERTEX shader. The same global can sit at
		// different registers in a vertex vs a pixel shader (separate constant-register files); the
		// transparency bug is a vertex-shader absolute-register binding, so a vertex-shader
		// declaration must WIN over a pixel-shader one. This set gates that precedence regardless of
		// blob iteration order.
		HashSet<string> RegisterIndexFromVertex = new HashSet<string>();
		// Insertion order of pinned names, so a cross-name register collision can be resolved
		// deterministically in favour of the first-recorded name.
		List<string> RegisterIndexOrder = new List<string>();
		// VS/VS register conflicts (same name at different registers among vertex shaders). The
		// first vertex shader wins; conflicts are recorded here for diagnostics.
		List<string> RegisterIndexConflicts = new List<string>();
		public EffectHLSLWriter(EffectContainer effectChunk)
		{
			EffectChunk = effectChunk;
		}
		public static string Decompile(EffectContainer effectChunk)
		{
			var asmWriter = new EffectHLSLWriter(effectChunk);
			return asmWriter.Decompile();
		}
		void BuildNameLookup()
		{
			int shaderCount = 0;
			foreach(var blob in EffectChunk.VariableBlobs)
			{
				if(blob.IsShader)
				{
					ShaderNames[blob] = $"Shader{shaderCount++}";
				}
			}
			foreach(var blob in EffectChunk.StateBlobs)
			{
				if(blob.BlobType == StateBlobType.Shader ||
					blob.BlobType == StateBlobType.IndexShader)
				{
					ShaderNames[blob] = $"Shader{shaderCount++}";
				}
			}

		}
		void BuildFlattenedMatrixArrays()
		{
			foreach(var blob in EffectChunk.VariableBlobs)
			{
				if(blob.IsShader && blob.Shader != null)
				{
					CollectFlattenedMatrixArrays(blob.Shader);
				}
			}
			foreach(var blob in EffectChunk.StateBlobs)
			{
				if((blob.BlobType == StateBlobType.Shader ||
					blob.BlobType == StateBlobType.IndexShader) && blob.Shader != null)
				{
					CollectFlattenedMatrixArrays(blob.Shader);
				}
			}
		}
		void CollectFlattenedMatrixArrays(ShaderModel shader)
		{
			foreach(var kvp in RegisterState.GetRelativeMatrixArrays(shader))
			{
				FlattenedMatrixArrays[kvp.Key] = kvp.Value;
			}
		}
		void BuildColumnMajorMatrices()
		{
			foreach(var blob in EffectChunk.VariableBlobs)
			{
				if(blob.IsShader && blob.Shader != null)
				{
					CollectColumnMajorMatrices(blob.Shader);
				}
			}
			foreach(var blob in EffectChunk.StateBlobs)
			{
				if((blob.BlobType == StateBlobType.Shader ||
					blob.BlobType == StateBlobType.IndexShader) && blob.Shader != null)
				{
					CollectColumnMajorMatrices(blob.Shader);
				}
			}
		}
		void CollectColumnMajorMatrices(ShaderModel shader)
		{
			if(shader?.ConstantTable == null)
			{
				return;
			}
			foreach(var decl in shader.ConstantTable.ConstantDeclarations)
			{
				if(decl.RegisterSet != RegisterSet.Float4)
				{
					continue;
				}
				// Only non-array matrices: relatively-addressed matrix ARRAYS (skinning bone
				// palettes) are handled separately by FlattenedMatrixArrays.
				if(decl.Elements > 1)
				{
					continue;
				}
				if(decl.ParameterClass == ParameterClass.MatrixColumns)
				{
					// Accessed via transpose(name)[k] -> must be declared column_major.
					ColumnMajorMatrices.Add(decl.Name);
				}
				else if(decl.ParameterClass == ParameterClass.MatrixRows)
				{
					// Accessed plainly as name[k] -> a column_major declaration would misread it.
					RowMajorAccessedMatrices.Add(decl.Name);
				}
			}
		}
		void BuildOriginalRegisterMap()
		{
			foreach(var blob in EffectChunk.VariableBlobs)
			{
				if(blob.IsShader && blob.Shader != null)
				{
					CollectOriginalRegisters(blob.Shader);
				}
			}
			foreach(var blob in EffectChunk.StateBlobs)
			{
				if((blob.BlobType == StateBlobType.Shader ||
					blob.BlobType == StateBlobType.IndexShader) && blob.Shader != null)
				{
					CollectOriginalRegisters(blob.Shader);
				}
			}
			ResolveRegisterCollisions();
		}
		void CollectOriginalRegisters(ShaderModel shader)
		{
			if(shader?.ConstantTable == null)
			{
				return;
			}
			// Pin ONLY vertex-shader float constants. The transparency bug is exclusively a
			// vertex-shader absolute-register binding, so the vertex constant file is the only one that
			// must reproduce the original layout. Pixel-shader globals are deliberately NOT pinned:
			// `register(cN)` is an effect-wide (per-stage) reservation, and a char effect's pixel-shader
			// globals ROTATE registers between pixel-shader variants (e.g. Light0_Spec / Light0_WorldViewDir
			// / MaterialColor / fSHPower each cycle through c7/c8/c9 depending on whether the variant does
			// specular), so pinning them makes fxc reject the effect with X4500 "overlapping register
			// semantics". The vertex constant file has no such cross-name overlap.
			if(shader.Type != ShaderType.Vertex)
			{
				return;
			}
			foreach(var decl in shader.ConstantTable.ConstantDeclarations)
			{
				// Only the float constant file (c#). Samplers (s#), int (i#) and bool (b#) registers
				// live in separate files and are never pinned here, so their declarations stay clean.
				if(decl.RegisterSet != RegisterSet.Float4)
				{
					continue;
				}
				if(RegisterIndexFromVertex.Contains(decl.Name))
				{
					// The name was already recorded from another vertex shader. On a disagreement keep
					// the HIGHER register. Char effects ship several vertex-shader variants: the main
					// lit variant carries a preshader whose output registers open a c90-c95 gap, which
					// pushes its transform block UP (matProj@c96, matView@c100); a stripped variant with
					// no preshader / no InvView tight-packs the same matrices LOWER (matProj@c90,
					// matView@c94). The engine binds the main lit variant by absolute register, so the
					// gap-shifted (higher) register is the one to reproduce. Blob iteration order is not
					// guaranteed to visit the main lit variant first (it does not for Char_Hair), so a
					// plain "keep first" is unreliable; the gap only ever shifts the block up, so "keep
					// higher" deterministically selects the main lit layout.
					if(OriginalRegisterIndex.TryGetValue(decl.Name, out var existing) &&
						existing != decl.RegisterIndex)
					{
						var kept = Math.Max(existing, decl.RegisterIndex);
						var dropped = Math.Min(existing, decl.RegisterIndex);
						OriginalRegisterIndex[decl.Name] = kept;
						var note = $"{decl.Name}: kept c{kept}, ignored c{dropped}";
						if(!RegisterIndexConflicts.Contains(note))
						{
							RegisterIndexConflicts.Add(note);
						}
					}
					continue;
				}
				OriginalRegisterIndex[decl.Name] = decl.RegisterIndex;
				RegisterIndexFromVertex.Add(decl.Name);
				RegisterIndexOrder.Add(decl.Name);
			}
		}
		// Safety net: guarantee no two distinct names are pinned to the same register (which would make
		// fxc reject the effect with X4500). The vertex constant file has no such overlap for the char
		// shaders, but if a future effect did, keep the first-recorded name at a register and drop the
		// later ones (leaving them for fxc to allocate), noting each drop.
		void ResolveRegisterCollisions()
		{
			var claimedBy = new Dictionary<ushort, string>();
			foreach(var name in RegisterIndexOrder)
			{
				if(!OriginalRegisterIndex.TryGetValue(name, out var reg))
				{
					continue;
				}
				if(claimedBy.TryGetValue(reg, out var owner))
				{
					RegisterIndexConflicts.Add(
						$"c{reg}: kept {owner}, unpinned {name} (register reuse across names)");
					OriginalRegisterIndex.Remove(name);
				}
				else
				{
					claimedBy[reg] = name;
				}
			}
		}
		protected override void Write()
		{
			BuildNameLookup();
			BuildFlattenedMatrixArrays();
			BuildColumnMajorMatrices();
			BuildOriginalRegisterMap();
			foreach(var variable in EffectChunk.Variables)
			{
				WriteVariable(variable);
			}
			foreach(var blob in EffectChunk.StateBlobs)
			{
				if(blob.BlobType == StateBlobType.Shader ||
					blob.BlobType == StateBlobType.IndexShader)
				{
					WriteShader(blob);
				}
			}
			foreach(var technique in EffectChunk.Techniques)
			{
				WriteTechnique(technique);
			}
		}
		void WriteShader(StateBlob blob) => WriteShader(ShaderNames[blob], blob.Shader);
		void WriteShader(string shaderName, ShaderModel shader)
		{
			WriteLine($"// {shaderName} {shader.Type}_{shader.MajorVersion}_{shader.MinorVersion} Has PRES {shader.Preshader != null}");
			var funcName = shaderName;
			var text = "";
			if(shader.Type == ShaderType.Expression)
			{
				text = ExpressionHLSLWriter.Decompile(shader, funcName);
			}
			else
			{
				text = HlslWriter.Decompile(shader, funcName, emitGlobals: false);
				// text = text.Replace("main(", $"{funcName}(");
			}
			WriteLine(text);
		}
		public string StateBlobToString(Assignment key)
		{
			if(!EffectChunk.StateBlobLookup.ContainsKey(key))
			{
				return $"Key not found";
			}
			var data = EffectChunk.StateBlobLookup[key];
			if(data == null)
			{
				// Only VertexShader/PixelShader states reach here with a null blob; 'PixelShader
				// = NULL;' is valid fx, whereas "Blob is NULL" is invalid syntax and aborts fxc.
				return "NULL";
			}
			if(data.BlobType == StateBlobType.Shader)
			{
				if(data.Shader.Type == ShaderType.Expression)
				{
					var funcName = ShaderNames[data];
					return $"{funcName}";
				}
				else
				{
					var funcName = ShaderNames[data];
					return $"compile {data.Shader.Type.GetDescription()}_{data.Shader.MajorVersion}_{data.Shader.MinorVersion} {funcName}()";
				}
			}
			if(data.BlobType == StateBlobType.Variable)
			{
				if(string.IsNullOrEmpty(data.VariableName))
				{
					return "NULL";
				}
				else
				{
					return $"<{data.VariableName}>";
				}
			}
			if(data.BlobType == StateBlobType.IndexShader)
			{
				var funcName = ShaderNames[data];
				return $"{data.VariableName}[{funcName}()]";
			}
			throw new ArgumentException();
		}
		public string VariableBlobToString(FX9.Parameter key, int index = 0)
		{
			if(!EffectChunk.VariableBlobLookup.ContainsKey(key))
			{
				return $"Key not found";
			}
			var data = EffectChunk.VariableBlobLookup[key][index];
			if(data == null)
			{
				return "Blob is NULL";
			}
			if(key.ParameterType == ParameterType.String)
			{
				// A string annotation/variable can never be a shader. Some string blobs are
				// misclassified as shaders by the 2-byte _IsShader heuristic and would otherwise
				// emit an invalid 'compile ps_0_0 ShaderN()' target that aborts fxc. Always emit
				// the string literal for a declared string parameter.
				return $"\"{data.Value}\"";
			}
			else if(data.IsShader)
			{
				var funcName = ShaderNames[data];
				return $"compile {data.Shader.Type.GetDescription()}_{data.Shader.MajorVersion}_{data.Shader.MinorVersion} {funcName}()";
			}
			else
			{
				return $"<{data.Value}>";
			}
		}
		// Emit the type+name+array+semantic for a global. A matrix array that is relatively
		// addressed anywhere in the effect (skinning bone palette) is declared column_major so fxc
		// tight-packs it at Columns registers per element (float4x3[30] -> 90 regs, 3/bone), which
		// (a) matches the original register layout and (b) recompiles with the ORIGINAL reflected
		// parameter type (MatrixRows {Rows}x{Columns}[{Elements}]). This is required for engines that
		// upload the bone palette by the reflected matrix-array parameter (Gamebryo): a flat
		// float4[registerCount] declaration reflects as Vector[registerCount] instead, the engine
		// then fails to upload the palette, the bone registers stay zero, and every skinned vertex
		// collapses to the origin -> the character renders fully transparent/invisible. The body
		// indexes the array by bone (the address register is rescaled by Columns at the mova) and
		// selects register-column k with a static basis mul (see HlslWriter.MovA / RegisterState).
		// For a float4-set global whose ORIGINAL absolute register was captured, return the
		// ` : register(cN)` suffix that pins it back to that register (appended after the semantic).
		// Empty for anything not in the map (samplers/int/bool params and constants that never appear
		// in a shader constant table), leaving those declarations untouched.
		string GetRegisterPinSuffix(FX9.Parameter param)
		{
			// Only float-family globals are allocated in the float (c#) file. A preshader constant
			// table can list a bool/int global (fog enable, packed fog colour, ...) as a Float4
			// constant, so such names DO appear in the map; but the effect-level parameter is bool/int
			// and must keep its b#/i# register file -> never emit a c-register for it. Samplers and
			// textures are likewise excluded.
			if(param.ParameterType != ParameterType.Float)
			{
				return string.Empty;
			}
			// Never pin the flattened bone-matrix array. `register(cN)` is an effect-wide (cross-stage)
			// reservation, so pinning SkinBone[30] would reserve its whole c0..c89 span in the PIXEL
			// constant file too; fxc then has to push the pixel shaders' own low constants (fog/light
			// colours at c5..c10 in the original) above c89, past ps_2_0's c31 limit -> X5200 "invalid
			// register number". The bone palette is the first, largest constant and fxc always packs it
			// back at c0 on its own, so it reproduces the original layout without a pin and without
			// stealing the pixel stage's low registers.
			if(FlattenedMatrixArrays.ContainsKey(param.Name))
			{
				return string.Empty;
			}
			if(OriginalRegisterIndex.TryGetValue(param.Name, out var registerIndex))
			{
				return string.Format(" : register(c{0})", registerIndex);
			}
			return string.Empty;
		}
		string GetVariableDeclaration(FX9.Parameter param)
		{
			bool isMatrixArray = param.ElementCount > 1 &&
				(param.ParameterClass == ParameterClass.MatrixColumns ||
				 param.ParameterClass == ParameterClass.MatrixRows);
			if(isMatrixArray && FlattenedMatrixArrays.ContainsKey(param.Name))
			{
				var semantic = string.IsNullOrEmpty(param.Semantic)
					? string.Empty
					: string.Format(" : {0}", param.Semantic);
				return string.Format("column_major {0}{1}x{2} {3}[{4}]{5}{6}",
					param.ParameterType.ToString().ToLower(), param.Rows, param.Columns,
					param.Name, param.ElementCount, semantic, GetRegisterPinSuffix(param));
			}
			// A non-array transform matrix that the shaders access via transpose() (per-shader CTAB
			// class MatrixColumns) must be declared column_major so `column_major`+`transpose()`
			// recompiles to the original register-direct dp4 instead of applying the transpose.
			// The effect parameter table reflects these as MatrixRows (-> Parameter.GetTypeName would
			// force row_major), so override the declaration here. Skip if any shader also accesses the
			// same matrix plainly as MatrixRows (would be misread under column_major).
			bool isNonArrayMatrix = param.ElementCount <= 1 &&
				(param.ParameterClass == ParameterClass.MatrixRows ||
				 param.ParameterClass == ParameterClass.MatrixColumns);
			if(isNonArrayMatrix &&
				ColumnMajorMatrices.Contains(param.Name) &&
				!RowMajorAccessedMatrices.Contains(param.Name))
			{
				var semantic = string.IsNullOrEmpty(param.Semantic)
					? string.Empty
					: string.Format(" : {0}", param.Semantic);
				var typeName = param.ParameterType.ToString().ToLower();
				return string.Format("column_major {0}{1}x{2} {3}{4}{5}",
					typeName, param.Rows, param.Columns, param.Name, semantic,
					GetRegisterPinSuffix(param));
			}
			return param.GetDecleration() + GetRegisterPinSuffix(param);
		}
		public void WriteVariable(Variable variable)
		{
			var param = variable.Parameter;
			WriteIndent();
			var isShaderArray = param.ParameterClass == ParameterClass.Object &&
				(param.ParameterType == ParameterType.PixelShader || param.ParameterType == ParameterType.VertexShader);
			Dictionary<uint, string> shaderArrayElements = null;
			if(isShaderArray)
			{
				shaderArrayElements = new Dictionary<uint, string>();
				var blobs = EffectChunk.VariableBlobLookup[param];
				var index = 0;
				foreach(var blob in blobs)
				{
					var name = $"{variable.Parameter.Name}_Shader_{index}";
					shaderArrayElements.Add(blob.Index, name);
					WriteShader(name, blob.Shader);
					++index;
				}
			}
			Write(GetVariableDeclaration(param));
			if(variable.Annotations.Count > 0)
			{
				Write(" ");
				WriteAnnotations(variable.Annotations);
			}
			if(param.ParameterType.IsSampler())
			{
				WriteLine(" =");
				if(variable.SamplerStates.Count > 1)
				{
					WriteLine("{");
					Indent++;
				}
				for(int i = 0; i < variable.SamplerStates.Count; i++)
				{
					var state = variable.SamplerStates[i];
					WriteIndent();
					WriteLine("sampler_state");
					WriteIndent();
					WriteLine("{");
					Indent++;
					foreach(var assignment in state.Assignments)
					{
						WriteIndent();
						if(assignment.Type == StateType.Texture)
						{
							var data = StateBlobToString(assignment);
							WriteLine("{0} = {1}; // {2}", assignment.Type, data, assignment.Value[0].UInt); // fix: StateBlobToString already wraps in <...>
						}
						else
						{
							WriteLine("{0} = {1};", assignment.Type, assignment.Value[0].UInt);
						}
					}
					Indent--;
					WriteIndent();
					Write("}");
					if(variable.SamplerStates.Count == 1)
					{
						WriteLine(";");
					}
					else if(i < variable.SamplerStates.Count - 1)
					{
						WriteLine(",");
					}
					else
					{
						WriteLine();
					}
				}
				if(variable.SamplerStates.Count > 1)
				{
					Indent--;
					WriteIndent();
					WriteLine("};");
				}
			}
			else
			{
				if(param.ParameterType.HasVariableBlob())
				{
					if(isShaderArray)
					{
						WriteLine(" = {");
						Indent++;
						foreach(var idx in variable.DefaultValue)
						{
							WriteIndent();
							WriteLine("{0}, // {1}", shaderArrayElements[idx.UInt], idx.UInt);
						}
						Indent--;
						WriteLine("};");
					}
					else
					{
						var data = VariableBlobToString(variable.Parameter);
						if(string.IsNullOrEmpty(data) || data == "<>")
						{
							WriteLine("; // {0}", variable.DefaultValue[0].UInt);
						}
						else
						{
							WriteLine(" = {0}; // {1}", data, variable.DefaultValue[0].UInt);
						}
					}	
				}
				else if(variable.DefaultValue.All(d => d.UInt == 0))
				{
					WriteLine(";");
				}
				else
				{
					var defaultValue = string.Join(", ", variable.DefaultValue);
					WriteLine(" = {{ {0} }};", defaultValue);
				}
			}
		}
		public void WriteAnnotations(List<Annotation> annotations)
		{
			Write("<");
			for(int i = 0; i < annotations.Count; i++)
			{
				var annotation = annotations[i];
				var value = string.Join(", ", annotation.Value);
				if(annotation.Parameter.ParameterType.HasVariableBlob())
				{
					Write("{0} {1} = {2};",
						annotation.Parameter.GetTypeName(),
						annotation.Parameter.Name,
						VariableBlobToString(annotation.Parameter));
				}
				else
				{
					Write("{0} {1} = {2};", annotation.Parameter.GetTypeName(), annotation.Parameter.Name, value);
				}
				if(i < annotations.Count - 1) Write(" ");
			}
			Write(">");
		}
		public void WriteTechnique(Technique technique)
		{
			Write("technique {0}", technique.Name);
			if(technique.Annotations.Count > 0)
			{
				Write(" ");
				WriteAnnotations(technique.Annotations);
			}
			WriteLine();
			WriteLine("{");
			Indent++;
			foreach(var pass in technique.Passes)
			{
				WritePass(pass);
			}
			Indent--;
			WriteLine("}");
			WriteLine("");
		}
		public void WritePass(Pass pass)
		{
			WriteIndent();
			Write("pass {0}", pass.Name);
			if(pass.Annotations.Count > 0)
			{
				Write(" ");
				WriteAnnotations(pass.Annotations);
			}
			WriteLine();
			WriteIndent();
			WriteLine("{");
			Indent++;
			var shaderAssignments = pass.Assignments;
			foreach(var assignment in shaderAssignments)
			{
				WriteAssignment(assignment);
			}
			Indent--;
			WriteIndent();
			WriteLine("}");
		}
		public void WriteAssignment(Assignment assignment)
		{
			WriteIndent();
			string index = "";
			if(assignment.Type.RequiresIndex())
			{
				index = string.Format("[{0}]", assignment.ArrayIndex);
			}
			// A pass render state driven by a preshader expression (e.g. 'FogColor = (dwFogColor)')
			// is stored as an Expression shader blob. fxc has no legal syntax to reference that
			// shader by name, so 'FogColor = ShaderN;' aborts compilation ("Unrecognized RHS value
			// in assignment" / "error initializing the compiler"). Emit it as a comment instead.
			if(EffectChunk.StateBlobLookup.ContainsKey(assignment))
			{
				var stateBlob = EffectChunk.StateBlobLookup[assignment];
				if(stateBlob != null &&
					stateBlob.BlobType == StateBlobType.Shader &&
					stateBlob.Shader.Type == ShaderType.Expression)
				{
					WriteLine("// {0}{1} = {2}; // preshader expression",
						assignment.Type.ToString(), index, StateBlobToString(assignment));
					return;
				}
			}
			string value;
			if(assignment.Value.Count > 1)
			{
				value = string.Format("{{ {0} }}", string.Join(", ", assignment.Value));
			}
			else if(EffectChunk.StateBlobLookup.ContainsKey(assignment))
			{
				value = StateBlobToString(assignment);
			}
			else
			{
				value = assignment.Value[0].ToString();
			}
			Write("{0}{1} = {2};", assignment.Type.ToString(), index, value);
			if(EffectChunk.StateBlobLookup.ContainsKey(assignment))
			{
				Write(" // {0}", assignment.Value[0].UInt);
			}
			WriteLine();
		}
	}
}
