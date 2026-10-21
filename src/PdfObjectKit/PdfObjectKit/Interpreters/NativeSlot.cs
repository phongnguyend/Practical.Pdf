using PdfObjectKit.Fonts;
using PdfObjectKit.Models;
using PdfObjectKit.Parsers;
using System.Security.Cryptography;

namespace PdfObjectKit.Interpreters;

internal sealed record NativeSlot(int Operation, int Token, int Start, string Text, byte[] Bytes, PdfNativeFont Font, List<NativeGlyph> Glyphs, double FontSize, PdfReference Stream);
