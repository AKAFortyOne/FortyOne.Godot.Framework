using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using System;

namespace FortyOne.Fody.Helpers;

#nullable enable

// 我真是天才，太优雅了hahahahahaha
// amazing code.
public readonly struct MethodPrologue : IDisposable
{
	private readonly MethodBody _body;
	private readonly ILProcessor _il;
	private readonly Instruction _pos;
	
	public MethodPrologue(MethodDefinition method, Instruction? pos = null)
	{
		_body = method.Body;
		_body.SimplifyMacros();
		
		_il = _body.GetILProcessor();
		_pos = pos ?? _body.Instructions[0];
	}

	private MethodPrologue Add(Instruction instruction)
	{
		_il.InsertBefore(_pos, instruction);
		return this;
	}
	
	public MethodPrologue Insert(OpCode opcode) => Add(Instruction.Create(opcode));
	public MethodPrologue Insert(OpCode opcode, MethodReference method) => Add(Instruction.Create(opcode, method));
	
	public MethodPrologue InsertString(OpCode opcode, string value) => Add(Instruction.Create(opcode, value));
	public MethodPrologue BranchToOrigin(OpCode opcode) => Add(Instruction.Create(opcode, _pos));
	
	public void Dispose()
	{
		_body.OptimizeMacros();
	}
}
