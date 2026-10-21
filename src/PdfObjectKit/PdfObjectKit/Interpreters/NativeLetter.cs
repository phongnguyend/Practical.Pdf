using PdfObjectKit.Models;
using PdfObjectKit.Parsers;
using System.Security.Cryptography;

namespace PdfObjectKit.Interpreters;

internal sealed record NativeLetter(string Value, PdfImagePoint StartBaseLine, double FontSize);
