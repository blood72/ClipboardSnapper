#if WINDOWS
using ClipboardSnapper;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

static class EncoderContracts
{
    public static async Task RunAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"ClipboardSnapper-encoder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var bytes = await MakeImageAsync();
            var options = new SaveOptions(folder, ImageFormat.Jpeg);
            var files = new Dictionary<int, string>();
            foreach (var quality in new[] { 1, 90, 100 })
            {
                var result = await ImageSaver.SaveAsync(new CapturedImage(bytes, options with { JpegQuality = quality }, 7));
                Check.That(result.Success, $"JPEG quality {quality} failed: {result.Error}");
                Check.That(result.Generation == 7 && result.Width == 128 && result.Height == 128, "Encoder lost capture identity or dimensions.");
                files.Add(quality, result.FilePath);
                using var stream = await (await StorageFile.GetFileFromPathAsync(result.FilePath)).OpenReadAsync();
                var decoder = await BitmapDecoder.CreateAsync(stream);
                var decoded = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                    new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
                Check.That(decoded[0] >= 248 && decoded[1] >= 248 && decoded[2] >= 248, "JPEG transparency was not composited onto white.");
            }
            var low = await File.ReadAllBytesAsync(files[1]);
            var normal = await File.ReadAllBytesAsync(files[90]);
            var high = await File.ReadAllBytesAsync(files[100]);
            Check.That(low.Length < normal.Length && normal.Length < high.Length,
                $"JPEG quality did not affect real encoded sizes: {low.Length}, {normal.Length}, {high.Length}.");
            Check.That(!Quantization(low).SequenceEqual(Quantization(normal)) &&
                !Quantization(normal).SequenceEqual(Quantization(high)), "ImageQuality did not change JPEG quantization tables.");
            var defaultResult = await ImageSaver.SaveAsync(new CapturedImage(bytes, options, 7));
            Check.That(defaultResult.Success && Quantization(await File.ReadAllBytesAsync(defaultResult.FilePath)).SequenceEqual(Quantization(normal)),
                "Default JPEG output did not use quality 90.");

            foreach (var format in new[] { ImageFormat.Png, ImageFormat.Bmp })
            {
                var result = await ImageSaver.SaveAsync(new CapturedImage(bytes, options with { Format = format, JpegQuality = 0 }, 8));
                Check.That(result.Success && result.Generation == 8, $"{format} incorrectly used the JPEG-only setting: {result.Error}");
            }
            foreach (var format in new[] { ImageFormat.Png, ImageFormat.Jpeg, ImageFormat.Bmp })
            {
                var extension = format switch { ImageFormat.Jpeg => "jpg", ImageFormat.Bmp => "bmp", _ => "png" };
                var collisionOptions = options with { Format = format, NamingFormula = "Repeated" };
                var existing = Path.Combine(folder, "Repeated." + extension);
                await File.WriteAllBytesAsync(existing, [9, 8, 7]);
                var numbered = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
                    ImageSaver.SaveAsync(new CapturedImage(bytes, collisionOptions, 9) { CaptureIndex = index })));
                Check.That(numbered.All(result => result.Success) && numbered.Select(result => result.FilePath).Distinct().Count() == 8,
                    $"Concurrent {format} encoding failed or overwrote a repeated name: {string.Join(" | ", numbered.Select(r => r.Error))}");
                Check.That(numbered.Select(r => Path.GetFileName(r.FilePath)).ToHashSet().SetEquals(
                    Enumerable.Range(2, 8).Select(index => $"Repeated ({index}).{extension}")), "Concurrent encoding skipped or reused a collision suffix.");
                Check.That((await File.ReadAllBytesAsync(existing)).SequenceEqual(new byte[] { 9, 8, 7 }), "Encoding overwrote the existing file.");
                foreach (var result in numbered)
                {
                    using var output = await (await StorageFile.GetFileFromPathAsync(result.FilePath)).OpenReadAsync();
                    var decoder = await BitmapDecoder.CreateAsync(output);
                    Check.That(decoder.PixelWidth == 128 && decoder.PixelHeight == 128, "A final collision output was not a complete image.");
                }
            }
            var acceptedAt = new DateTimeOffset(new DateTime(2026, 10, 6, 9, 3, 5, 123, DateTimeKind.Local));
            var dated = await ImageSaver.SaveAsync(new CapturedImage(bytes,
                options with { Format = ImageFormat.Png, NamingFormula = "At_$YYYY$MM$DD_$hh$mm$ss_$fff_${start=1}" }, 9)
                { AcceptedAt = acceptedAt, CaptureIndex = 4 });
            Check.That(dated.Success && Path.GetFileName(dated.FilePath) == "At_20261006_090305_123_5.png", "Encoding used save time instead of captured metadata.");
            var invalidRule = await ImageSaver.SaveAsync(new CapturedImage(bytes, options with { NamingFormula = "../escape" }, 9));
            Check.That(!invalidRule.Success && invalidRule.Error.Contains("FormatException", StringComparison.Ordinal), "Invalid naming escaped the save error flow.");
            var session = new SessionHistory();
            var pending = new CapturedImage(bytes, options, session.Generation);
            var saving = ImageSaver.SaveAsync(pending);
            session.Clear();
            var savedAfterClear = await saving;
            Check.That(savedAfterClear.Success && File.Exists(savedAfterClear.FilePath) && !session.Publish(savedAfterClear),
                "A real pre-clear image did not finish saving without returning to the history.");
            var failed = await ImageSaver.SaveAsync(new CapturedImage([1, 2, 3], options, pending.Generation));
            Check.That(!failed.Success && failed.Generation == pending.Generation && !session.Publish(failed), "Late encoder failure lost its generation.");
            Check.That(!Directory.EnumerateFiles(folder, "*.tmp").Any(), "Encoding failure/success left temporary images behind.");
            Console.WriteLine("::notice::Real encoding verified: PNG/JPEG/BMP concurrent (2)–(9) names, existing-file hashes, complete outputs, capture timestamp/numbering and invalid-name/temp cleanup.");
            Console.WriteLine($"::notice::Real BitmapEncoder verified: JPEG 1/90/100 sizes {low.Length}/{normal.Length}/{high.Length}, distinct quantization, default 90, white alpha, PNG/BMP isolation and post-clear file preservation.");
        }
        finally { Directory.Delete(folder, true); }
    }

    private static async Task<byte[]> MakeImageAsync()
    {
        var pixels = new byte[128 * 128 * 4];
        new Random(42).NextBytes(pixels);
        for (var i = 0; i < pixels.Length; i += 4) pixels[i + 3] = (byte)(i < 128 * 16 * 4 ? 0 : 255);
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, 128, 128, 96, 96, pixels);
        await encoder.FlushAsync();
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        var bytes = new byte[(int)stream.Size];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static byte[] Quantization(byte[] jpeg)
    {
        var tables = new List<byte>();
        for (var i = 2; i + 3 < jpeg.Length;)
        {
            Check.That(jpeg[i] == 0xff, "Invalid JPEG marker.");
            var marker = jpeg[i + 1];
            if (marker == 0xda || marker == 0xd9) break;
            var length = jpeg[i + 2] * 256 + jpeg[i + 3];
            if (marker == 0xdb) tables.AddRange(jpeg.AsSpan(i + 4, length - 2).ToArray());
            i += length + 2;
        }
        Check.That(tables.Count != 0, "JPEG quantization tables are missing.");
        return tables.ToArray();
    }
}
#endif
