using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;

namespace DotNetVisualizer.Core;

/// <summary>Reads IL instruction by instruction, with the metadata token of the operand when there is one.</summary>
public static class IlReader
{
    private static readonly Dictionary<short, OpCode> IlOpCodes = CreateIlOpCodeMap();

    public static bool TryReadInstruction(ref BlobReader reader, out OpCode opCode, out int metadataToken)
    {
        opCode = default;
        metadataToken = 0;

        if (reader.RemainingBytes == 0)
            return false;

        short opcodeValue = reader.ReadByte();

        // Handle two-byte IL opcodes (0xFE xx)
        if (opcodeValue == 0xFE)
        {
            if (reader.RemainingBytes == 0)
                return false;

            opcodeValue = (short)(0xFE00 | reader.ReadByte());
        }

        if (!IlOpCodes.TryGetValue(opcodeValue, out opCode))
            return false;

        // Handle switch
        if (opCode.OperandType == OperandType.InlineSwitch)
        {
            if (reader.RemainingBytes < 4)
                return false;

            int targetCount = reader.ReadInt32();

            if (targetCount < 0)
                return false;

            int targetBytes = checked(targetCount * 4);

            if (reader.RemainingBytes < targetBytes)
                return false;

            reader.Offset += targetBytes;
            metadataToken = -targetCount;                       // not a token: the number of switch cases (used for complexity)

            return true;
        }

        // Handle regular operand
        int operandSize = GetOperandSize(opCode.OperandType);

        if (reader.RemainingBytes < operandSize)
            return false;

        // If this instruction has a metadata token, preserve it.
        if (operandSize == 4 && IsMetadataTokenOperand(opCode.OperandType))
            metadataToken = reader.ReadInt32();
        else
            reader.Offset += operandSize;

        return true;
    }

    private static int GetOperandSize(OperandType operandType)
    {
        return operandType switch
        {
            OperandType.InlineBrTarget or
            OperandType.InlineField or
            OperandType.InlineI or
            OperandType.InlineMethod or
            OperandType.InlineSig or
            OperandType.InlineString or
            OperandType.InlineTok or
            OperandType.InlineType or
            OperandType.ShortInlineR
                => 4,

            OperandType.InlineI8 or
            OperandType.InlineR
                => 8,

            OperandType.InlineVar
                => 2,

            OperandType.ShortInlineBrTarget or
            OperandType.ShortInlineI or
            OperandType.ShortInlineVar
                => 1,

            _ => 0
        };
    }

    private static bool IsMetadataTokenOperand(OperandType operandType)
    {
        return operandType is
            OperandType.InlineField or
            OperandType.InlineMethod or
            OperandType.InlineSig or
            OperandType.InlineString or
            OperandType.InlineTok or
            OperandType.InlineType;
    }

    private static Dictionary<short, OpCode> CreateIlOpCodeMap()
    {
        var opCodes = new Dictionary<short, OpCode>();

        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType == typeof(OpCode))
            {
                OpCode opCode = (OpCode)field.GetValue(null)!;
                opCodes[opCode.Value] = opCode;
            }
        }

        return opCodes;
    }
}
