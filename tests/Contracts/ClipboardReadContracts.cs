using System.Runtime.InteropServices;
using ClipboardSnapper;

internal static class ClipboardReadContracts
{
    private const int ClipboardCannotOpen = unchecked((int)0x800401D0);

    public static async Task RunAsync()
    {
        const uint sequence = 17;
        var failure = new COMException("", ClipboardCannotOpen);
        var image = new byte[] { 12, 34 };
        var reads = 0;
        var recovered = await ClipboardReadRetry.ReadAsync(sequence, () => sequence, () =>
        {
            if (++reads == 1) throw failure;
            return Task.FromResult<byte[]?>(image);
        });
        Check.That(reads == 2 && ReferenceEquals(recovered, image), "The original image did not recover after a clipboard COM error.");

        // Replace the clipboard after the failed attempt has entered its retry delay.
        reads = 0;
        var sequenceChecks = 0;
        var replaced = ClipboardReadRetry.ReadAsync(sequence, () => ++sequenceChecks == 1 ? sequence : sequence + 1, () =>
        {
            if (++reads == 1) throw failure;
            return Task.FromResult<byte[]?>([56, 78]);
        });
        await ExpectFailureAsync(replaced, failure);
        Check.That(reads == 1 && sequenceChecks == 2, "A newer clipboard item was read for the earlier capture.");

        reads = 0;
        var exhausted = ClipboardReadRetry.ReadAsync(sequence, () => sequence, () => { reads++; throw failure; });
        await ExpectFailureAsync(exhausted, failure);
        Check.That(reads == 5, "Repeated clipboard errors did not stop at the retry limit.");

        reads = 0;
        var nonInteropFailure = new InvalidDataException("Image exceeds the size limit.");
        var invalid = ClipboardReadRetry.ReadAsync(sequence, () => sequence, () => { reads++; throw nonInteropFailure; });
        await ExpectFailureAsync(invalid, nonInteropFailure);
        Check.That(reads == 1, "Permanent image validation errors must not retry.");
    }

    private static async Task ExpectFailureAsync(Task task, Exception expected)
    {
        try { await task; }
        catch (Exception exception) when (ReferenceEquals(exception, expected)) { return; }
        throw new InvalidOperationException("Expected the original clipboard read error.");
    }
}
