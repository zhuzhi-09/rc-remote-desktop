using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;

namespace Rc.Agent;

/// <summary>
/// One decoded animated GIF together with the stream it was decoded from. GDI+ reads GIF frames
/// lazily from the source stream, so the stream must stay alive for as long as the image is shown;
/// a copy such as <c>new Bitmap(image)</c> would flatten the animation. Keep this object alive while
/// its <see cref="Image"/> is displayed and dispose it together with that image.
/// </summary>
internal sealed class AnimatedGif : IDisposable
{
    private readonly MemoryStream _stream;
    private bool _disposed;

    private AnimatedGif(Image image, MemoryStream stream)
    {
        Image = image;
        _stream = stream;
    }

    /// <summary>The decoded image; only valid while this instance is alive and not disposed.</summary>
    public Image Image { get; }

    /// <summary>
    /// Decodes the embedded GIF stored under <paramref name="resourceName"/>. Returns null (never
    /// throws) when the resource is missing or cannot be decoded.
    /// </summary>
    public static AnimatedGif? FromResource(string resourceName, string fileName)
    {
        MemoryStream? stream = null;
        try
        {
            stream = StatusPig.OpenResource(resourceName, fileName);
            if (stream is null)
            {
                Log.Warn($"Embedded status image '{resourceName}' was not found.");
                return null;
            }

            var image = new Bitmap(stream);
            var gif = new AnimatedGif(image, stream);
            stream = null; // Ownership moves to the AnimatedGif so the stream outlives the image.
            return gif;
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to load embedded status image '{resourceName}': {ex.Message}");
            return null;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    /// <summary>Releases the image and the stream that backs it. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Image.Dispose();
        _stream.Dispose();
    }
}

/// <summary>
/// Status images shared by the agent window and the tray menu: a walking pig while the relay
/// connection is up and a squashed pig otherwise. Both GIFs are embedded in the assembly so the
/// published single-file executable needs no loose asset files.
/// </summary>
internal static class StatusPig
{
    internal const string OkFileName = "pig-ok.gif";
    internal const string FailFileName = "pig-fail.gif";
    internal const string OkResourceName = "Rc.Agent.Assets." + OkFileName;
    internal const string FailResourceName = "Rc.Agent.Assets." + FailFileName;

    private static readonly object Gate = new();
    private static Image? _staticOk;
    private static Image? _staticFail;

    /// <summary>Manifest resource name of the GIF for the requested state.</summary>
    public static string ResourceName(bool ok) => ok ? OkResourceName : FailResourceName;

    /// <summary>File name of the GIF for the requested state (used by the self-check output).</summary>
    public static string FileName(bool ok) => ok ? OkFileName : FailFileName;

    /// <summary>
    /// First frame only, for the tray menu. Decoded once and cached for the process lifetime, so
    /// every state change reuses the same instance. Returns null when the asset cannot be loaded.
    /// </summary>
    public static Image? LoadStatic(bool ok)
    {
        lock (Gate)
        {
            if (ok)
            {
                return _staticOk ??= LoadStaticImage(OkResourceName, OkFileName);
            }

            return _staticFail ??= LoadStaticImage(FailResourceName, FailFileName);
        }
    }

    /// <summary>
    /// A fresh animated instance per call; the caller owns it, must keep it alive while the image is
    /// displayed, and disposes it when the image is replaced. Returns null when the asset cannot be
    /// loaded.
    /// </summary>
    public static AnimatedGif? LoadAnimated(bool ok) => AnimatedGif.FromResource(ResourceName(ok), FileName(ok));

    /// <summary>
    /// Dimensions and frame count of both embedded GIFs, for the headless <c>--pig-check</c>
    /// self-check. An entry whose image failed to load reports zeroes instead of throwing.
    /// </summary>
    public static (string Name, int Width, int Height, int Frames)[] Describe()
    {
        var descriptions = new (string Name, int Width, int Height, int Frames)[2];
        var index = 0;
        foreach (var ok in new[] { true, false })
        {
            var width = 0;
            var height = 0;
            var frames = 0;
            using (var gif = LoadAnimated(ok))
            {
                if (gif is not null)
                {
                    width = gif.Image.Width;
                    height = gif.Image.Height;
                    frames = gif.Image.GetFrameCount(FrameDimension.Time);
                }
            }

            descriptions[index++] = (FileName(ok), width, height, frames);
        }

        return descriptions;
    }

    /// <summary>
    /// Reads the embedded resource into a seekable <see cref="MemoryStream"/> that outlives the
    /// resource stream. Returns null when the resource does not exist.
    /// </summary>
    internal static MemoryStream? OpenResource(string resourceName, string fileName)
    {
        var assembly = typeof(StatusPig).Assembly;
        var source = assembly.GetManifestResourceStream(resourceName) ?? FindByFileName(assembly, fileName);
        if (source is null)
        {
            return null;
        }

        using (source)
        {
            var buffer = new MemoryStream();
            source.CopyTo(buffer);
            buffer.Position = 0;
            return buffer;
        }
    }

    private static Image? LoadStaticImage(string resourceName, string fileName)
    {
        try
        {
            using var stream = OpenResource(resourceName, fileName);
            if (stream is null)
            {
                Log.Warn($"Embedded status image '{resourceName}' was not found.");
                return null;
            }

            using var decoded = new Bitmap(stream);

            // Copy the first frame into a standalone bitmap so the menu image keeps working after
            // the backing stream is disposed.
            return new Bitmap(decoded);
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to load embedded status image '{resourceName}': {ex.Message}");
            return null;
        }
    }

    private static Stream? FindByFileName(Assembly assembly, string fileName)
    {
        // Resource names are normally RootNamespace + folder path; keep a suffix lookup as a
        // fallback so a namespace change cannot silently break the status images.
        var suffix = "." + fileName;
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return assembly.GetManifestResourceStream(name);
            }
        }

        return null;
    }
}
