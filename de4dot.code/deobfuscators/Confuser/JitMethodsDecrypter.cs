﻿/*
    Copyright (C) 2011-2012 de4dot@gmail.com

    This file is part of de4dot.

    de4dot is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    de4dot is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with de4dot.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Metadata;
using Mono.MyStuff;
using de4dot.blocks;
using de4dot.PE;

namespace de4dot.code.deobfuscators.Confuser {
	class JitMethodsDecrypter : MethodsDecrypterBase, IStringDecrypter {
		MethodDefinition compileMethod;
		MethodDefinition hookConstructStr;
		MethodDataIndexes methodDataIndexes;

		struct MethodDataIndexes {
			public int codeSize;
			public int maxStack;
			public int ehs;
			public int localVarSigTok;
			public int options;
		}

		public JitMethodsDecrypter(ModuleDefinition module, ISimpleDeobfuscator simpleDeobfuscator)
			: base(module, simpleDeobfuscator) {
		}

		public JitMethodsDecrypter(ModuleDefinition module, ISimpleDeobfuscator simpleDeobfuscator, JitMethodsDecrypter other)
			: base(module, simpleDeobfuscator, other) {
		}

		protected override bool checkType(TypeDefinition type, MethodDefinition initMethod) {
			if (type == null)
				return false;
			if (type.NestedTypes.Count != 27)
				return false;

			compileMethod = findCompileMethod(type);
			if (compileMethod == null)
				return false;
			hookConstructStr = findHookConstructStr(type);
			if (hookConstructStr == null)
				return false;
			decryptMethod = findDecryptMethod(type);
			if (decryptMethod == null)
				return false;

			return true;
		}

		static MethodDefinition findCompileMethod(TypeDefinition type) {
			foreach (var method in type.Methods) {
				if (!method.IsStatic || method.Body == null)
					continue;
				if (method.Parameters.Count != 6)
					continue;
				if (method.MethodReturnType.ReturnType.EType != ElementType.U4)
					continue;
				if (method.Parameters[0].ParameterType.EType != ElementType.I)
					continue;
				if (method.Parameters[3].ParameterType.EType != ElementType.U4)
					continue;
				if (method.Parameters[4].ParameterType.FullName != "System.Byte**")
					continue;
				if (method.Parameters[5].ParameterType.FullName != "System.UInt32*")
					continue;

				return method;
			}
			return null;
		}

		static MethodDefinition findHookConstructStr(TypeDefinition type) {
			foreach (var nested in type.NestedTypes) {
				if (nested.Fields.Count != 10)
					continue;
				foreach (var method in nested.Methods) {
					if (method.IsStatic || method.Body == null)
						continue;
					if (method.Parameters.Count != 4)
						continue;
					if (method.Parameters[0].ParameterType.EType != ElementType.I)
						continue;
					if (method.Parameters[1].ParameterType.EType != ElementType.I)
						continue;
					if (method.Parameters[2].ParameterType.EType != ElementType.U4)
						continue;
					if (method.Parameters[3].ParameterType.FullName != "System.IntPtr&")
						continue;

					return method;
				}
			}
			return null;
		}

		public void initialize() {
			if (initMethod == null)
				return;
			if (!initializeKeys())
				throw new ApplicationException("Could not find all decryption keys");
			if (!initializeMethodDataIndexes(compileMethod))
				throw new ApplicationException("Could not find MethodData indexes");
		}

		bool initializeKeys() {
			simpleDeobfuscator.deobfuscate(initMethod);
			if (!findLKey0(initMethod, out lkey0))
				return false;
			if (!findKey0(initMethod, out key0))
				return false;
			if (!findKey1(initMethod, out key1))
				return false;
			if (!findKey2Key3(initMethod, out key2, out key3))
				return false;

			simpleDeobfuscator.deobfuscate(compileMethod);
			if (!findKey4(compileMethod, out key4))
				return false;

			simpleDeobfuscator.deobfuscate(hookConstructStr);
			if (!findKey5(hookConstructStr, out key5))
				return false;

			simpleDeobfuscator.deobfuscate(decryptMethod);
			if (!findKey6(decryptMethod, out key6))
				return false;

			return true;
		}

		static bool findKey4(MethodDefinition method, out uint key) {
			var instrs = method.Body.Instructions;
			for (int index = 0; index < instrs.Count; index++) {
				index = ConfuserUtils.findCallMethod(instrs, index, Code.Call, "System.Void System.Runtime.InteropServices.Marshal::Copy(System.Byte[],System.Int32,System.IntPtr,System.Int32)");
				if (index < 0)
					break;
				if (index + 2 >= instrs.Count)
					continue;
				if (!DotNetUtils.isLdloc(instrs[index + 1]))
					continue;
				var ldci4 = instrs[index + 2];
				if (!DotNetUtils.isLdcI4(ldci4))
					continue;

				key = (uint)DotNetUtils.getLdcI4Value(ldci4);
				return true;
			}

			key = 0;
			return false;
		}

		static bool findKey5(MethodDefinition method, out uint key) {
			var instrs = method.Body.Instructions;
			for (int i = 0; i + 4 < instrs.Count; i++) {
				int index = i;
				var ldci4_8 = instrs[index++];
				if (!DotNetUtils.isLdcI4(ldci4_8) || DotNetUtils.getLdcI4Value(ldci4_8) != 8)
					continue;
				if (instrs[index++].OpCode.Code != Code.Shl)
					continue;
				if (instrs[index++].OpCode.Code != Code.Or)
					continue;
				var ldci4 = instrs[index++];
				if (!DotNetUtils.isLdcI4(ldci4))
					continue;
				if (instrs[index++].OpCode.Code != Code.Xor)
					continue;

				key = (uint)DotNetUtils.getLdcI4Value(ldci4);
				return true;
			}

			key = 0;
			return false;
		}

		bool initializeMethodDataIndexes(MethodDefinition method) {
			var methodDataType = findFirstThreeIndexes(method, out methodDataIndexes.maxStack, out methodDataIndexes.ehs, out methodDataIndexes.options);
			if (methodDataType == null)
				return false;

			if (!findLocalVarSigTokIndex(method, methodDataType, out methodDataIndexes.localVarSigTok))
				return false;

			if (!findCodeSizeIndex(method, methodDataType, out methodDataIndexes.codeSize))
				return false;

			return true;
		}

		static TypeDefinition findFirstThreeIndexes(MethodDefinition method, out int maxStackIndex, out int ehsIndex, out int optionsIndex) {
			var instrs = method.Body.Instructions;
			for (int i = 0; i < instrs.Count; i++) {
				int index1 = findLdfldStind(instrs, i, false, true);
				if (index1 < 0)
					break;
				i = index1;

				int index2 = findLdfldStind(instrs, index1 + 1, true, true);
				if (index2 < 0)
					continue;

				int index3 = findLdfldStind(instrs, index2 + 1, true, false);
				if (index3 < 0)
					continue;

				var field1 = instrs[index1].Operand as FieldDefinition;
				var field2 = instrs[index2].Operand as FieldDefinition;
				var field3 = instrs[index3].Operand as FieldDefinition;
				if (field1 == null || field2 == null || field3 == null)
					continue;
				if (field1.DeclaringType != field2.DeclaringType || field1.DeclaringType != field3.DeclaringType)
					continue;

				maxStackIndex = getInstanceFieldIndex(field1);
				ehsIndex = getInstanceFieldIndex(field2);
				optionsIndex = getInstanceFieldIndex(field3);
				return field1.DeclaringType;
			}

			maxStackIndex = -1;
			ehsIndex = -1;
			optionsIndex = -1;
			return null;
		}

		static bool findLocalVarSigTokIndex(MethodDefinition method, TypeDefinition methodDataType, out int localVarSigTokIndex) {
			var instrs = method.Body.Instructions;
			for (int i = 0; i < instrs.Count - 1; i++) {
				var ldfld = instrs[i];
				if (ldfld.OpCode.Code != Code.Ldfld)
					continue;
				var field = ldfld.Operand as FieldDefinition;
				if (field == null || field.DeclaringType != methodDataType)
					continue;

				var call = instrs[i + 1];
				if (call.OpCode.Code != Code.Call)
					continue;
				var calledMethod = call.Operand as MethodDefinition;
				if (calledMethod == null || !calledMethod.IsStatic || calledMethod.DeclaringType != method.DeclaringType)
					continue;

				localVarSigTokIndex = getInstanceFieldIndex(field);
				return true;
			}

			localVarSigTokIndex = -1;
			return false;
		}

		static bool findCodeSizeIndex(MethodDefinition method, TypeDefinition methodDataType, out int codeSizeIndex) {
			var instrs = method.Body.Instructions;
			for (int i = 0; i < instrs.Count - 1; i++) {
				var ldfld = instrs[i];
				if (ldfld.OpCode.Code != Code.Ldfld)
					continue;
				var field = ldfld.Operand as FieldDefinition;
				if (field == null || field.DeclaringType != methodDataType)
					continue;

				if (instrs[i+1].OpCode.Code != Code.Stfld)
					continue;

				codeSizeIndex = getInstanceFieldIndex(field);
				return true;
			}

			codeSizeIndex = -1;
			return false;
		}

		static int getInstanceFieldIndex(FieldDefinition field) {
			int i = 0;
			foreach (var f in field.DeclaringType.Fields) {
				if (f.IsStatic)
					continue;
				if (f == field)
					return i;
				i++;
			}
			throw new ApplicationException("Could not find field");
		}

		static int findLdfldStind(IList<Instruction> instrs, int index, bool onlyInBlock, bool checkStindi4) {
			for (int i = index; i < instrs.Count - 1; i++) {
				var ldfld = instrs[i];
				if (onlyInBlock && ldfld.OpCode.FlowControl != FlowControl.Next)
					break;

				if (ldfld.OpCode.Code != Code.Ldfld)
					continue;

				var stindi4 = instrs[i + 1];
				if (checkStindi4 && stindi4.OpCode.Code != Code.Stind_I4)
					continue;

				return i;
			}
			return -1;
		}

		public bool decrypt(PeImage peImage, byte[] fileData, ref DumpedMethods dumpedMethods) {
			if (initMethod == null)
				return false;
			if (peImage.OptionalHeader.checkSum == 0)
				return false;

			methodsData = decryptMethodsData(peImage);
			dumpedMethods = decrypt(peImage, fileData);
			return dumpedMethods != null;
		}

		DumpedMethods decrypt(PeImage peImage, byte[] fileData) {
			var dumpedMethods = new DumpedMethods { StringDecrypter = this };

			var metadataTables = peImage.Cor20Header.createMetadataTables();
			var methodDef = metadataTables.getMetadataType(MetadataIndex.iMethodDef);
			uint methodDefOffset = methodDef.fileOffset;
			for (int i = 0; i < methodDef.rows; i++, methodDefOffset += methodDef.totalSize) {
				uint bodyRva = peImage.offsetReadUInt32(methodDefOffset);
				if (bodyRva == 0)
					continue;
				uint bodyOffset = peImage.rvaToOffset(bodyRva);

				if (!isEncryptedMethod(fileData, (int)bodyOffset))
					continue;

				var dm = new DumpedMethod();
				dm.token = (uint)(0x06000001 + i);
				dm.mdImplFlags = peImage.offsetReadUInt16(methodDefOffset + (uint)methodDef.fields[1].offset);
				dm.mdFlags = peImage.offsetReadUInt16(methodDefOffset + (uint)methodDef.fields[2].offset);
				dm.mdName = peImage.offsetRead(methodDefOffset + (uint)methodDef.fields[3].offset, methodDef.fields[3].size);
				dm.mdSignature = peImage.offsetRead(methodDefOffset + (uint)methodDef.fields[4].offset, methodDef.fields[4].size);
				dm.mdParamList = peImage.offsetRead(methodDefOffset + (uint)methodDef.fields[5].offset, methodDef.fields[5].size);

				int key = BitConverter.ToInt32(fileData, (int)bodyOffset + 6);
				int mdOffs = BitConverter.ToInt32(fileData, (int)bodyOffset + 2) ^ key;
				int len = BitConverter.ToInt32(fileData, (int)bodyOffset + 11) ^ ~key;
				int methodDataOffset = mdOffs + 2;
				uint[] methodData;
				byte[] codeData;
				decryptMethodData(methodsData, methodDataOffset, (uint)key, len, out methodData, out codeData);

				dm.mhFlags = 0x03;
				int maxStack = (int)methodData[methodDataIndexes.maxStack];
				dm.mhMaxStack = (ushort)maxStack;
				dm.mhLocalVarSigTok = methodData[methodDataIndexes.localVarSigTok];
				int numExceptions = (int)methodData[methodDataIndexes.ehs];
				uint options = methodData[methodDataIndexes.options];
				int codeSize = (int)methodData[methodDataIndexes.codeSize];

				var codeDataReader = new BinaryReader(new MemoryStream(codeData));
				if ((options >> 8) == 0) {
					dm.code = codeDataReader.ReadBytes(codeSize);
					dm.extraSections = readExceptionHandlers(codeDataReader, numExceptions);
				}
				else {
					dm.extraSections = readExceptionHandlers(codeDataReader, numExceptions);
					dm.code = codeDataReader.ReadBytes(codeSize);
				}
				if (codeDataReader.BaseStream.Position != codeDataReader.BaseStream.Length)
					throw new ApplicationException("Invalid method data");
				if (dm.extraSections != null)
					dm.mhFlags |= 8;
				dm.mhCodeSize = (uint)dm.code.Length;

				// Figure out if the original method was tiny or not.
				bool isTiny = dm.code.Length <= 0x3F &&
							dm.mhLocalVarSigTok == 0 &&
							dm.extraSections == null &&
							dm.mhMaxStack == 8;
				if (isTiny)
					dm.mhFlags |= 0x10;	// Set 'init locals'
				dm.mhFlags |= (ushort)(options & 0x10);	// copy 'init locals' bit

				dumpedMethods.add(dm);
			}

			return dumpedMethods;
		}

		static bool isEncryptedMethod(byte[] fileData, int offset) {
			return fileData[offset] == 0x46 &&
				fileData[offset + 1] == 0x21 &&
				fileData[offset + 10] == 0x20 &&
				fileData[offset + 15] == 0x26;
		}

		static byte[] readExceptionHandlers(BinaryReader reader, int numExceptions) {
			if (numExceptions == 0)
				return null;

			var memStream = new MemoryStream();
			var writer = new BinaryWriter(memStream);

			ulong header64 = (((ulong)numExceptions * 24) << 8) | 0x41;
			if (header64 > uint.MaxValue)
				throw new ApplicationException("Too many exception handlers...");
			writer.Write((uint)header64);
			for (int i = 0; i < numExceptions; i++) {
				writer.Write(reader.ReadUInt32());	// flags
				writer.Write(reader.ReadUInt32());	// try offset
				writer.Write(reader.ReadUInt32());	// try length
				writer.Write(reader.ReadUInt32());	// handler offset
				writer.Write(reader.ReadUInt32());	// handler length
				writer.Write(reader.ReadUInt32());	// catch token or filter offset
			}

			return memStream.ToArray();
		}

		void decryptMethodData(byte[] fileData, int offset, uint k1, int size, out uint[] methodData, out byte[] codeData) {
			var data = new byte[size];
			Array.Copy(fileData, offset, data, 0, data.Length);
			uint k2 = key4 * k1;
			for (int i = 0; i < data.Length; i++) {
				data[i] ^= (byte)k2;
				k2 = (byte)((k2 * data[i] + k1) % 0xFF);
			}

			methodData = new uint[5];
			Buffer.BlockCopy(data, 0, methodData, 0, 20);
			codeData = new byte[size - 20];
			Array.Copy(data, 20, codeData, 0, codeData.Length);
		}

		string IStringDecrypter.decrypt(uint token) {
			if ((token & 0xFF800000) != 0x70800000)
				return null;
			var reader = new BinaryReader(new MemoryStream(methodsData));
			reader.BaseStream.Position = (token & ~0xFF800000) + 2;
			int len = reader.ReadInt32();
			if ((len & 1) != 1)
				throw new ApplicationException("Invalid string len");
			int chars = len / 2;
			var sb = new StringBuilder(chars);
			for (int i = 0; i < chars; i++)
				sb.Append((char)(reader.ReadUInt16() ^ key5));
			return sb.ToString();
		}
	}
}
