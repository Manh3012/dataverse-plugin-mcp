using ModelContextProtocol;

// This verifies the protobuf wire format only. Field meanings remain unknown until a schema is established.
static class ProtobufCaptureVerifier
{
    public static void Verify(string context)
    {
        if (string.IsNullOrWhiteSpace(context)) throw new McpException("Not a trace-log profile: the capture is empty.");

        byte[] bytes;
        try { bytes = Convert.FromBase64String(context); }
        catch (FormatException) { throw new McpException("Not a trace-log profile: the capture is not valid Base64."); }
        if (bytes.Length == 0) throw new McpException("Not a trace-log profile: the capture is empty.");

        var offset = 0;
        while (offset < bytes.Length)
        {
            var tag = ReadVarint(bytes, ref offset);
            var fieldNumber = tag >> 3;
            var wireType = tag & 7;
            if (fieldNumber == 0 || fieldNumber > 536870911) throw new McpException("Not a trace-log profile: invalid protobuf field number.");

            switch (wireType)
            {
                case 0: ReadVarint(bytes, ref offset); break;
                case 1: Skip(bytes, ref offset, 8); break;
                case 2:
                    var length = ReadVarint(bytes, ref offset);
                    if (length > int.MaxValue) throw new McpException("Not a trace-log profile: oversized protobuf field.");
                    Skip(bytes, ref offset, (int)length);
                    break;
                case 5: Skip(bytes, ref offset, 4); break;
                default: throw new McpException($"Not a trace-log profile: unsupported protobuf wire type {wireType}.");
            }
        }
    }

    static ulong ReadVarint(byte[] bytes, ref int offset)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (offset == bytes.Length) throw new McpException("Not a trace-log profile: truncated protobuf value.");
            var b = bytes[offset++];
            if (shift == 63 && b > 1) throw new McpException("Not a trace-log profile: invalid protobuf varint.");
            value |= (ulong)(b & 127) << shift;
            if ((b & 128) == 0) return value;
        }
        throw new McpException("Not a trace-log profile: invalid protobuf varint.");
    }

    static void Skip(byte[] bytes, ref int offset, int length)
    {
        if (length < 0 || length > bytes.Length - offset) throw new McpException("Not a trace-log profile: truncated protobuf field.");
        offset += length;
    }
}
