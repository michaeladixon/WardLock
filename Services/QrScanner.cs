using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.Common;
using ZXing.Windows.Compatibility;

namespace WardLock.Services;

public static class QrScanner
{
    private static readonly BarcodeReader Reader = new()
    {
        AutoRotate = true,
        Options = new DecodingOptions
        {
            TryHarder = true,
            PossibleFormats = [BarcodeFormat.QR_CODE],
            TryInverted = true
        }
    };

    /// <summary>
    /// Decode a QR code from a System.Drawing.Bitmap.
    /// Returns the otpauth:// URI or null if not found.
    /// </summary>
    public static string? DecodeFromBitmap(Bitmap bitmap)
    {
        // ZXing's BitmapLuminanceSource only handles Format32bppArgb reliably.
        // Normalise any other format (palettized PNG, 24bpp JPEG, etc.) before decoding.
        using var normalised = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(normalised))
            g.DrawImage(bitmap, 0, 0, bitmap.Width, bitmap.Height);

        var result = Reader.Decode(normalised);
        var text = result?.Text;
        if (text != null &&
            (text.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase) ||
             text.StartsWith("otpauth-migration://", StringComparison.OrdinalIgnoreCase)))
            return text;
        return null;
    }

    /// <summary>
    /// Decode a QR code from an image file path (png, jpg, bmp, gif).
    /// </summary>
    public static string? DecodeFromFile(string filePath)
    {
        using var bitmap = new Bitmap(filePath);
        return DecodeFromBitmap(bitmap);
    }

    /// <summary>
    /// Capture full virtual screen and try to find a QR code anywhere on it.
    /// </summary>
    public static string? DecodeFromScreen()
    {
        // Capture the entire virtual desktop (all monitors)
        var left = (int)SystemParameters.VirtualScreenLeft;
        var top = (int)SystemParameters.VirtualScreenTop;
        var width = (int)SystemParameters.VirtualScreenWidth;
        var height = (int)SystemParameters.VirtualScreenHeight;

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var gfx = Graphics.FromImage(bitmap))
        {
            gfx.CopyFromScreen(left, top, 0, 0, new System.Drawing.Size(width, height),
                CopyPixelOperation.SourceCopy);
        }

        return DecodeFromBitmap(bitmap);
    }

    /// <summary>
    /// Capture a specific region of the screen.
    /// </summary>
    public static string? DecodeFromRegion(int x, int y, int width, int height)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var gfx = Graphics.FromImage(bitmap))
        {
            gfx.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height),
                CopyPixelOperation.SourceCopy);
        }

        return DecodeFromBitmap(bitmap);
    }

    /// <summary>
    /// Try to decode QR from clipboard image.
    /// </summary>
    public static string? DecodeFromClipboard()
    {
        if (Clipboard.ContainsImage())
        {
            var bitmapSource = Clipboard.GetImage();
            if (bitmapSource != null)
            {
                using var bitmap = BitmapSourceToBitmap(bitmapSource);
                return DecodeFromBitmap(bitmap);
            }
        }
        return null;
    }

    private static Bitmap BitmapSourceToBitmap(BitmapSource source)
    {
        var encoder = new BmpBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        // Copy bytes into a fresh stream: System.Drawing.Bitmap requires its
        // backing stream to remain open for the lifetime of the object.
        return new Bitmap(new MemoryStream(ms.ToArray()));
    }
}
