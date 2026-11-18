using System.Runtime.InteropServices;

namespace ClipboardSnapper;

internal static class ClipboardReadRetry
{
    private const int ClipboardCannotOpen = unchecked((int)0x800401D0);
    private static readonly int[] DelaysMilliseconds = [50, 100, 200, 400];

    public static bool IsInteropError(Exception exception) =>
        exception is COMException || exception.HResult == ClipboardCannotOpen;

    public static async Task<byte[]?> ReadAsync(uint sequence, Func<uint> currentSequence, Func<Task<byte[]?>> read)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await read(); }
            catch (Exception exception) when (IsInteropError(exception) && sequence != 0 && attempt < DelaysMilliseconds.Length)
            {
                // A newly copied item has its own job. Never reopen it for this failed read.
                if (currentSequence() != sequence) throw;
                await Task.Delay(DelaysMilliseconds[attempt]);
                if (currentSequence() != sequence) throw;
            }
        }
    }
}
