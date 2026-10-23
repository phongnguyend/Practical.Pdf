using System.Text;
using PdfObjectKit.Converters;
using PdfObjectKit.Editors;
using PdfObjectKit.Extractors;
using PdfObjectKit.Graphs;
using PdfObjectKit.Interpreters;
using PdfObjectKit.Models;
using PdfObjectKit.Readers;
using PdfObjectKit.Writers;
using static PdfObjectKit.Primitives.PdfPrimitives;

internal static class TextMovementTests
{
    public static void Run()
    {
        var checks = 0;
        void Check(bool valid, string message)
        {
            if (!valid)
            {
                throw new Exception(message);
            }
            checks++;
        }
        foreach (var (form, count) in new[] { (false, 2), (true, 2), (true, 3) })
        {
            var document = PdfReader.Read(PdfFixture.Create(null, new FixturePage("Original")));
            var page = new PdfGraph(document).Pages()[0];
            const string content = "BT /F1 12 Tf 2 Tc 3 Tw 20 30 Td [(First ) -50 (second)] TJ ( untouched) Tj ET";
            if (form)
            {
                var value = Stream(Encoding.ASCII.GetBytes(content));
                value.Entries!["Subtype"] = Name("Form");
                value.Entries["BBox"] = Array(Num(0), Num(0), Num(500), Num(500));
                value.Entries["Resources"] = page.Resources;
                value.Entries["Matrix"] = Array(Num(2), Num(0), Num(0), Num(2), Num(5), Num(10));
                var id = new PdfReference(document.Objects.Max(item => item.Id.ObjectNumber) + 1);
                document.Objects.Add(new(id, value));
                page.Dictionary.Entries!["Resources"] = Dict(("XObject", Dict(("Shared", Ref(id)))));
                page.Streams[0].Value.Base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes("q /Shared Do Q q 1 0 0 1 100 200 cm /Shared Do Q"));
            }
            else
            {
                page.Streams[0].Value.Base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes("q 2 0 0 2 5 10 cm " + content + " Q"));
            }
            List<NativeSlot> Slots(PdfObjectDocument doc)
            {
                var graph = new PdfGraph(doc);
                return new PdfPageInterpreter(graph, graph.Pages()[0]).Slots;
            }
            var before = Slots(document);
            var runs = PdfParagraphExtractor.Extract(document, 1).SelectMany(p => p.Lines).SelectMany(line => line.Runs).Where(run => run.FormOccurrenceId == before[0].FormOccurrenceId).OrderBy(run => run.OperatorIndex).ThenBy(run => run.OperandIndex).Take(count).ToList();
            Check(runs.Count == count, "Fixture has multiple text runs");
            var deleted = PdfConverter.FromJson(PdfConverter.ToJson(document));
            PdfTextEditor.DeleteText(deleted, 1, runs);
            var remaining = Slots(PdfReader.Read(PdfWriter.Write(deleted)));
            Check(remaining.Select(slot => slot.Text).SequenceEqual(before.Skip(count).Select(slot => slot.Text)), "Batch deletion removes all selected runs only");
            Check(remaining.Zip(before.Skip(count)).All(pair => Math.Abs(pair.First.BaselineStart.X - pair.Second.BaselineStart.X) < .001 && Math.Abs(pair.First.BaselineStart.Y - pair.Second.BaselineStart.Y) < .001), "Batch deletion preserves following text and shared Form placements");
            PdfTextEditor.MoveText(document, 1, runs, 15, 20);
            var after = Slots(PdfReader.Read(PdfWriter.Write(document)));
            Check(before.Select(s => s.Text).SequenceEqual(after.Select(s => s.Text)), "Moving preserves all text");
            for (var i = 0; i < before.Count; i++)
            {
                Check(Math.Abs(after[i].BaselineStart.X - before[i].BaselineStart.X - (i < count ? 15 : 0)) < .001
                    && Math.Abs(after[i].BaselineStart.Y - before[i].BaselineStart.Y - (i < count ? 20 : 0)) < .001, "Only selected runs move, including shared Forms");
                Check(after[i].FontSize == before[i].FontSize && after[i].CharacterSpacing == before[i].CharacterSpacing && after[i].WordSpacing == before[i].WordSpacing, "Moving preserves text formatting");
            }
            if (count == 3)
            {
                // Moving an exclusive Form invocation preserves the source text stream itself.
                Check(after[0].Stream == before[0].Stream, "Exclusive Form movement preserves the source text stream");
                continue;
            }
            var json = PdfConverter.ToJson(document);
            try
            {
                PdfTextEditor.MoveText(document, 1, runs, 10, 10);
                throw new Exception("Stale text runs accepted");
            }
            catch (InvalidDataException)
            {
                Check(PdfConverter.ToJson(document) == json, "Failed text movement is atomic");
            }
        }
        Console.WriteLine($"Passed {checks} text movement checks.");
    }
}
