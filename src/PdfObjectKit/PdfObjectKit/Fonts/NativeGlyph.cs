using PdfObjectKit.Models;
using PdfObjectKit.Parsers;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfObjectKit.Fonts;

internal sealed record NativeGlyph(string Text, byte[] Bytes, double Width);
