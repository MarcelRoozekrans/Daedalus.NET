using System.Buffers.Binary;
using System.Buffers.Text;
using Thalos.Workflow;

namespace Daedalus.Api.Controllers;

/// <summary>
///     The list's <c>after</c> cursor on the wire: base64url of the run's creation time in UTC ticks, 8 bytes big
///     endian, then its id, 16 bytes. Opaque to clients; only this class reads or writes it.
/// </summary>
internal static class WorkflowRunCursorCodec
{
    private const int Length = 24;

    public static string Encode(WorkflowRunCursor cursor)
    {
        Span<byte> bytes = stackalloc byte[Length];
        BinaryPrimitives.WriteInt64BigEndian(bytes, cursor.CreatedAt.UtcTicks);
        cursor.Id.TryWriteBytes(bytes[8..]);
        return Base64Url.EncodeToString(bytes);
    }

    /// <summary>
    ///     Reads a cursor <see cref="Encode"/> wrote. <see cref="Base64Url.IsValid(ReadOnlySpan{char}, out int)"/> runs
    ///     first because <see cref="Base64Url.TryDecodeFromChars"/> throws <see cref="FormatException"/> on a character
    ///     outside the alphabet rather than returning false; it also gives the decoded length, so a cursor that is not
    ///     exactly 24 bytes is refused before anything is decoded. Ticks outside what a <see cref="DateTimeOffset"/> can
    ///     hold are refused too, since its constructor would throw on them.
    /// </summary>
    public static bool TryDecode(string text, out WorkflowRunCursor cursor)
    {
        cursor = null!;
        if (string.IsNullOrEmpty(text) || !Base64Url.IsValid(text, out var decodedLength) || decodedLength != Length)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[Length];
        Base64Url.DecodeFromChars(text, bytes);
        var ticks = BinaryPrimitives.ReadInt64BigEndian(bytes);
        if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
        {
            return false;
        }

        cursor = new WorkflowRunCursor(new DateTimeOffset(ticks, TimeSpan.Zero), new Guid(bytes[8..]));
        return true;
    }
}
