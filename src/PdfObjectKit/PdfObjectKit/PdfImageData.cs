using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using static PdfObjectKit.PdfPrimitives;

namespace PdfObjectKit;

internal sealed record PdfImageData(PdfValue Image, PdfValue? Mask = null)
{
    private const int MaximumPixels = 16_000_000;

    public static PdfImageData Read(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            return Png(bytes);
        }

        if (bytes.AsSpan().StartsWith(new byte[] { 255, 216 }))
        {
            return Jpeg(bytes);
        }

        throw new NotSupportedException("InsertImage accepts JPEG or PNG file bytes.");
    }

    private static void Dimensions(int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > MaximumPixels)
        {
            throw new InvalidDataException("Image dimensions must be positive and at most 16 million pixels.");
        }
    }

    private static PdfValue ImageStream(byte[] data, int width, int height, string color, string filter)
    {
        var value = Stream(data);
        value.Entries = new()
        {
            ["Type"] = Name("XObject"),
            ["Subtype"] = Name("Image"),
            ["Width"] = Num(width),
            ["Height"] = Num(height),
            ["ColorSpace"] = Name(color),
            ["BitsPerComponent"] = Num(8),
            ["Filter"] = Name(filter)
        };
        return value;
    }

    private static PdfImageData Jpeg(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes[^2] != 255 || bytes[^1] != 217)
        {
            throw new InvalidDataException("JPEG end marker missing.");
        }

        var width = 0;
        var height = 0;
        var components = 0;
        int? adobeTransform = null;
        for (var p = 2; p < bytes.Length;)
        {
            if (bytes[p++] != 255)
            {
                throw new InvalidDataException("Invalid JPEG marker.");
            }

            while (p < bytes.Length && bytes[p] == 255)
            {
                p++;
            }

            if (p >= bytes.Length)
            {
                break;
            }

            var marker = bytes[p++];
            if (p + 2 > bytes.Length)
            {
                throw new InvalidDataException("Truncated JPEG segment.");
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(p, 2));
            if (length < 2 || length > bytes.Length - p)
            {
                throw new InvalidDataException("Invalid JPEG segment length.");
            }

            var data = bytes.AsSpan(p + 2, length - 2);
            if (marker == 218)
            {
                if (components == 0 || data.Length < 4 || data.Length != 4 + 2 * data[0])
                {
                    throw new InvalidDataException("Invalid JPEG scan header.");
                }

                var image = ImageStream(bytes, width, height, components == 1 ? "DeviceGray" : "DeviceRGB", "DCTDecode");
                if (adobeTransform is { } transform)
                {
                    if (transform is not (0 or 1))
                    {
                        throw new NotSupportedException("Unsupported Adobe JPEG color transform.");
                    }

                    image.Entries!["DecodeParms"] = Dict(("ColorTransform", Num(transform)));
                }

                // The JPEG program is embedded intact; entropy decoding remains the PDF viewer's job.
                return new(image);
            }

            if (marker is >= 192 and <= 207 && marker is not (196 or 200 or 204))
            {
                if (marker is not (192 or 193 or 194))
                {
                    throw new NotSupportedException("Only baseline, extended sequential, or progressive DCT JPEG is supported.");
                }

                if (components != 0 || data.Length < 6 || data[0] != 8 || data[5] is not (1 or 3) || data.Length != 6 + 3 * data[5])
                {
                    throw new NotSupportedException("JPEG must have one frame with 8-bit grayscale or three color components; CMYK is unsupported.");
                }

                height = BinaryPrimitives.ReadUInt16BigEndian(data[1..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(data[3..]);
                components = data[5];
                Dimensions(width, height);
            }

            if (marker == 238 && data.Length >= 12 && data[..5].SequenceEqual("Adobe"u8))
            {
                adobeTransform = data[11];
            }

            p += length;
        }

        throw new InvalidDataException("JPEG frame or scan missing.");
    }

    private static PdfImageData Png(byte[] bytes)
    {
        var width = 0;
        var height = 0;
        var color = -1;
        var bitDepth = 8;
        byte[]? palette = null;
        byte[]? transparency = null;
        var seenData = false;
        var closedData = false;
        var ended = false;
        using var compressed = new MemoryStream();
        for (var p = 8; p < bytes.Length;)
        {
            if (bytes.Length - p < 12)
            {
                throw new InvalidDataException("Truncated PNG chunk.");
            }

            var size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(p, 4));
            if (size > bytes.Length - p - 12)
            {
                throw new InvalidDataException("Invalid PNG chunk length.");
            }

            var length = (int)size;
            var type = Encoding.ASCII.GetString(bytes, p + 4, 4);
            var data = bytes.AsSpan(p + 8, length);
            if (Crc(bytes.AsSpan(p + 4, length + 4)) != BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(p + 8 + length, 4)))
            {
                throw new InvalidDataException("PNG chunk checksum mismatch.");
            }

            if (color < 0 && type != "IHDR")
            {
                throw new InvalidDataException("PNG must begin with IHDR.");
            }

            if (seenData && type != "IDAT")
            {
                closedData = true;
            }

            switch (type)
            {
                case "IHDR":
                    if (color >= 0 || length != 13)
                    {
                        throw new InvalidDataException("Invalid PNG header.");
                    }

                    var w = BinaryPrimitives.ReadUInt32BigEndian(data);
                    var h = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                    if (w > int.MaxValue || h > int.MaxValue)
                    {
                        throw new InvalidDataException("PNG dimensions exceed limit.");
                    }

                    width = (int)w;
                    height = (int)h;
                    Dimensions(width, height);
                    color = data[9];
                    bitDepth = data[8];
                    if ((bitDepth != 8 && !(bitDepth == 1 && color == 0)) || color is not (0 or 2 or 3 or 4 or 6) || data[10] != 0 || data[11] != 0 || data[12] != 0)
                    {
                        throw new NotSupportedException("PNG must use 8-bit samples or 1-bit grayscale, standard compression/filtering, and no interlacing.");
                    }
                    break;
                case "PLTE":
                    if (seenData || palette is not null || color is 0 or 4 || length == 0 || length > 768 || length % 3 != 0)
                    {
                        throw new InvalidDataException("Invalid PNG palette.");
                    }

                    palette = data.ToArray();
                    break;
                case "tRNS":
                    if (seenData || transparency is not null || color is 4 or 6 ||
                        (color == 0 && length != 2) || (color == 2 && length != 6) ||
                        (color == 3 && (palette is null || length == 0 || length > palette.Length / 3)))
                    {
                        throw new InvalidDataException("Invalid PNG transparency.");
                    }

                    transparency = data.ToArray();
                    break;
                case "IDAT":
                    if (closedData || (color == 3 && palette is null))
                    {
                        throw new InvalidDataException("Invalid PNG image data ordering.");
                    }

                    compressed.Write(data);
                    seenData = true;
                    break;
                case "IEND":
                    if (!seenData || length != 0 || p + 12 != bytes.Length)
                    {
                        throw new InvalidDataException("Invalid PNG end chunk.");
                    }

                    ended = true;
                    break;
                case "acTL":
                    throw new NotSupportedException("Animated PNG is not supported.");
                default:
                    if ((bytes[p + 4] & 32) == 0)
                    {
                        throw new NotSupportedException($"Unsupported critical PNG chunk {type}.");
                    }
                    break;
            }

            p += length + 12;
        }

        if (!ended)
        {
            throw new InvalidDataException("PNG end chunk missing.");
        }

        var components = color switch { 0 or 3 => 1, 2 => 3, 4 => 2, _ => 4 };
        var encoded = Stream(compressed.ToArray());
        encoded.Entries!["Filter"] = Name("FlateDecode");
        encoded.Entries["DecodeParms"] = Dict(("Predictor", Num(15)), ("Colors", Num(components)), ("Columns", Num(width)), ("BitsPerComponent", Num(bitDepth)));
        var samples = PdfStreamDecoder.Decode(encoded);
        var rowBytes = (width * components * bitDepth + 7) / 8;
        if (samples.Length != (long)rowBytes * height)
        {
            throw new InvalidDataException("PNG sample count does not match its dimensions.");
        }

        if (bitDepth == 1)
        {
            var expanded = new byte[width * height];
            for (var row = 0; row < height; row++)
            {
                for (var column = 0; column < width; column++)
                {
                    var bit = (samples[row * rowBytes + column / 8] >> (7 - column % 8)) & 1;
                    expanded[row * width + column] = (byte)(bit * 255);
                }
            }

            samples = expanded;
        }

        var gray = color is 0 or 4;
        var pixels = new byte[width * height * (gray ? 1 : 3)];
        var alpha = new byte[width * height];
        for (var pixel = 0; pixel < alpha.Length; pixel++)
        {
            var source = pixel * components;
            var target = pixel * (gray ? 1 : 3);
            alpha[pixel] = 255;
            if (color == 3)
            {
                var index = samples[source];
                if (index * 3 + 2 >= palette!.Length)
                {
                    throw new InvalidDataException("PNG palette index outside palette.");
                }

                palette.AsSpan(index * 3, 3).CopyTo(pixels.AsSpan(target));
                if (transparency is not null && index < transparency.Length)
                {
                    alpha[pixel] = transparency[index];
                }
            }
            else
            {
                samples.AsSpan(source, gray ? 1 : 3).CopyTo(pixels.AsSpan(target));
                if (color is 4 or 6)
                {
                    alpha[pixel] = samples[source + components - 1];
                }
                else if (transparency is not null)
                {
                    var transparent = true;
                    for (var c = 0; c < components; c++)
                    {
                        var sample = bitDepth == 1 ? samples[source + c] / 255 : samples[source + c];
                        transparent &= sample == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(c * 2, 2));
                    }

                    if (transparent)
                    {
                        alpha[pixel] = 0;
                    }
                }
            }
        }

        var image = ImageStream(Compress(pixels), width, height, gray ? "DeviceGray" : "DeviceRGB", "FlateDecode");
        var mask = alpha.Any(a => a != 255) ? ImageStream(Compress(alpha), width, height, "DeviceGray", "FlateDecode") : null;
        return new(image, mask);
    }

    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var zip = new ZLibStream(output, CompressionLevel.Optimal, true))
        {
            zip.Write(bytes);
        }

        return output.ToArray();
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xEDB88320u);
            }
        }

        return ~crc;
    }
}
