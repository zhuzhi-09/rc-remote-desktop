using System.Buffers.Binary;

namespace Rc.Protocol;

/// <summary>One JPEG-encoded tile of the remote screen, addressed by grid coordinates.</summary>
public sealed class Tile
{
    public int GridX { get; init; }
    public int GridY { get; init; }

    /// <summary>Pixel width of the encoded tile (may be smaller at the right edge).</summary>
    public int Width { get; init; }

    /// <summary>Pixel height of the encoded tile (may be smaller at the bottom edge).</summary>
    public int Height { get; init; }

    public byte[] Jpeg { get; init; } = [];
}

/// <summary>A batch of changed tiles belonging to a single captured frame.</summary>
public sealed class FramePacket
{
    /// <summary>Full logical screen width in pixels (after scaling).</summary>
    public int FrameWidth { get; set; }

    /// <summary>Full logical screen height in pixels (after scaling).</summary>
    public int FrameHeight { get; set; }

    public int TileSize { get; set; } = ProtocolInfo.DefaultTileSize;

    public uint FrameId { get; set; }

    /// <summary>True when every tile is present (a full redraw / keyframe).</summary>
    public bool Keyframe { get; set; }

    public List<Tile> Tiles { get; set; } = [];
}

/// <summary>
/// Compact little-endian binary layout for frame messages.
/// <code>
/// int32  frameWidth
/// int32  frameHeight
/// int32  tileSize
/// uint32 frameId
/// byte   keyframe
/// int32  tileCount
/// tile[tileCount]:
///   int32 gridX, int32 gridY, int32 width, int32 height, int32 jpegLen, byte[] jpeg
/// </code>
/// </summary>
public static class FrameCodec
{
    public const int HeaderSize = 4 + 4 + 4 + 4 + 1 + 4;
    private const int TileHeaderSize = 4 + 4 + 4 + 4 + 4;

    public static byte[] Encode(FramePacket packet)
    {
        var size = HeaderSize + packet.Tiles.Count * TileHeaderSize;
        foreach (var tile in packet.Tiles)
        {
            size += tile.Jpeg.Length;
        }

        var buffer = new byte[size];
        var span = buffer.AsSpan();
        var offset = 0;

        WriteInt32(span, ref offset, packet.FrameWidth);
        WriteInt32(span, ref offset, packet.FrameHeight);
        WriteInt32(span, ref offset, packet.TileSize);
        WriteUInt32(span, ref offset, packet.FrameId);
        span[offset++] = packet.Keyframe ? (byte)1 : (byte)0;
        WriteInt32(span, ref offset, packet.Tiles.Count);

        foreach (var tile in packet.Tiles)
        {
            WriteInt32(span, ref offset, tile.GridX);
            WriteInt32(span, ref offset, tile.GridY);
            WriteInt32(span, ref offset, tile.Width);
            WriteInt32(span, ref offset, tile.Height);
            WriteInt32(span, ref offset, tile.Jpeg.Length);
            tile.Jpeg.CopyTo(span[offset..]);
            offset += tile.Jpeg.Length;
        }

        return buffer;
    }

    public static FramePacket Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeaderSize)
        {
            throw new InvalidDataException("Frame payload is shorter than its header.");
        }

        var offset = 0;
        var packet = new FramePacket
        {
            FrameWidth = ReadInt32(payload, ref offset),
            FrameHeight = ReadInt32(payload, ref offset),
            TileSize = ReadInt32(payload, ref offset),
            FrameId = ReadUInt32(payload, ref offset),
            Keyframe = payload[offset++] != 0,
        };

        var tileCount = ReadInt32(payload, ref offset);
        if (tileCount < 0 || tileCount > 1_000_000)
        {
            throw new InvalidDataException($"Invalid tile count {tileCount}.");
        }

        var tiles = new List<Tile>(tileCount);
        for (var i = 0; i < tileCount; i++)
        {
            if (payload.Length - offset < TileHeaderSize)
            {
                throw new InvalidDataException("Truncated tile header.");
            }

            var gridX = ReadInt32(payload, ref offset);
            var gridY = ReadInt32(payload, ref offset);
            var width = ReadInt32(payload, ref offset);
            var height = ReadInt32(payload, ref offset);
            var jpegLen = ReadInt32(payload, ref offset);

            if (jpegLen < 0 || payload.Length - offset < jpegLen)
            {
                throw new InvalidDataException("Truncated tile payload.");
            }

            tiles.Add(new Tile
            {
                GridX = gridX,
                GridY = gridY,
                Width = width,
                Height = height,
                Jpeg = payload.Slice(offset, jpegLen).ToArray(),
            });
            offset += jpegLen;
        }

        packet.Tiles = tiles;
        return packet;
    }

    private static void WriteInt32(Span<byte> span, ref int offset, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], value);
        offset += 4;
    }

    private static void WriteUInt32(Span<byte> span, ref int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], value);
        offset += 4;
    }

    private static int ReadInt32(ReadOnlySpan<byte> span, ref int offset)
    {
        var value = BinaryPrimitives.ReadInt32LittleEndian(span[offset..]);
        offset += 4;
        return value;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> span, ref int offset)
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(span[offset..]);
        offset += 4;
        return value;
    }
}
