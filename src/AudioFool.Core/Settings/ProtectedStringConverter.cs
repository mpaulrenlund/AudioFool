using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioFool.Core.Settings;

/// <summary>
/// Keeps a secret in settings.json encrypted with Windows' own per-account
/// encryption (DPAPI) rather than as plain text (session 71, the user's call;
/// session 60's review): the Last.fm API secret and session key. Written as
/// <c>"dpapi:"</c> and the encrypted bytes in base64.
/// <para>
/// Only this Windows account on this PC can read it back, so a settings.json
/// copied, synced or attached somewhere is no use to anyone else. Any program
/// running as the user still could, as with any secret an app keeps. Elsewhere,
/// or with the bytes damaged, it reads as missing and Last.fm asks to connect
/// again. A plain value from before session 71 is read as it is and written
/// encrypted on the next save. Never throws.
/// </para>
/// </summary>
internal sealed class ProtectedStringConverter : JsonConverter<string?>
{
    private const string Prefix = "dpapi:";

    /// <summary>Ties the bytes to this use, so another app's DPAPI data can't be passed off as ours.</summary>
    private static readonly byte[] Entropy = "AudioFool settings secret"u8.ToArray();

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            reader.Skip();
            return null;
        }

        var value = reader.GetString();
        if (value is null || !value.StartsWith(Prefix, StringComparison.Ordinal))
            return value;

        try
        {
            return Encoding.UTF8.GetString(Dpapi.Unprotect(Convert.FromBase64String(value[Prefix.Length..]), Entropy));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicFailure or DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        try
        {
            writer.WriteStringValue(Prefix + Convert.ToBase64String(Dpapi.Protect(Encoding.UTF8.GetBytes(value), Entropy)));
        }
        catch (Exception ex) when (ex is CryptographicFailure or DllNotFoundException or EntryPointNotFoundException)
        {
            // Better kept as before than lost.
            writer.WriteStringValue(value);
        }
    }

    internal sealed class CryptographicFailure(int error) : Exception($"DPAPI failed (Win32 error {error}).");

    /// <summary>CryptProtectData and CryptUnprotectData, current user, no prompts.</summary>
    internal static class Dpapi
    {
        private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

        public static byte[] Protect(byte[] data, byte[] entropy) => Run(data, entropy, protect: true);

        public static byte[] Unprotect(byte[] data, byte[] entropy) => Run(data, entropy, protect: false);

        private static byte[] Run(byte[] data, byte[] entropy, bool protect)
        {
            var input = GCHandle.Alloc(data, GCHandleType.Pinned);
            var salt = GCHandle.Alloc(entropy, GCHandleType.Pinned);
            var output = default(DataBlob);
            try
            {
                var inBlob = new DataBlob(data.Length, input.AddrOfPinnedObject());
                var saltBlob = new DataBlob(entropy.Length, salt.AddrOfPinnedObject());
                var ok = protect
                    ? CryptProtectData(ref inBlob, null, ref saltBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output)
                    : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref saltBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output);
                if (!ok)
                    throw new CryptographicFailure(Marshal.GetLastPInvokeError());

                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                input.Free();
                salt.Free();
                if (output.Data != IntPtr.Zero)
                    LocalFree(output.Data);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob(int size, IntPtr data)
        {
            public int Size = size;
            public IntPtr Data = data;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy,
            IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy,
            IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
