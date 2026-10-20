using System.Security.Cryptography;

namespace Practical.PdfJson;

internal readonly record struct PdfMatrix(double A, double B, double C, double D, double E, double F)
{
    public static PdfMatrix Identity => new(1, 0, 0, 1, 0, 0);

    public PdfImagePoint Point(double x, double y) => new(A * x + C * y + E, B * x + D * y + F);
    public PdfMatrix Multiply(PdfMatrix rhs) => new(A * rhs.A + C * rhs.B, B * rhs.A + D * rhs.B, A * rhs.C + C * rhs.D, B * rhs.C + D * rhs.D, A * rhs.E + C * rhs.F + E, B * rhs.E + D * rhs.F + F);
    public PdfMatrix Translate(double x, double y) => Multiply(new(1, 0, 0, 1, x, y));
    public static PdfMatrix From(IEnumerable<PdfValue> values)
    {
        var n = values.Select(PdfPrimitives.Number).ToArray();
        if (n.Length != 6)
        {
            throw new InvalidDataException("Expected six matrix components.");
        }

        return new(n[0], n[1], n[2], n[3], n[4], n[5]);
    }
}

internal sealed record NativeLetter(string Value, PdfImagePoint StartBaseLine, double FontSize);
internal sealed record NativeSlot(int Operation, int Token, int Start, string Text, byte[] Bytes, PdfNativeFont Font, List<NativeGlyph> Glyphs, double FontSize, PdfReference Stream);
internal sealed class PdfPageInterpreter
{
    public List<NativeLetter> Letters { get; } = [];

    public List<NativeSlot> Slots { get; } = [];

    public List<PdfOperation> Operations { get; } = [];

    public List<PdfImageOccurrence> Images { get; } = [];

    public string Text => string.Concat(Letters.Select(l => l.Value));

    public string? TextWarning { get; private set; }

    public string? ImageWarning { get; private set; }

    private sealed record State(PdfMatrix Matrix, string? Font = null, double Size = 0, double CharSpace = 0, double WordSpace = 0, double Scale = 1, double Leading = 0, double Rise = 0, PdfValue? DirectFont = null);
    private readonly PdfGraph graph;
    private readonly Dictionary<PdfValue, PdfNativeFont> fonts = [];
    private readonly HashSet<PdfValue> forms = [];
    private int count;
    public PdfPageInterpreter(PdfGraph graph, NativePage page)
    {
        this.graph = graph;
        var state = new State(PdfMatrix.Identity);
        var tm = PdfMatrix.Identity;
        var line = tm;
        var stack = new Stack<State>();
        foreach (var(id, stream)in page.Streams)
        {
            try
            {
                var operations = PdfContent.Parse(PdfStreamDecoder.Decode(stream));
                var offset = Operations.Count;
                Operations.AddRange(operations);
                Execute(operations, page.Resources, id, offset, ref state, ref tm, ref line, stack, 0);
            }
            catch (Exception ex)when (ex is not OutOfMemoryException)
            {
                TextWarning ??= ex.Message;
                ImageWarning ??= ex.Message;
                break;
            }
        }
    }

