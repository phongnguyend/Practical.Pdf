using PdfObjectKit.Graphs;
using PdfObjectKit.Models;
using PdfObjectKit.Parsers;
using static PdfObjectKit.Primitives.PdfPrimitives;

namespace PdfObjectKit.Rendering;

internal static class PdfCanvasImageDecoder
{
    public static PdfCanvasCommand Decode(PdfGraph graph, PdfValue image)
    {
        var width = checked((int)graph.Number(image, "Width"));
        var height = checked((int)graph.Number(image, "Height"));
        if (width <= 0 || height <= 0 || (long)width * height > 16000000)
        {
            throw new NotSupportedException("Image pixel budget exceeded.");
        }
        if (graph.Get(image, "ImageMask")?.Boolean == true || graph.Get(image, "Mask") is not null || graph.Get(image, "Decode") is not null)
        {
            throw new NotSupportedException("Image masks and custom Decode arrays are not supported.");
        }
        var filter = graph.Get(image, "Filter");
        var color = graph.Get(image, "ColorSpace")?.Name;
        byte[]? alpha = null;
        double[]? matte = null;
        if (graph.Get(image, "SMask") is { } mask)
        {
            if (graph.Number(mask, "Width") != width || graph.Number(mask, "Height") != height || graph.Number(mask, "BitsPerComponent") != 8 || graph.Get(mask, "ColorSpace")?.Name != "DeviceGray")
            {
                throw new NotSupportedException("Only matching-size 8-bit grayscale soft masks are supported.");
            }
            alpha = PdfStreamDecoder.Decode(mask);
            if (alpha.Length < checked(width * height))
            {
                throw new InvalidDataException("Truncated alpha samples.");
            }
            if (graph.Get(mask, "Decode")?.Items is { } decode)
            {
                if (decode.Count != 2)
                {
                    throw new InvalidDataException("Invalid soft-mask Decode array.");
                }
                var low = Number(decode[0]);
                var high = Number(decode[1]);
                for (var pixel = 0; pixel < width * height; pixel++)
                {
                    alpha[pixel] = (byte)Math.Clamp(Math.Round(255 * low + alpha[pixel] * (high - low)), 0, 255);
                }
            }
            if (graph.Get(mask, "Matte")?.Items is { } components)
            {
                var channels = color == "DeviceRGB" ? 3 : color == "DeviceGray" ? 1 : 0;
                if (channels == 0 || components.Count != channels)
                {
                    throw new NotSupportedException("Unsupported soft-mask matte color space.");
                }
                matte = channels == 1 ? Enumerable.Repeat(Number(components[0]), 3).ToArray() : components.Select(Number).ToArray();
            }
        }
        PdfCanvasCommand command;
        if ((filter?.Name == "DCTDecode" || filter?.Items is { Count: 1 } filters && graph.Resolve(filters[0]).Name == "DCTDecode") && color is "DeviceRGB" or "DeviceGray")
        {
            command = new("jpeg") { Data = image.Base64, Width = width, Height = height, AlphaData = alpha is null ? null : Convert.ToBase64String(alpha), Matte = matte };
        }
        else
        {
            var channels = color == "DeviceRGB" ? 3 : color == "DeviceGray" ? 1 : 0;
            if (channels == 0 || graph.Number(image, "BitsPerComponent") != 8)
            {
                throw new NotSupportedException("Only 8-bit DeviceRGB/DeviceGray images are supported.");
            }
            var bytes = PdfStreamDecoder.Decode(image);
            if (bytes.Length < (long)width * height * channels)
            {
                throw new InvalidDataException("Truncated image samples.");
            }
            var rgba = new byte[checked(width * height * 4)];
            for (var pixel = 0; pixel < width * height; pixel++)
            {
                rgba[pixel * 4] = bytes[pixel * channels];
                rgba[pixel * 4 + 1] = bytes[pixel * channels + (channels == 3 ? 1 : 0)];
                rgba[pixel * 4 + 2] = bytes[pixel * channels + (channels == 3 ? 2 : 0)];
                var opacity = alpha?[pixel] ?? 255;
                rgba[pixel * 4 + 3] = opacity;
                if (matte is not null && opacity > 0)
                {
                    for (var channel = 0; channel < 3; channel++)
                    {
                        rgba[pixel * 4 + channel] = (byte)Math.Clamp(Math.Round((rgba[pixel * 4 + channel] - (255 - opacity) * matte[channel]) * 255 / opacity), 0, 255);
                    }
                }
            }
            command = new("rgba") { Data = Convert.ToBase64String(rgba), Width = width, Height = height };
        }
        return command;
    }
}
