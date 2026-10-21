using PdfObjectKit.Fonts;
using PdfObjectKit.Primitives;
using PdfObjectKit.Writers;
using PdfObjectKit.Models;
using System.Text;
using PdfObjectKit;
using PdfObjectKit.Editors;
using static PdfObjectKit.Primitives.PdfPrimitives;

// Test data construction only; not a text-only document API in the library.
internal sealed record FixturePage(string Text = "", double Width = 595, double Height = 842,
    double X = 20, double Y = 100, double FontSize = 12);

internal static class PdfFixture
{
    public static byte[] Create(byte[]? fontBytes, params FixturePage[] sources)
    {
        var document = new PdfObjectDocument { Trailer = Dict(("Root", Ref(new(1)))) };
        PdfValue Add(PdfValue value)
        {
            var id = new PdfReference(document.Objects.Count + 1);
            document.Objects.Add(new(id, value));
            return Ref(id);
        }

        Add(Dict(("Type", Name("Catalog")), ("Pages", Ref(new(2)))));
        var pages = Dict(("Type", Name("Pages")), ("Count", Num(sources.Length)), ("Kids", Array()));
        Add(pages);
        var embedded = fontBytes is null ? null : new PdfTrueTypeFont(fontBytes).Embed(document, string.Concat(sources.Select(p => p.Text)));
        var font = embedded?.Font ?? Add(Dict(("Type", Name("Font")), ("Subtype", Name("Type1")),
            ("BaseFont", Name("Helvetica")), ("Encoding", Name("WinAnsiEncoding"))));
        foreach (var source in sources)
        {
            var bytes = embedded is null ? Encoding.ASCII.GetBytes(source.Text) : embedded.Encode(source.Text).Bytes;
            var content = source.Text.Length == 0 ? "" :
                $"BT /F1 {Syntax(Num(source.FontSize))} Tf 1 0 0 1 {Syntax(Num(source.X))} {Syntax(Num(source.Y))} Tm {Syntax(Str(bytes))} Tj ET\n";
            var stream = Add(Stream(Encoding.ASCII.GetBytes(content)));
            var page = Add(Dict(("Type", Name("Page")), ("Parent", Ref(new(2))),
                ("MediaBox", Array(Num(0), Num(0), Num(source.Width), Num(source.Height))),
                ("Resources", Dict(("Font", Dict(("F1", font))))), ("Contents", stream)));
            pages.Entries!["Kids"].Items!.Add(page);
        }

        return PdfWriter.Write(document);
    }
}
