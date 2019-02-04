using System;
using System.Collections.Generic;
using AsmResolver.Net.Cts;
using OldRod.Core.Architecture;
using OldRod.Core.Disassembly.Inference;

namespace OldRod.Core.Ast
{
    public class ILAstBuilder
    {
        private const string Tag = "ILAstBuilder";
        
        private readonly MetadataImage _image;

        public ILAstBuilder(MetadataImage image)
        {
            _image = image ?? throw new ArgumentNullException(nameof(image));
        }
        
        public ILogger Logger
        {
            get;
            set;
        } = EmptyLogger.Instance;
        
        public ILCompilationUnit BuildAst(IDictionary<long, ILInstruction> instructions, long startOffset)
        {
            var result = new ILCompilationUnit();

            // Introduce variables:
            Logger.Debug(Tag, "Determining variables...");
            for (int i = 0; i < (int) VMRegisters.Max; i++)
            {
                var registerVar = result.GetOrCreateVariable(((VMRegisters) i).ToString());
                registerVar.VariableType = VMType.Object;
            }
            var resultVariables = IntroduceResultVariables(result, instructions.Values);
            
            Logger.Debug(Tag, "Building AST...");
            var agenda = new Stack<long>();
            agenda.Push(startOffset);
            
            while (agenda.Count > 0)
            {
                // Build expression.
                var instruction = instructions[agenda.Pop()];
                var expression = BuildExpression(instruction, result);

                // Add statement to result.
                if (resultVariables.TryGetValue(instruction.Offset, out var resultVariable))
                    result.Statements.Add(new ILAssignmentStatement(resultVariable, expression));
                else
                    result.Statements.Add(new ILExpressionStatement(expression));

                // Go to next instruction.
                switch (instruction.OpCode.FlowControl)
                {
                    case ILFlowControl.Next:
                        agenda.Push(instruction.Offset + instruction.Size);
                        break;
                    case ILFlowControl.Jump:
                        foreach (var target in ((JumpMetadata) instruction.InferredMetadata).InferredJumpTargets)
                            agenda.Push((long) target);
                        break;
                    case ILFlowControl.Call:
                    case ILFlowControl.ConditionalJump:
                        foreach (var target in ((JumpMetadata) instruction.InferredMetadata).InferredJumpTargets)
                            agenda.Push((long) target);
                        agenda.Push(instruction.Offset + instruction.Size);
                        break;
                    case ILFlowControl.Return:
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            return result;
        }

        private IDictionary<int, ILVariable> IntroduceResultVariables(ILCompilationUnit result, IEnumerable<ILInstruction> instructions)
        {
            // Determine result variables based on where the value is used by other instructions.
            // Find for each instruction the dependent instructions and assign to each of those dependent instructions
            // the same variable.
            
            var resultVariables = new Dictionary<int, ILVariable>();
            foreach (var instruction in instructions)
            {
                for (int i = 0; i < instruction.Dependencies.Count; i++)
                {
                    var dep = instruction.Dependencies[i];
                    var resultVar = result.GetOrCreateVariable(GetOperandVariableName(instruction, i));
                    resultVar.VariableType = dep.Type;
                    foreach (var source in dep.DataSources)
                        resultVariables[source.Offset] = resultVar;
                }
            }

            return resultVariables;
        }

        private static ILExpression BuildExpression(ILInstruction instruction, ILCompilationUnit result)
        {
            var expression = instruction.OpCode.Code == ILCode.VCALL
                ? (IArgumentsProvider) new ILVCallExpression((VCallMetadata) instruction.InferredMetadata)
                : new ILInstructionExpression(instruction);

            for (int i = 0; i < instruction.Dependencies.Count; i++)
            {
                var argument = new ILVariableExpression(
                    result.GetOrCreateVariable(GetOperandVariableName(instruction, i)));
                argument.Variable.UsedBy.Add(argument);
                expression.Arguments.Add(argument);
            }

            return (ILExpression) expression;
        }

        private static string GetOperandVariableName(ILInstruction instruction, int operandIndex)
        {
            return $"operand_{instruction.Offset:X}_{operandIndex}";
        }
    }
}