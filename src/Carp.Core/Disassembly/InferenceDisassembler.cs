using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver;
using Carp.Core.Architecture;
using Carp.Core.Disassembly.DataFlow;
using Carp.Core.Emulation;

namespace Carp.Core.Disassembly
{
    public class InferenceDisassembler
    {
        private readonly VMConstants _constants;
        private readonly KoiStream _koiStream;
        
        public InferenceDisassembler(VMConstants constants, KoiStream koiStream)
        {
            _constants = constants;
            _koiStream = koiStream ?? throw new ArgumentNullException(nameof(koiStream));
        }
        
        public IList<ILInstruction> Disassemble()
        {
            var instructions = new Dictionary<long, ILInstruction>();

            foreach (var export in _koiStream.Exports.Values)
                Disassemble(instructions, export);

            return instructions.OrderBy(x => x.Key).Select(x => x.Value).ToList();
        }

        private void Disassemble(IDictionary<long, ILInstruction> visited, VMExportInfo exportInfo)
        {
            var disassembler = new LinearDisassembler(_constants, new MemoryStreamReader(_koiStream.Data)
            {
                Position = exportInfo.CodeOffset
            }, exportInfo.EntryKey);

            var initialState = new ProgramState() {IP = exportInfo.CodeOffset};
            
            var agenda = new Stack<ProgramState>();
            agenda.Push(initialState);

            while (agenda.Count > 0)
            {
                var currentState = agenda.Pop();
                
                // Check if offset is already visited before.
                if (visited.TryGetValue((long) currentState.IP, out var instruction))
                {
                    // Check if program state is changed, if so, we need to revisit.
                    if (instruction.ProgramState.MergeWith(currentState))
                        currentState = instruction.ProgramState;
                    else
                        continue;
                }
                else
                {
                    // Offset is not visited yet, read instruction. 
                    disassembler.Reader.Position = (long) currentState.IP;
                    instruction = disassembler.ReadNextInstruction();
                    instruction.ProgramState = currentState;
                    currentState.Registers[VMRegisters.IP] = new SymbolicValue(instruction);
                    visited.Add((long) currentState.IP, instruction);
                }

                // Determine next states.
                foreach (var state in GetNextStates(currentState, instruction))
                    agenda.Push(state);
            }
        }

        private IList<ProgramState> GetNextStates(ProgramState currentState, ILInstruction instruction)
        {
            var nextStates = new List<ProgramState>(1);
            var next = currentState.Copy();
            next.IP += (ulong) instruction.Size;

            // Push/pop necessary values from stack.
            PopSymbolicValues(instruction, next);
            PushSymbolicValues(instruction, next);

            // Apply control flow.
            PerformFlowControl(instruction, nextStates, next);
            
            return nextStates;
        }

        private static void PopSymbolicValues(ILInstruction instruction, ProgramState next)
        {
            var operands = new List<SymbolicValue>(2);
            switch (instruction.OpCode.StackBehaviourPop)
            {
                case ILStackBehaviour.None:
                    break;
                
                case ILStackBehaviour.Pop1:
                    var value = next.Stack.Pop();
                    
                    // Check if instruction pops a value to a register.
                    if (instruction.OpCode.OperandType == ILOperandType.Register)
                        next.Registers[(VMRegisters) instruction.Operand] = value;
                    
                    operands.Add(value);
                    break;
                
                case ILStackBehaviour.Pop2:
                    operands.Add(next.Stack.Pop());
                    operands.Add(next.Stack.Pop());
                    break;
                
                case ILStackBehaviour.PopVar:
                    // TODO: support vcall.    
                    break;
                
                default:
                    throw new ArgumentOutOfRangeException();
            }

            // Add instruction dependencies for data flow graph, in reverse order to negate natural stack behaviour.
            for (int i = operands.Count - 1; i >= 0; i--) 
                instruction.Dependencies.Add(operands[i]);
        }

        private void PushSymbolicValues(ILInstruction instruction, ProgramState next)
        {
            switch (instruction.OpCode.StackBehaviourPush)
            {
                case ILStackBehaviour.None:
                    break;
                
                case ILStackBehaviour.Push1:
                    // If register is pushed, push the value in the register instead.
                    next.Stack.Push(instruction.OpCode.OperandType == ILOperandType.Register
                        ? next.Registers[(VMRegisters) instruction.Operand]
                        : new SymbolicValue(instruction));
                    break;
                
                case ILStackBehaviour.PushVar:
                    // TODO: support vcall.
                    break;
                
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private static void PerformFlowControl(ILInstruction instruction, List<ProgramState> nextStates, ProgramState next)
        {
            switch (instruction.OpCode.FlowControl)
            {
                case ILFlowControl.Next:
                {
                    // Normal flow.
                    nextStates.Add(next);
                    break;
                }
                case ILFlowControl.Jump:
                {
                    // Unconditional jump target.
                    ulong nextIpU8 = InferJumpTarget(instruction);
                    next.IP = nextIpU8;
                    nextStates.Add(next);
                    break;
                }
                case ILFlowControl.Call:
                case ILFlowControl.ConditionalJump:
                {
                    // Next to normal jump target, we need to consider that either condition was false,
                    // or we returned from a call. Both have virtually the same effect on the flow analysis.
                    
                    ulong nextIpU8 = InferJumpTarget(instruction);
                    var branch = next.Copy();
                    branch.IP = nextIpU8;
                    nextStates.Add(branch);
                    nextStates.Add(next);
                    break;
                }
                case ILFlowControl.Return:
                {
                    // Return, do nothing.
                    break;
                }
                default:
                {
                    throw new ArgumentOutOfRangeException();
                }
            }
        }

        private static ulong InferJumpTarget(ILInstruction instruction)
        {
            var emulator = EmulateDependentInstructions(instruction);
            
            // After partial emulation, IP is on stack. 
            var nextIp = emulator.Stack.Pop();
            return nextIp.U8;
        }

        private static InstructionEmulator EmulateDependentInstructions(ILInstruction instruction)
        {
            // TODO: Use data flow graph instead to determine order of instructions.
            var queue = instruction.GetAllDependencies()
                .OrderBy(x => x.Offset)
                .ToList();

            var emulator = new InstructionEmulator();
            foreach (var source in queue)
                emulator.EmulateInstruction(source);
            return emulator;
        }
    }
}