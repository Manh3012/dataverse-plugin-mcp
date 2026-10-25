using System;
using System.IO;
using System.Runtime.Serialization;
using System.Xml;
using Microsoft.Xrm.Sdk;
using Microsoft.Crm.Sdk.Messages;

[DataContract(Name = "PluginExecutionContext", Namespace = "")]
sealed class CapturedExecutionContext : IPluginExecutionContext4
{
    [DataMember] public int Mode { get; set; }
    [DataMember] public int IsolationMode { get; set; }
    [DataMember] public int Depth { get; set; }
    [DataMember] public int Stage { get; set; }
    [DataMember] public string MessageName { get; set; } = "";
    [DataMember] public string PrimaryEntityName { get; set; } = "";
    [DataMember] public Guid PrimaryEntityId { get; set; }
    [DataMember] public Guid? RequestId { get; set; }
    [DataMember] public string SecondaryEntityName { get; set; } = "";
    [DataMember] public ParameterCollection InputParameters { get; set; } = new();
    [DataMember] public ParameterCollection OutputParameters { get; set; } = new();
    [DataMember] public ParameterCollection SharedVariables { get; set; } = new();
    [DataMember] public EntityImageCollection PreEntityImages { get; set; } = new();
    [DataMember] public EntityImageCollection PostEntityImages { get; set; } = new();
    [DataMember] public Guid UserId { get; set; }
    [DataMember] public Guid InitiatingUserId { get; set; }
    [DataMember] public Guid BusinessUnitId { get; set; }
    [DataMember] public Guid OrganizationId { get; set; }
    [DataMember] public string? OrganizationName { get; set; }
    [DataMember] public EntityReference? OwningExtension { get; set; }
    [DataMember] public Guid CorrelationId { get; set; }
    [DataMember] public bool IsExecutingOffline { get; set; }
    [DataMember] public bool IsOfflinePlayback { get; set; }
    [DataMember] public bool IsInTransaction { get; set; }
    [DataMember] public Guid OperationId { get; set; }
    [DataMember] public DateTime OperationCreatedOn { get; set; }
    [DataMember] public Guid UserAzureActiveDirectoryObjectId { get; set; }
    [DataMember] public Guid InitiatingUserAzureActiveDirectoryObjectId { get; set; }
    [DataMember] public Guid AuthenticatedUserId { get; set; }
    [DataMember] public EntityImageCollection[]? PreEntityImagesCollection { get; set; }
    [DataMember] public EntityImageCollection[]? PostEntityImagesCollection { get; set; }
    [DataMember] public CapturedExecutionContext? ParentContext { get; set; }
    IPluginExecutionContext IPluginExecutionContext.ParentContext => ParentContext!;
    [DataMember] public Guid InitiatingUserApplicationId { get; set; }
    [DataMember] public Guid PortalsContactId { get; set; }
    [DataMember] public bool IsPortalsClientCall { get; set; }
}

// PRT trace-log captures are protobuf contexts. ParameterCollection values inside them are WCF binary XML.
static class ProfileContextDecoder
{
    public static CapturedExecutionContext Decode(string text)
    {
        var bytes = Convert.FromBase64String(text);
        var context = Decode(bytes, 0);
        if (string.IsNullOrWhiteSpace(context.MessageName) || context.Depth < 1 || context.Stage < 1)
            throw new ArgumentException("Capture is missing required MessageName, Depth or Stage context fields.");
        return context;
    }

