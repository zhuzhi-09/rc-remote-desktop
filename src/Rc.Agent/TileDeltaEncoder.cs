using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Rc.Protocol;

namespace Rc.Agent;

/// <summary>
/// Turns successive frames into a <see cref="FramePacket"/> containing only the tiles whose pixels
/// changed. Owns the previous frame (raw BGRA) so it can diff cheaply, and reuses scratch bitmaps,
/// the JPEG codec and its encoder parameters to avoid per-tile allocations.
/// </summary>
public sealed class TileDeltaEncoder : IDisposable
{
    private readonly int _tileSize;
    private readonly ImageCodecInfo _jpegCodec;
    private readonly EncoderParameters _jpegParams;
    private readonly Dictionary<(int Width, int Height), Bitmap> _scratch = [];

    private volatile int _quality;
    private long _paramsQuality = -1;

    private byte[]? _prev;
    private byte[]? _current;
    private int _prevWidth;
    private int _prevHeight;

    public TileDeltaEncoder(int tileSize, int quality)
    {
        _tileSize = Math.Max(1, tileSize);
        _quality = Math.Clamp(quality, 1, 100);
        _jpegCodec = FindJpegCodec();
        _jpegParams = new EncoderParameters(1);
        UpdateParameters();
    }

    public int TileSize => _tileSize;

    /// <summary>JPEG quality (1..100). Safe to set from another thread.</summary>
    public int Quality
    {
        get => _quality;
        set => _quality = Math.Clamp(value, 1, 100);
    }

    /// <summary>
    /// Diffs <paramref name="frame"/> against the previous frame and JPEG-encodes the changed tiles.
    /// A forced keyframe encodes every tile. When nothing changed the packet has zero tiles and
    /// <see cref="FramePacket.Keyframe"/> is false, and the caller should not send it.
    /// </summary>
    public FramePacket Encode(Bitmap frame, bool forceKeyframe, uint frameId)
    {
        var width = frame.Width;
        var height = frame.Height;

        var data = frame.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppRgb);
        byte[] current;
        int stride;
        try
        {
            stride = data.Stride;
            var length = stride * height;
            if (_current is null || _current.Length < length)
            {
                _current = new byte[length];
            }
            Marshal.Copy(data.Scan0, _current, 0, length);
            current = _current;
        }
        finally
        {
            frame.UnlockBits(data);
        }

        var sameDimensions = _prev is not null && _prevWidth == width && _prevHeight == height;
        if (!sameDimensions)
        {
            forceKeyframe = true;
        }

        if (!forceKeyframe && sameDimensions &&
            _prev!.AsSpan(0, stride * height).SequenceEqual(current.AsSpan(0, stride * height)))
        {
            // Byte-identical to the previous frame: recycle the buffer, report nothing changed.
            (_prev, _current) = (current, _prev);
            return new FramePacket
            {
                FrameWidth = width,
                FrameHeight = height,
                TileSize = _tileSize,
                FrameId = frameId,
                Keyframe = false,
                Tiles = [],
            };
        }

        UpdateParameters();

        var tilesX = (width + _tileSize - 1) / _tileSize;
        var tilesY = (height + _tileSize - 1) / _tileSize;
        var tiles = new List<Tile>();

        for (var gridY = 0; gridY < tilesY; gridY++)
        {
            var tileHeight = Math.Min(_tileSize, height - gridY * _tileSize);
            for (var gridX = 0; gridX < tilesX; gridX++)
            {
                var tileWidth = Math.Min(_tileSize, width - gridX * _tileSize);
                var x = gridX * _tileSize;
                var y = gridY * _tileSize;

                var changed = forceKeyframe
                    || !sameDimensions
                    || RegionChanged(_prev!, current, stride, x, y, tileWidth, tileHeight);
                if (!changed)
                {
                    continue;
                }

                tiles.Add(new Tile
                {
                    GridX = gridX,
                    GridY = gridY,
                    Width = tileWidth,
                    Height = tileHeight,
                    Jpeg = EncodeRegion(current, stride, x, y, tileWidth, tileHeight),
                });
            }
        }

        (_prev, _current) = (current, _prev);
        _prevWidth = width;
        _prevHeight = height;

        return new FramePacket
        {
            FrameWidth = width,
            FrameHeight = height,
            TileSize = _tileSize,
            FrameId = frameId,
            Keyframe = forceKeyframe,
            Tiles = tiles,
        };
    }

    private static bool RegionChanged(byte[] previous, byte[] current, int stride, int x, int y, int width, int height)
    {
        var bytes = width * 4;
        for (var row = 0; row < height; row++)
        {
            var offset = (y + row) * stride + x * 4;
            if (!previous.AsSpan(offset, bytes).SequenceEqual(current.AsSpan(offset, bytes)))
            {
                return true;
            }
        }
        return false;
    }

    private byte[] EncodeRegion(byte[] source, int stride, int x, int y, int width, int height)
    {
        var scratch = GetScratch(width, height);
        var data = scratch.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppRgb);
        try
        {
            var bytes = width * 4;
            for (var row = 0; row < height; row++)
            {
                var sourceOffset = (y + row) * stride + x * 4;
                Marshal.Copy(source, sourceOffset, IntPtr.Add(data.Scan0, row * data.Stride), bytes);
            }
        }
        finally
        {
            scratch.UnlockBits(data);
        }

        using var stream = new MemoryStream();
        scratch.Save(stream, _jpegCodec, _jpegParams);
        return stream.ToArray();
    }

    private Bitmap GetScratch(int width, int height)
    {
        if (_scratch.TryGetValue((width, height), out var bitmap))
        {
            return bitmap;
        }

        if (_scratch.Count > 16)
        {
            // A long run of scale changes should not accumulate bitmaps forever.
            foreach (var existing in _scratch.Values)
            {
                existing.Dispose();
            }
            _scratch.Clear();
        }

        bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        _scratch[(width, height)] = bitmap;
        return bitmap;
    }

    private void UpdateParameters()
    {
        var quality = _quality;
        if (_paramsQuality == quality)
        {
            return;
        }
        _jpegParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
        _paramsQuality = quality;
    }

    private static ImageCodecInfo FindJpegCodec()
    {
        foreach (var codec in ImageCodecInfo.GetImageEncoders())
        {
            if (codec.FormatID == ImageFormat.Jpeg.Guid)
            {
                return codec;
            }
        }
        throw new InvalidOperationException("No JPEG encoder is available.");
    }

    public void Dispose()
    {
        foreach (var bitmap in _scratch.Values)
        {
            bitmap.Dispose();
        }
        _scratch.Clear();
        _jpegParams.Dispose();
        _prev = null;
        _current = null;
    }
}
