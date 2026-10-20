using System.IO.Compression;

namespace Practical.PdfJson;

// Decodes structural streams and, when indexing is requested, content/CMap streams.
internal static class PdfStreamDecoder
{
    private const int MaximumBytes = 256 * 1024 * 1024;
    public static byte[] Decode(PdfValue stream)
    {
        var data = Convert.FromBase64String(stream.Base64!);
        var entries = stream.Entries!;
        if (!entries.TryGetValue("Filter", out var filter)) return data;
        var filters = filter.Type == "array" ? filter.Items! : [filter];
        entries.TryGetValue("DecodeParms", out var parameters);
        var parms = parameters?.Type == "array" ? parameters.Items! : null;
        if (parms is not null && parms.Count != filters.Count) throw new InvalidDataException("Filter and DecodeParms lengths disagree.");
        for (var i = 0; i < filters.Count; i++)
        {
            if (filters[i].Name is "ASCIIHexDecode" or "AHx")
            {
                var digits = new string(System.Text.Encoding.ASCII.GetString(data).TakeWhile(c => c != '>').Where(c => !PdfSyntaxReader.White((byte)c)).ToArray());
                if (digits.Length % 2 != 0) digits += "0";
                data = Convert.FromHexString(digits);
                continue;
            }
            if (filters[i].Name is "ASCII85Decode" or "A85")
            {
                data = Ascii85(data);
                continue;
            }
            if (filters[i].Name is "RunLengthDecode" or "RL")
            {
                using var decoded = new MemoryStream();
                var ended = false;
                for (var p = 0; p < data.Length;)
                {
                    var n = data[p++];
                    if (n == 128) { ended = true; break; }
                    var length = n < 128 ? n + 1 : 257 - n;
                    if (decoded.Length + length > MaximumBytes) throw new InvalidDataException("Decoded stream exceeds limit.");
                    if (n < 128)
                    {
                        if (length > data.Length - p) throw new InvalidDataException("Truncated RunLength stream.");
                        decoded.Write(data, p, length); p += length;
                    }
                    else
                    {
                        if (p == data.Length) throw new InvalidDataException("Truncated RunLength stream.");
                        for (var j = 0; j < length; j++) decoded.WriteByte(data[p]);
                        p++;
                    }
                }
                if (!ended) throw new InvalidDataException("RunLength end marker missing.");
                data = decoded.ToArray();
                continue;
            }
            if (filters[i].Name is not ("FlateDecode" or "Fl"))
                throw new NotSupportedException($"Stream filter '{filters[i].Name}' is not supported.");
            using var input = new MemoryStream(data);
            using var inflater = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = inflater.Read(buffer)) != 0)
            {
                if (output.Length + count > MaximumBytes) throw new InvalidDataException("Decoded structural stream exceeds 256 MiB.");
                output.Write(buffer, 0, count);
            }
            data = Predictor(output.ToArray(), parms is null ? parameters : parms[i]);
        }
        return data;
    }

    private static byte[] Ascii85(byte[] data)
    {
        using var output = new MemoryStream();
        ulong value = 0; var count = 0; var ended = false;
        void Write(int length)
        {
            if (value > uint.MaxValue) throw new InvalidDataException("Invalid ASCII85 tuple.");
            if (output.Length + length > MaximumBytes) throw new InvalidDataException("Decoded stream exceeds limit.");
            for (var j = 0; j < length; j++) output.WriteByte((byte)(value >> (24 - j * 8)));
            value = 0;
        }
        for (var p = 0; p < data.Length; p++)
        {
            var b = data[p];
            if (PdfSyntaxReader.White(b)) continue;
            if (b == '~')
            {
                if (p + 1 >= data.Length || data[p + 1] != '>') throw new InvalidDataException("Invalid ASCII85 terminator.");
                ended = true; break;
            }
            if (b == 'z')
            {
                if (count != 0) throw new InvalidDataException("ASCII85 z within tuple.");
                Write(4); continue;
            }
            if (b < '!' || b > 'u') throw new InvalidDataException("Invalid ASCII85 character.");
            value = value * 85 + b - (byte)'!';
            if (++count == 5) { Write(4); count = 0; }
        }
        if (!ended || count == 1) throw new InvalidDataException("Truncated ASCII85 stream.");
        if (count > 1)
        {
            for (var j = count; j < 5; j++) value = value * 85 + 84;
            Write(count - 1);
        }
        return output.ToArray();
    }

    private static byte[] Predictor(byte[] data, PdfValue? parameters)
    {
        if (parameters is null || parameters.Type == "null") return data;
        if (parameters.Type != "dictionary") throw new InvalidDataException("Expected DecodeParms dictionary.");
        int Get(string key, int fallback) => parameters.Entries!.TryGetValue(key, out var value)
            ? PdfNativeParser.Count(PdfNativeParser.Number(value)) : fallback;
        var predictor = Get("Predictor", 1);
        if (predictor == 1) return data;
        var colors = Get("Colors", 1);
        var bits = Get("BitsPerComponent", 8);
        var columns = Get("Columns", 1);
        if (colors == 0 || columns == 0 || bits is not (1 or 2 or 4 or 8 or 16))
            throw new InvalidDataException("Invalid predictor dimensions.");
        var rowLength = checked((int)(((long)colors * bits * columns + 7) / 8));
        var pixelLength = (colors * bits + 7) / 8;
        if (rowLength > MaximumBytes) throw new InvalidDataException("Predictor row exceeds limit.");
        if (predictor == 2)
        {
            if (bits != 8) throw new NotSupportedException("TIFF predictor currently supports 8-bit components.");
            if (data.Length % rowLength != 0) throw new InvalidDataException("Truncated TIFF predictor row.");
            for (var row = 0; row < data.Length; row += rowLength)
                for (var x = colors; x < rowLength; x++) data[row + x] = unchecked((byte)(data[row + x] + data[row + x - colors]));
            return data;
        }
        if (predictor is < 10 or > 15) throw new NotSupportedException($"Unsupported predictor {predictor}.");
        if (data.Length % (rowLength + 1) != 0) throw new InvalidDataException("Truncated PNG predictor row.");
        var result = new byte[data.Length / (rowLength + 1) * rowLength];
        for (int source = 0, target = 0; source < data.Length; target += rowLength)
        {
            var filter = data[source++];
            if (filter > 4) throw new InvalidDataException("Invalid PNG predictor filter.");
            for (var x = 0; x < rowLength; x++)
            {
                var left = x >= pixelLength ? result[target + x - pixelLength] : 0;
                var up = target >= rowLength ? result[target + x - rowLength] : 0;
                var upperLeft = x >= pixelLength && target >= rowLength ? result[target + x - rowLength - pixelLength] : 0;
                var prediction = filter switch { 0 => 0, 1 => left, 2 => up, 3 => (left + up) / 2, _ => Paeth(left, up, upperLeft) };
                result[target + x] = unchecked((byte)(data[source++] + prediction));
            }
        }
        return result;
    }
    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a); var pb = Math.Abs(p - b); var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