    static CapturedExecutionContext Decode(byte[] bytes, int depth)
    {
        if (depth > 64) throw new ArgumentException("The captured parent context exceeds 64 levels.");
        var context = new CapturedExecutionContext();
        var offset = 0;
        while (offset < bytes.Length)
        {
            var tag = ReadVarint(bytes, ref offset);
            var field = tag >> 3;
            var wire = tag & 7;
            if (field == 0 || field > 536870911) throw new ArgumentException("Invalid protobuf field number.");
            if (field <= 35)
            {
                var scalar = field <= 4 || field == 22 || field == 23 || field == 26 || field == 32;
                if (wire != (scalar ? 0UL : 2UL)) throw new ArgumentException($"Invalid wire type for context field {field}.");
            }
            switch (wire)
            {
                case 0:
                    var number = ReadVarint(bytes, ref offset);
                    if (field == 1) context.Mode = checked((int)number);
                    else if (field == 2) context.IsInTransaction = number != 0;
                    else if (field == 3) context.IsOfflinePlayback = number != 0;
                    else if (field == 4) context.IsExecutingOffline = number != 0;
                    else if (field == 22) context.Depth = checked((int)number);
                    else if (field == 23) context.IsolationMode = (int)number;
                    else if (field == 26) context.Stage = (int)number;
                    else if (field == 32) context.IsPortalsClientCall = number != 0;
                    break;
                case 2:
                    var value = ReadBytes(bytes, ref offset);
                    switch (field)
                    {
                        case 5: context.CorrelationId = Guid.Parse(ReadString(value)); break;
                        case 6: context.OwningExtension = ReadContract<EntityReference>(value); break;
                        case 7: CopyImages(ReadContract<EntityImageCollection>(value), context.PostEntityImages); break;
                        case 8: CopyImages(ReadContract<EntityImageCollection>(value), context.PreEntityImages); break;
                        case 9: context.PrimaryEntityId = Guid.Parse(ReadString(value)); break;
                        case 10: context.OrganizationId = Guid.Parse(ReadString(value)); break;
                        case 11: context.OrganizationName = ReadString(value); break;
                        case 12: context.OperationId = Guid.Parse(ReadString(value)); break;
                        case 13: context.BusinessUnitId = Guid.Parse(ReadString(value)); break;
                        case 14: context.UserId = Guid.Parse(ReadString(value)); break;
                        case 15: Copy(ReadContract<ParameterCollection>(value), context.SharedVariables); break;
                        case 16: Copy(ReadContract<ParameterCollection>(value), context.OutputParameters); break;
                        case 17: Copy(ReadContract<ParameterCollection>(value), context.InputParameters); break;
                        case 18: context.SecondaryEntityName = ReadString(value); break;
                        case 19: context.RequestId = Guid.Parse(ReadString(value)); break;
                        case 20: context.PrimaryEntityName = ReadString(value); break;
                        case 21: context.MessageName = ReadString(value); break;
                        case 24: context.InitiatingUserId = Guid.Parse(ReadString(value)); break;
                        case 25: context.OperationCreatedOn = DateTime.Parse(ReadString(value), null, System.Globalization.DateTimeStyles.RoundtripKind); break;
                        case 27: context.ParentContext = Decode(value, depth + 1); break;
                        case 28: context.UserAzureActiveDirectoryObjectId = Guid.Parse(ReadString(value)); break;
                        case 29: context.InitiatingUserAzureActiveDirectoryObjectId = Guid.Parse(ReadString(value)); break;
                        case 30: context.InitiatingUserApplicationId = Guid.Parse(ReadString(value)); break;
                        case 31: context.PortalsContactId = Guid.Parse(ReadString(value)); break;
                        case 33: context.AuthenticatedUserId = Guid.Parse(ReadString(value)); break;
                        case 34: context.PreEntityImagesCollection = ReadContract<EntityImageCollection[]>(value); break;
                        case 35: context.PostEntityImagesCollection = ReadContract<EntityImageCollection[]>(value); break;
                    }
                    break;
                case 1: Skip(bytes, ref offset, 8); break;
                case 5: Skip(bytes, ref offset, 4); break;
                default: throw new ArgumentException($"Unsupported protobuf wire type {wire}.");
            }
        }
        return context;
    }

    static T ReadContract<T>(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = XmlDictionaryReader.CreateBinaryReader(stream, XmlDictionaryReaderQuotas.Max);
        return (T)new DataContractSerializer(typeof(T), new DataContractSerializerSettings
        {
            KnownTypes = new[]
            {
                typeof(Entity), typeof(EntityReference), typeof(Money), typeof(OptionSetValue), typeof(OptionSetValueCollection),
                typeof(EntityCollection), typeof(AliasedValue), typeof(BooleanManagedProperty),
                typeof(PrincipalAccess), typeof(PrincipalAccess[]), typeof(AccessRights)
            },
            DataContractResolver = new CaptureContractResolver()
        }).ReadObject(reader)!;
    }

    static void CopyImages(EntityImageCollection source, EntityImageCollection destination)
    {
        foreach (var item in source) destination[item.Key] = item.Value;
    }

    static void Copy(ParameterCollection source, ParameterCollection destination)
    {
        foreach (var item in source) destination[item.Key] = item.Value;
    }

    static string ReadString(byte[] value) => System.Text.Encoding.UTF8.GetString(value);
    static byte[] ReadBytes(byte[] bytes, ref int offset)
    {
        var length = ReadVarint(bytes, ref offset);
        if (length > int.MaxValue) throw new ArgumentException("Protobuf field is oversized.");
        if (length > (ulong)(bytes.Length - offset)) throw new ArgumentException("Truncated protobuf field.");
        var result = new byte[(int)length];
        Skip(bytes, ref offset, result.Length);
        Buffer.BlockCopy(bytes, offset - result.Length, result, 0, result.Length);
        return result;
    }
    static ulong ReadVarint(byte[] bytes, ref int offset)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (offset == bytes.Length) throw new ArgumentException("Truncated protobuf value.");
            var b = bytes[offset++];
            if (shift == 63 && b > 1) throw new ArgumentException("Invalid protobuf varint.");
            value |= (ulong)(b & 127) << shift;
            if ((b & 128) == 0) return value;
        }
        throw new ArgumentException("Invalid protobuf varint.");
    }
    static void Skip(byte[] bytes, ref int offset, int length)
    {
        if (length < 0 || length > bytes.Length - offset) throw new ArgumentException("Truncated protobuf field.");
        offset += length;
    }
}

// RetrievePrincipalAccess captures use the legacy enum namespace, although SDK proxy types use a newer namespace.
sealed class CaptureContractResolver : DataContractResolver
{
    public override Type ResolveName(string name, string ns, Type declaredType, DataContractResolver knownTypeResolver) =>
        name == "AccessRights" && ns == "Microsoft.Crm.Sdk" ? typeof(AccessRights) : knownTypeResolver.ResolveName(name, ns, declaredType, null!);

    public override bool TryResolveType(Type type, Type declaredType, DataContractResolver knownTypeResolver, out XmlDictionaryString typeName, out XmlDictionaryString typeNamespace) =>
        knownTypeResolver.TryResolveType(type, declaredType, null!, out typeName, out typeNamespace);
}
