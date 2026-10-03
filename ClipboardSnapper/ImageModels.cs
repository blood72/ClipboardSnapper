namespace ClipboardSnapper;

public enum ImageFormat { Png, Jpeg, Bmp }
public sealed record SaveOptions(string Folder, ImageFormat Format, int JpegQuality = 90)
{
    public float EncoderQuality => JpegQuality is >= 1 and <= 100
        ? JpegQuality / 100f
        : throw new ArgumentOutOfRangeException(nameof(JpegQuality), "JPEG quality must be between 1 and 100.");
}
public sealed record CapturedImage(byte[] Bytes, SaveOptions Options, long Generation);
public sealed record SaveResult(string FilePath, DateTimeOffset Time, bool Success,
    string Error = "", uint Width = 0, uint Height = 0, long Generation = 0);
