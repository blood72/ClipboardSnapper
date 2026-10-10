using System.Globalization;

namespace ClipboardSnapper;

public sealed record ImageSettings(ImageFormat Format, int JpegQuality)
{
    public static ImageSettings Default => new(ImageFormat.Png, 90);
}

public sealed record ImagePreference(ImageSettings Settings, IReadOnlyList<UiMessage> Notices);

public sealed class ImagePreferences(string configPath)
{
    private readonly PortableConfig _config = new(configPath);

    public Task<ImagePreference> LoadAsync() => Task.Run(() =>
    {
        try { return _config.Read(ReadSettings); }
        catch (Exception exception) when (PortableConfig.IsStorageError(exception))
        { return new ImagePreference(ImageSettings.Default, [new("ImageOptionsReadFailed", UiMessage.FromException(exception))]); }
    });

    public Task<ImagePreference> SaveAsync(ImageSettings settings) => Task.Run(() =>
    {
        if (!Enum.IsDefined(settings.Format)) throw UiMessage.Argument("InvalidImageFormat");
        if (settings.JpegQuality is < 1 or > 100) throw UiMessage.OutOfRange(nameof(settings.JpegQuality), "InvalidQuality");
        try
        {
            _config.Update(document =>
            {
                document.Set("Storage", "ImageFormat", settings.Format.ToString().ToUpperInvariant());
                document.Set("Storage", "JpegQuality", settings.JpegQuality.ToString(CultureInfo.InvariantCulture));
            });
            return new ImagePreference(settings, []);
        }
        catch (Exception exception) when (PortableConfig.IsStorageError(exception))
        { return new ImagePreference(settings, [new("ImageOptionsWriteFailed", UiMessage.FromException(exception))]); }
    });

    private static ImagePreference ReadSettings(IniDocument document)
    {
        var notices = new List<UiMessage>();
        var storedFormat = document.Get("Storage", "ImageFormat");
        var format = storedFormat?.ToUpperInvariant() switch
        {
            null or "PNG" => ImageFormat.Png,
            "JPEG" => ImageFormat.Jpeg,
            "BMP" => ImageFormat.Bmp,
            _ => ImageFormat.Png
        };
        if (storedFormat is not null && storedFormat.ToUpperInvariant() is not ("PNG" or "JPEG" or "BMP"))
            notices.Add(new("StoredImageFormatInvalid", storedFormat));
        var storedQuality = document.Get("Storage", "JpegQuality");
        var quality = 90;
        if (storedQuality is not null && (!int.TryParse(storedQuality, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out quality) || quality is < 1 or > 100))
        {
            quality = 90;
            notices.Add(new("StoredQualityInvalid", storedQuality));
        }
        return new(new(format, quality), notices.ToArray());
    }
}
