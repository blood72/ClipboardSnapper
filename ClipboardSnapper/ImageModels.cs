namespace ClipboardSnapper;

public enum ImageFormat { Png, Jpeg, Bmp }
public sealed record SaveOptions(string Folder, ImageFormat Format, int JpegQuality = 90,
    string NamingFormula = FilenameRule.DefaultFormula)
{
    public float EncoderQuality => JpegQuality is >= 1 and <= 100
        ? JpegQuality / 100f
        : throw UiMessage.OutOfRange(nameof(JpegQuality), "InvalidQuality");
}
public sealed record CapturedImage(byte[] Bytes, SaveOptions Options, long Generation)
{
    public DateTimeOffset AcceptedAt { get; init; } = DateTimeOffset.Now;
    public long CaptureIndex { get; init; }
    public long ProgressId { get; init; }
}
public sealed record SaveResult(string FilePath, DateTimeOffset Time, bool Success,
    string Error = "", uint Width = 0, uint Height = 0, long Generation = 0)
{
    public UiMessage? ErrorMessage { get; init; }
}
