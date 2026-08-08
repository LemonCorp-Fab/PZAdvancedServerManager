using System.Buffers.Binary;

namespace PZAdvancedServerManager.Core.Packaging;

public static class WorkshopPreviewFile
{
    public const long MaximumBytes = 1024 * 1024;

    public static string Validate(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("L'image Workshop est introuvable.", path);
        var length = new FileInfo(path).Length;
        if (length == 0) throw new InvalidDataException("L'image Workshop est vide.");
        if (length > MaximumBytes) throw new InvalidDataException("L'image Workshop dépasse 1 Mio. Réduisez-la avant de construire le pack.");

        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[32];
        var read = stream.Read(header);
        var (extension, width, height) = Detect(stream, header[..read]);
        if (width is < 64 or > 4096 || height is < 64 or > 4096)
            throw new InvalidDataException("La preview Workshop doit mesurer entre 64 et 4 096 pixels de côté.");
        return extension;
    }

    private static (string Extension, int Width, int Height) Detect(Stream stream, ReadOnlySpan<byte> header)
    {
        if (header.Length >= 24 && header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            return (".png", BinaryPrimitives.ReadInt32BigEndian(header[16..20]), BinaryPrimitives.ReadInt32BigEndian(header[20..24]));

        if (header.Length >= 10 && (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8)))
            return (".gif", BinaryPrimitives.ReadUInt16LittleEndian(header[6..8]), BinaryPrimitives.ReadUInt16LittleEndian(header[8..10]));

        if (header.Length >= 2 && header[0] == 0xff && header[1] == 0xd8)
        {
            stream.Position = 2;
            var sizeBytes = new byte[2];
            var dimensions = new byte[5];
            while (stream.Position < stream.Length)
            {
                if (stream.ReadByte() != 0xff) continue;
                int marker;
                do marker = stream.ReadByte(); while (marker == 0xff);
                if (marker is -1 or 0xd9 or 0xda) break;
                if (stream.Read(sizeBytes) != 2) break;
                var size = BinaryPrimitives.ReadUInt16BigEndian(sizeBytes);
                if (size < 2) break;
                if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
                {
                    if (stream.Read(dimensions) != 5) break;
                    return (".jpg", BinaryPrimitives.ReadUInt16BigEndian(dimensions[1..3]), BinaryPrimitives.ReadUInt16BigEndian(dimensions[3..5]));
                }
                stream.Seek(size - 2, SeekOrigin.Current);
            }
        }

        throw new InvalidDataException("Format de preview non pris en charge. Utilisez une image PNG, JPEG ou GIF valide.");
    }
}
