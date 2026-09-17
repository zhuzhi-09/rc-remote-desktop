using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Rc.Protocol;

namespace Rc.Agent;

/// <summary>
/// Turns successive frames into a <see cref="FramePacket"/> containing only the areas whose pixels
/// changed. Dirty detection stays at the configured tile size so small edits are still noticed, but
/// adjacent dirty tiles are coalesced into larger rectangles (horizontal runs, then vertical strips)
/// before encoding. That removes the grid of 64x64 seams and lets the codec work on real picture
/// content. Quality 100 selects lossless PNG instead of JPEG for pixel-perfect text.
/// </summary>
public sealed class TileDeltaEncoder : IDisposable
{
    /// <summary>Pixel cap on a single coalesced region's width, to bound one client-side decode.</summary>
    private const int MaxRegionWidth = 1024;

    private readonly int _tileSize;
    private readonly ImageCodecInfo _jpegCodec;
    private readonly ImageCodecInfo _pngCodec;
    private readonly EncoderParameters _jpegParams;
    private readonly Dictionary<(int Width, int Height), Bitmap> _scratch = [];
    private readonly List<Region> _regions = [];

    private volatile int _quality;
    private long _paramsQuality = -1;

    private byte[]? _prev;
    private byte[]? _current;
    private int _prevWidth;
    private int _prevHeight;
    private bool[] _dirty = [];

    public TileDeltaEncoder(int tileSize, int quality)
    {
        _tileSize = Math.Max(1, tileSize);
        _quality = Math.Clamp(quality, 1, 100);
        _jpegCodec = FindCodec(ImageFormat.Jpeg, "JPEG");
        _pngCodec = FindCodec(ImageFormat.Png, "PNG");
        _jpegParams = new EncoderParameters(1);
        UpdateParameters();
    }

    public int TileSize => _tileSize;

    /// <summary>Encoding quality (1..100). 100 means lossless PNG, not "JPEG 100". Safe cross-thread.</summary>
    public int Quality
    {
        get => _quality;
        set => _quality = Math.Clamp(value, 1, 100);
    }

    /// <summary>True when regions are encoded as lossless PNG instead of JPEG.</summary>
    private bool IsLossless => _quality >= 100;

    /// <summary>
    /// Diffs <paramref name="frame"/> against the previous frame and encodes the changed regions.
    /// A forced keyframe encodes every region. When nothing changed the packet has zero tiles and
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

        MarkDirty(current, stride, width, height, forceKeyframe, sameDimensions, tilesX, tilesY);
        var tiles = BuildTiles(current, stride, width, height, tilesX, tilesY);

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

    /// <summary>
    /// Fills the reused dirty grid at tile granularity. A keyframe (or a size change) marks every
    /// cell; otherwise each tile is byte-compared against the previous frame.
    /// </summary>
    private void MarkDirty(byte[] current, int stride, int width, int height,
        bool forceKeyframe, bool sameDimensions, int tilesX, int tilesY)
    {
        var count = tilesX * tilesY;
        if (_dirty.Length < count)
        {
            _dirty = new bool[count];
        }
        var dirty = _dirty;
        Array.Clear(dirty, 0, count);

        if (forceKeyframe || !sameDimensions)
        {
            Array.Fill(dirty, true, 0, count);
            return;
        }

        for (var gridY = 0; gridY < tilesY; gridY++)
        {
            var tileHeight = Math.Min(_tileSize, height - gridY * _tileSize);
            var rowBase = gridY * tilesX;
            for (var gridX = 0; gridX < tilesX; gridX++)
            {
                var tileWidth = Math.Min(_tileSize, width - gridX * _tileSize);
                var x = gridX * _tileSize;
                var y = gridY * _tileSize;
                if (RegionChanged(_prev!, current, stride, x, y, tileWidth, tileHeight))
                {
                    dirty[rowBase + gridX] = true;
                }
            }
        }
    }

    /// <summary>
    /// Coalesces the dirty grid into rectangles and encodes each one. Step 1 merges horizontally
    /// adjacent dirty tiles into runs (capped at <see cref="MaxRegionWidth"/>); step 2 extends a run
    /// downward while the next row has a run with the exact same x-range. A full keyframe therefore
    /// collapses into a handful of full-height strips instead of one region per tile.
    /// </summary>
    private List<Tile> BuildTiles(byte[] current, int stride, int width, int height, int tilesX, int tilesY)
    {
        var dirty = _dirty;
        var regions = _regions;
        regions.Clear();

        var open = new Dictionary<int, Region>();
        for (var gridY = 0; gridY < tilesY; gridY++)
        {
            var rowBase = gridY * tilesX;
            var next = new Dictionary<int, Region>(open.Count);
            var gridX = 0;
            while (gridX < tilesX)
            {
                if (!dirty[rowBase + gridX])
                {
                    gridX++;
                    continue;
                }

                var start = gridX;
                var end = gridX;
                while (end + 1 < tilesX && dirty[rowBase + end + 1] &&
                       (end + 2 - start) * _tileSize <= MaxRegionWidth)
                {
                    end++;
                }

                if (open.TryGetValue(start, out var region) && region.EndX == end)
                {
                    region.EndY = gridY;
                }
                else
                {
                    region = new Region(start, end, gridY);
                    regions.Add(region);
                }
                next[start] = region;

                gridX = end + 1;
            }
            open = next;
        }

        var tiles = new List<Tile>(regions.Count);
        foreach (var region in regions)
        {
            var x = region.StartX * _tileSize;
            var y = region.StartY * _tileSize;
            var regionWidth = Math.Min((region.EndX + 1) * _tileSize, width) - x;
            var regionHeight = Math.Min((region.EndY + 1) * _tileSize, height) - y;
            if (regionWidth <= 0 || regionHeight <= 0)
            {
                continue;
            }

            tiles.Add(new Tile
            {
                GridX = region.StartX,
                GridY = region.StartY,
                Width = regionWidth,
                Height = regionHeight,
                Jpeg = EncodeRegion(current, stride, x, y, regionWidth, regionHeight),
            });
        }
        return tiles;
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
        if (IsLossless)
        {
            scratch.Save(stream, _pngCodec, null);
        }
        else
        {
            scratch.Save(stream, _jpegCodec, _jpegParams);
        }
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

    private static ImageCodecInfo FindCodec(ImageFormat format, string name)
    {
        foreach (var codec in ImageCodecInfo.GetImageEncoders())
        {
            if (codec.FormatID == format.Guid)
            {
                return codec;
            }
        }
        throw new InvalidOperationException($"No {name} encoder is available.");
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

    /// <summary>A coalesced rectangle of dirty tiles, inclusive of both corners in tile coordinates.</summary>
    private sealed class Region(int startX, int endX, int startY)
    {
        public int StartX { get; } = startX;
        public int EndX { get; } = endX;
        public int StartY { get; } = startY;
        public int EndY { get; set; } = startY;
    }
}
