using Practical.PdfJson;

// Native semantic assertions plus independent PDFsharp checks of the generated file.
internal sealed class NativeTestDocument : IDisposable
{
    private readonly PdfSharp.Pdf.PdfDocument independent;
    private readonly PdfGraph graph;
    private readonly List<NativePage> pages;
    private NativeTestDocument(byte[] bytes)
    {
        independent = PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(bytes), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        graph = new(PdfNativeParser.Parse(bytes));
        pages = graph.Pages();
        if (pages.Count != independent.PageCount) throw new Exception("Native and independent page counts differ.");
    }
    public static NativeTestDocument Open(byte[] bytes) => new(bytes);
    public int NumberOfPages => independent.PageCount;
    public PdfSharp.Pdf.PdfDocumentInformation Information => independent.Info;
    public NativeTestPage GetPage(int number)
    {
        var parsed = new PdfPageInterpreter(graph, pages[number - 1]);
        if (parsed.TextWarning is not null || parsed.ImageWarning is not null)
            throw new Exception(parsed.TextWarning ?? parsed.ImageWarning);
        return new(parsed);
    }
    public void Dispose() => independent.Dispose();
}
internal sealed class NativeTestPage(PdfPageInterpreter parsed)
{
    public string Text => parsed.Text;
    public List<NativeLetter> Letters => parsed.Letters;
    public int NumberOfImages => parsed.Images.Count;
}
