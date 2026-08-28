namespace AudioFool.Core.Library;

/// <summary>
/// Minimal reader for the DSF (.dsf) header. TagLib's DSD support is patchy, but
/// the header is a fixed layout, so reading it directly is the reliable way to
/// fill in the Sample Rate, Bit Depth, Bitrate and Time columns for DSD files.
/// Layout per Sony's "DSF File Format Specification" v1.01.
/// </summary>
public static class DsfHeaderReader
{
    public readonly record struct DsfProperties(
        int SampleRate,
        int BitDepth,
        int Channels,
        TimeSpan Duration,
        int BitrateKbps);

    public static DsfProperties? TryRead(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            // "DSD " chunk: id(4) size(8) fileSize(8) metadataPointer(8) = 28 bytes
            if (new string(reader.ReadChars(4)) != "DSD ")
                return null;

            stream.Position = 28;

            // "fmt " chunk
            if (new string(reader.ReadChars(4)) != "fmt ")
                return null;

            reader.ReadUInt64();                        // chunk size
            reader.ReadUInt32();                        // format version
            reader.ReadUInt32();                        // format id (0 = DSD raw)
            reader.ReadUInt32();                        // channel type
            var channels = (int)reader.ReadUInt32();
            var sampleRate = (int)reader.ReadUInt32();
            var bitDepth = (int)reader.ReadUInt32();    // 1 for DSD
            var sampleCount = reader.ReadUInt64();

            if (sampleRate <= 0 || channels <= 0)
                return null;

            var duration = TimeSpan.FromSeconds((double)sampleCount / sampleRate);
            var bitrate = (int)((long)sampleRate * channels * bitDepth / 1000);

            return new DsfProperties(sampleRate, bitDepth, channels, duration, bitrate);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return null;
        }
    }
}