    private void Execute(List<PdfOperation> operations, PdfValue resources, PdfReference stream, int offset, ref State state, ref PdfMatrix tm, ref PdfMatrix line, Stack<State> stack, int depth)
    {
        if (depth > 100)
        {
            throw new InvalidDataException("Form nesting exceeds limit.");
        }

        for (var i = 0; i < operations.Count; i++)
        {
            if (++count > 1_000_000)
            {
                throw new InvalidDataException("Too many drawing operations.");
            }

            var op = operations[i];
            var a = op.Operands;
            double N(int j) => PdfPrimitives.Number(a[j]);
            switch (op.Operator)
            {
                case "q":
                    stack.Push(state);
                    break;
                case "Q":
                    state = stack.Count > 0 ? stack.Pop() : throw new InvalidDataException("Unbalanced graphics restore.");
                    break;
                case "cm":
                    state = state with
                    {
                        Matrix = state.Matrix.Multiply(PdfMatrix.From(a))
                    };
                    break;
                case "BT":
                    tm = line = PdfMatrix.Identity;
                    break;
                case "Tf":
                    state = state with
                    {
                        Font = a[0].Name,
                        Size = N(1),
                        DirectFont = null
                    };
                    break;
                case "gs":
                    var graphicsState = graph.Get(resources, "ExtGState")?.Entries?.GetValueOrDefault(a[0].Name!);
                    if (graphicsState is null)
                    {
                        throw new InvalidDataException("Missing ExtGState resource.");
                    }

                    if (graph.Get(graphicsState, "Font")?.Items is { Count: 2 } selectedFont)
                    {
                        state = state with
                        {
                            Font = null,
                            DirectFont = selectedFont[0],
                            Size = PdfPrimitives.Number(selectedFont[1])
                        };
                    }

                    break;
                case "Tc":
                    state = state with
                    {
                        CharSpace = N(0)
                    };
                    break;
                case "Tw":
                    state = state with
                    {
                        WordSpace = N(0)
                    };
                    break;
                case "Tz":
                    state = state with
                    {
                        Scale = N(0) / 100
                    };
                    break;
                case "TL":
                    state = state with
                    {
                        Leading = N(0)
                    };
                    break;
                case "Ts":
                    state = state with
                    {
                        Rise = N(0)
                    };
                    break;
                case "Tm":
                    tm = line = PdfMatrix.From(a);
                    break;
                case "TD":
                    state = state with
                    {
                        Leading = -N(1)
                    };
                    goto case "Td";
                case "Td":
                    tm = line = line.Translate(N(0), N(1));
                    break;
                case "T*":
                    tm = line = line.Translate(0, -state.Leading);
                    break;
                case "\"":
                    state = state with
                    {
                        WordSpace = N(0),
                        CharSpace = N(1)
                    };
                    tm = line = line.Translate(0, -state.Leading);
                    Show(a[2], -1, i + offset, resources, stream, state, ref tm);
                    break;
                case "'":
                    tm = line = line.Translate(0, -state.Leading);
                    Show(a[0], -1, i + offset, resources, stream, state, ref tm);
                    break;
                case "Tj":
                    Show(a[0], -1, i + offset, resources, stream, state, ref tm);
                    break;
                case "TJ":
                    for (var j = 0; j < a[0].Items!.Count; j++)
                    {
                        var value = a[0].Items![j];
                        if (value.Type == "number")
                        {
                            tm = tm.Translate(-PdfPrimitives.Number(value) / 1000 * state.Size * state.Scale, 0);
                        }
                        else
                        {
                            Show(value, j, i + offset, resources, stream, state, ref tm);
                        }
                    }

                    break;
                case "BI":
                    Image(op.InlineImage!, null, state.Matrix);
                    break;
                case "Do":
                    var xobjects = graph.Get(resources, "XObject");
                    var raw = xobjects?.Entries?.GetValueOrDefault(a[0].Name!);
                    if (raw is null)
                    {
                        throw new InvalidDataException("Missing XObject resource.");
                    }

                    var xobject = graph.Resolve(raw);
                    if (graph.Get(xobject, "Subtype")?.Name == "Image")
                    {
                        Image(xobject, raw.Reference, state.Matrix);
                    }
                    else if (graph.Get(xobject, "Subtype")?.Name == "Form")
                    {
                        if (!forms.Add(xobject))
                        {
                            throw new InvalidDataException("Recursive Form XObject.");
                        }

                        try
                        {
                            var childState = state;
                            if (graph.Get(xobject, "Matrix")?.Items is { } matrix)
                            {
                                childState = state with
                                {
                                    Matrix = state.Matrix.Multiply(PdfMatrix.From(matrix))
                                };
                            }

                            var childTm = tm;
                            var childLine = line;
                            Execute(PdfContent.Parse(PdfStreamDecoder.Decode(xobject)), graph.Get(xobject, "Resources") ?? resources, raw.Reference ?? stream, 0, ref childState, ref childTm, ref childLine, new(), depth + 1);
                        }
                        finally
                        {
                            forms.Remove(xobject);
                        }
                    }

                    break;
            }
        }
    }

    private void Show(PdfValue value, int token, int operation, PdfValue resources, PdfReference stream, State state, ref PdfMatrix tm)
    {
        try
        {
            var fontValue = state.DirectFont ?? graph.Get(resources, "Font")?.Entries?.GetValueOrDefault(state.Font ?? "") ?? throw new InvalidDataException("Text font resource missing.");
            fontValue = graph.Resolve(fontValue);
            if (!fonts.TryGetValue(fontValue, out var font))
            {
                fonts[fontValue] = font = new(graph, fontValue);
            }

            if (font.IsVertical)
            {
                throw new NotSupportedException("Vertical text indexing is not supported.");
            }

            var bytes = Convert.FromBase64String(value.Base64!);
            var glyphs = font.Decode(bytes);
            var text = string.Concat(glyphs.Select(g => g.Text));
            Slots.Add(new(operation, token, Text.Length, text, bytes, font, glyphs, state.Size, stream));
            foreach (var glyph in glyphs)
            {
                var point = state.Matrix.Multiply(tm).Point(0, state.Rise);
                Letters.Add(new(glyph.Text, point, state.Size));
                var advance = (glyph.Width * state.Size + state.CharSpace + (glyph.Bytes.Length == 1 && glyph.Bytes[0] == 32 ? state.WordSpace : 0)) * state.Scale;
                tm = tm.Translate(advance, 0);
            }
        }
        catch (Exception ex)when (ex is not OutOfMemoryException)
        {
            TextWarning ??= ex.Message;
        }
    }

    private void Image(PdfValue image, PdfReference? id, PdfMatrix matrix)
    {
        var bytes = Convert.FromBase64String(image.Base64!);
        var mask = graph.Get(image, "ImageMask")?.Boolean == true;
        Images.Add(new() { ImageNumber = Images.Count + 1, IsInline = id is null, PixelWidth = (int)graph.Number(image, "Width"), PixelHeight = (int)graph.Number(image, "Height"), BitsPerComponent = (int)graph.Number(image, "BitsPerComponent", mask ? 1 : 8), IsImageMask = mask, Corners = [matrix.Point(0, 0), matrix.Point(1, 0), matrix.Point(1, 1), matrix.Point(0, 1)], ImageObjects = id is null ? [] : [id], EncodedDataSha256 = Convert.ToHexString(SHA256.HashData(bytes)), InlineDataBase64 = id is null ? image.Base64 : null });
    }
}
