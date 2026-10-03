using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ClipboardSnapper;

public static class ImageSaver
{
    public static async Task<SaveResult> SaveAsync(CapturedImage image)
    {
        var extension = image.Options.Format switch
        {
            ImageFormat.Jpeg => "jpg",
            ImageFormat.Bmp => "bmp",
            _ => "png"
        };
        var name = $"Clipboard_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.{extension}";
        var path = Path.Combine(image.Options.Folder, name);
        StorageFile? temporary = null;
        try
        {
            Directory.CreateDirectory(image.Options.Folder);
            using var input = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(input.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(image.Bytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            var decoder = await BitmapDecoder.CreateAsync(input);
            var width = decoder.OrientedPixelWidth;
            var height = decoder.OrientedPixelHeight;
            if ((ulong)width * height > 64_000_000)
                throw new InvalidDataException("The image exceeds the 64 megapixel limit.");
            var pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight, new BitmapTransform(),
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb)).DetachPixelData();
            if (image.Options.Format == ImageFormat.Jpeg)
            {
                // JPEG has no alpha channel; composite transparent pixels onto white.
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    var alpha = pixels[i + 3];
                    for (var channel = 0; channel < 3; channel++)
                        pixels[i + channel] = (byte)((pixels[i + channel] * alpha + 255 * (255 - alpha) + 127) / 255);
                    pixels[i + 3] = 255;
                }
            }
            var folder = await StorageFolder.GetFolderFromPathAsync(image.Options.Folder);
            temporary = await folder.CreateFileAsync($".{name}.tmp", CreationCollisionOption.FailIfExists);
            using (var output = await temporary.OpenAsync(FileAccessMode.ReadWrite))
            {
                var encoderId = image.Options.Format switch
                {
                    ImageFormat.Jpeg => BitmapEncoder.JpegEncoderId,
                    ImageFormat.Bmp => BitmapEncoder.BmpEncoderId,
                    _ => BitmapEncoder.PngEncoderId
                };
                var properties = new BitmapPropertySet();
                if (image.Options.Format == ImageFormat.Jpeg)
                    properties.Add("ImageQuality", new BitmapTypedValue(image.Options.EncoderQuality, PropertyType.Single));
                var encoder = await BitmapEncoder.CreateAsync(encoderId, output, properties);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8,
                    image.Options.Format == ImageFormat.Jpeg ? BitmapAlphaMode.Ignore : BitmapAlphaMode.Straight,
                    width, height, decoder.DpiX, decoder.DpiY, pixels);
                await encoder.FlushAsync();
            }
            await temporary.RenameAsync(name, NameCollisionOption.FailIfExists);
            temporary = null;
            return new SaveResult(path, DateTimeOffset.Now, true, Width: width, Height: height,
                Generation: image.Generation);
        }
        catch (Exception exception)
        {
            return new SaveResult(path, DateTimeOffset.Now, false,
                $"{exception.GetType().Name}: {exception.Message}", Generation: image.Generation);
        }
        finally
        {
            if (temporary is not null)
            {
                try { await temporary.DeleteAsync(StorageDeleteOption.PermanentDelete); }
                catch { /* Preserve the original failure if temporary-file cleanup fails. */ }
            }
        }
    }
}
