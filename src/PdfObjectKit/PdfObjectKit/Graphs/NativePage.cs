using PdfObjectKit.Models;
using PdfObjectKit.Parsers;
using System.Globalization;
using System.Text;

namespace PdfObjectKit.Graphs;

internal sealed record NativePage(int Number, PdfValue Dictionary, PdfValue Resources, double Width, double Height, List<(PdfReference Id, PdfValue Value)> Streams);
