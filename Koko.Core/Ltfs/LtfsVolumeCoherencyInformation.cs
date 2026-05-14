using System.Buffers.Binary;
using System.Text;

using Koko.Core.Scsi.Commands;

namespace Koko.Core.Ltfs;

public sealed record LtfsVolumeCoherencyInformation(
    ulong Generation,
    ulong IndexBlock,
    Guid VolumeUuid)
{
    public const ushort MamAttributeId = 0x080C;

    public MamAttribute ToMamAttribute()
    {
        return new MamAttribute(MamAttributeId, MamAttributeFormat.Binary, BuildPayload());
    }

    public byte[] BuildPayload()
    {
        var payload = new byte[70];
        payload[0] = 8;
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(9, 8), Generation);
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(17, 8), IndexBlock);
        payload[26] = (byte)'+';
        payload[27] = (byte)'L';
        payload[28] = (byte)'T';
        payload[29] = (byte)'F';
        payload[30] = (byte)'S';
        Encoding.ASCII.GetBytes(VolumeUuid.ToString("D").PadRight(36), payload.AsSpan(32, 36));
        payload[69] = 1;
        return payload;
    }

    public static bool TryParse(ReadOnlySpan<byte> payload, out LtfsVolumeCoherencyInformation vci)
    {
        vci = default!;
        if (payload.Length < 70)
            return false;

        if (payload[26] != (byte)'+' || payload[27] != (byte)'L' || payload[28] != (byte)'T' || payload[29] != (byte)'F' || payload[30] != (byte)'S')
            return false;

        var uuidText = Encoding.ASCII.GetString(payload.Slice(32, 36)).Trim();
        if (!Guid.TryParse(uuidText, out var uuid))
            return false;

        vci = new LtfsVolumeCoherencyInformation(
            BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(9, 8)),
            BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(17, 8)),
            uuid);
        return true;
    }
}
