using System.Text;
using Practical.PdfJson;

internal static class NativeParserTests
{
    public static void Run()
    {
        var checks = 0;
        void Check(bool condition)
        {
            if (!condition)
            {
                throw new Exception("Native parser assertion failed.");
            }

            checks++;
        }

        void Reject(byte[] input)
        {
            try
            {
                PdfNativeParser.Parse(input);
            }
            catch (InvalidDataException)
            {
                checks++;
                return;
            }
            catch (NotSupportedException)
            {
                checks++;
                return;
            }

            throw new Exception("Native parser accepted invalid/unsupported input.");
        }

        var payload = " \0\nendstream\nendobj\n";
        var bytes = Fixture(["<< /Type /Catalog /Pages 2 0 R /Escaped#20Name (a\\050b\\051\\n\\377) /Hex <ABC> /Number +001.2300 /Flags [true false null] >>", "<< /Type /Pages /Kids [] /Count 0 >>", $"<< /Length 4 0 R >>\nstream\n{payload}\nendstream", Encoding.Latin1.GetByteCount(payload).ToString()]);
        var graph = PdfNativeParser.Parse(bytes);
        var root = graph.Objects[0].Value.Entries!;
        Check(root.ContainsKey("Escaped Name"));
        Check(Convert.FromBase64String(root["Escaped Name"].Base64!).SequenceEqual(new byte[] { 97, 40, 98, 41, 10, 255 }));
        Check(Convert.FromBase64String(root["Hex"].Base64!).SequenceEqual(new byte[] { 0xAB, 0xC0 }));
        Check(root["Number"].Number == "+001.2300");
        Check(root["Flags"].Items![2].Type == "null");
        Check(Convert.FromBase64String(graph.Objects[2].Value.Base64!).SequenceEqual(Encoding.Latin1.GetBytes(payload)));
        Check(graph.TextIndex.Count == 0 && graph.ImageIndex.Count == 0);
        Check(PdfObjectConverter.ReadPdf(bytes, includeIndexes: false).Objects.Count == 4);
        Reject(Fixture(["<< /Type /Catalog >>", "<< /Length 99999 >>\nstream\nx\nendstream"]));
        Reject(Fixture(["<< /Type /Catalog /A (unterminated >>"]));
        Reject(Fixture(["<< /Type /Catalog /A 1 /A 2 >>"]));
        Reject(Fixture(["<< /Type /Catalog >>"], "/Encrypt 1 0 R"));
        Reject(Encoding.ASCII.GetBytes("%PDF-1.7\n"));
        Console.WriteLine($"Passed {checks} native parser checks.");
    }

    private static byte[] Fixture(string[] objects, string trailerExtra = "")
    {
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (var item in objects)
        {
            offsets.Add(text.Length);
            text.Append($"{offsets.Count} 0 obj\n{item}\nendobj\n");
        }

        var xref = text.Length;
        text.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            text.Append($"{offset:D10} 00000 n \n");
        }

        text.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R {trailerExtra} >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(text.ToString());
    }
}
