using PdfObjectKit.Models;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfObjectKit.Parsers;

// Byte-oriented lexer: strings, names and streams must never pass through UTF-8.
internal sealed class PdfSyntaxReader(byte[] bytes, int position = 0)
{
    public int Position { get; set; } = position;

    public byte[] Bytes => bytes;

    public static bool White(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;
    private static bool Delimiter(byte b) => White(b) || "()<>[]{}/%".Contains((char)b);
    public void Skip()
    {
        while (Position < bytes.Length)
        {
            if (White(bytes[Position]))
            {
                Position++;
                continue;
            }

            if (bytes[Position] != '%')
            {
                break;
            }

            while (Position < bytes.Length && bytes[Position] is not (10 or 13))
            {
                Position++;
            }
        }
    }

    public string Word()
    {
        Skip();
        var start = Position;
        while (Position < bytes.Length && !Delimiter(bytes[Position]))
        {
            Position++;
        }

        if (start == Position)
        {
            throw Error("Expected a token");
        }

        return Encoding.ASCII.GetString(bytes, start, Position - start);
    }

    public bool Take(string keyword)
    {
        Skip();
        if (Position + keyword.Length > bytes.Length)
        {
            return false;
        }

        for (var i = 0; i < keyword.Length; i++)
        {
            if (bytes[Position + i] != keyword[i])
            {
                return false;
            }
        }

        var end = Position + keyword.Length;
        if (end < bytes.Length && !Delimiter(bytes[end]))
        {
            return false;
        }

        Position = end;
        return true;
    }

    public void Expect(string keyword)
    {
        if (!Take(keyword))
        {
            throw Error($"Expected '{keyword}'");
        }
    }

    public long Integer()
    {
        if (!long.TryParse(Word(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
        {
            throw Error("Expected integer");
        }

        return value;
    }

    public PdfValue Value(int depth = 0)
    {
        if (depth > 100)
        {
            throw Error("Object nesting exceeds 100 levels");
        }

        Skip();
        if (Position >= bytes.Length)
        {
            throw Error("Unexpected end of input");
        }

        var first = bytes[Position++];
        if (first == '/')
        {
            var name = new List<byte>();
            while (Position < bytes.Length && !Delimiter(bytes[Position]))
            {
                var b = bytes[Position++];
                if (b == '#')
                {
                    b = (byte)((Hex(Read()) << 4) | Hex(Read()));
                }

                if (b == 0)
                {
                    throw Error("Null byte in PDF name");
                }

                name.Add(b);
            }

            return new()
            {
                Type = "name",
                Name = Encoding.Latin1.GetString(name.ToArray())
            };
        }

        if (first == '(')
        {
            return String(Literal());
        }

        if (first == '[')
        {
            var items = new List<PdfValue>();
            while (true)
            {
                Skip();
                if (Position >= bytes.Length)
                {
                    throw Error("Unterminated array");
                }

                if (bytes[Position] == ']')
                {
                    Position++;
                    break;
                }

                items.Add(Value(depth + 1));
            }

            return new()
            {
                Type = "array",
                Items = items
            };
        }

        if (first == '<')
        {
            if (Position < bytes.Length && bytes[Position] == '<')
            {
                Position++;
                var entries = new Dictionary<string, PdfValue>(StringComparer.Ordinal);
                while (true)
                {
                    Skip();
                    if (Position + 1 < bytes.Length && bytes[Position] == '>' && bytes[Position + 1] == '>')
                    {
                        Position += 2;
                        break;
                    }

                    var key = Value(depth + 1);
                    if (key.Type != "name")
                    {
                        throw Error("Dictionary keys must be names");
                    }

                    if (!entries.TryAdd(key.Name!, Value(depth + 1)))
                    {
                        throw Error("Duplicate dictionary key");
                    }
                }

                return new()
                {
                    Type = "dictionary",
                    Entries = entries
                };
            }

            var data = new List<byte>();
            var high = -1;
            while (true)
            {
                var b = Read();
                if (b == '>')
                {
                    break;
                }

                if (White(b))
                {
                    continue;
                }

                var digit = Hex(b);
                if (high < 0)
                {
                    high = digit;
                }
                else
                {
                    data.Add((byte)((high << 4) | digit));
                    high = -1;
                }
            }

            if (high >= 0)
            {
                data.Add((byte)(high << 4));
            }

            return String(data.ToArray());
        }

        Position--;
        var word = Word();
        if (word == "null")
        {
            return new()
            {
                Type = "null"
            };
        }

        if (word is "true" or "false")
        {
            return new()
            {
                Type = "boolean",
                Boolean = word == "true"
            };
        }

        if (!Regex.IsMatch(word, @"\A[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)\z"))
        {
            throw Error($"Unsupported token '{word}'");
        }

        var saved = Position;
        if (long.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var objectNumber))
        {
            Skip();
            if (Position < bytes.Length && bytes[Position] is >= (byte)'0' and <= (byte)'9')
            {
                var generation = Word();
                if (int.TryParse(generation, NumberStyles.None, CultureInfo.InvariantCulture, out var gen) && Take("R"))
                {
                    if (objectNumber <= 0 || gen > 65535)
                    {
                        throw Error("Invalid indirect reference");
                    }

                    return new()
                    {
                        Type = "reference",
                        Reference = new(objectNumber, gen)
                    };
                }
            }
        }

        Position = saved;
        return new()
        {
            Type = "number",
            Number = word
        };
    }

    private byte[] Literal()
    {
        var data = new List<byte>();
        var nesting = 1;
        while (nesting > 0)
        {
            var b = Read();
            if (b == '\\')
            {
                b = Read();
                if (b == 13)
                {
                    if (Position < bytes.Length && bytes[Position] == 10)
                    {
                        Position++;
                    }

                    continue;
                }

                if (b == 10)
                {
                    continue;
                }

                if (b is >= (byte)'0' and <= (byte)'7')
                {
                    var value = b - '0';
                    for (var i = 0; i < 2 && Position < bytes.Length && bytes[Position] is >= (byte)'0' and <= (byte)'7'; i++)
                    {
                        value = value * 8 + bytes[Position++] - '0';
                    }

                    data.Add((byte)value);
                    continue;
                }

                data.Add(b switch
                {
                    (byte)'n' => 10,
                    (byte)'r' => 13,
                    (byte)'t' => 9,
                    (byte)'b' => 8,
                    (byte)'f' => 12,
                    _ => b
                });
                continue;
            }

            if (b == '(')
            {
                nesting++;
            }

            if (b == ')')
            {
                nesting--;
            }

            if (b == ')' && nesting == 0)
            {
                break;
            }

            if (b == 13)
            {
                if (Position < bytes.Length && bytes[Position] == 10)
                {
                    Position++;
                }

                b = 10;
            }

            data.Add(b);
        }

        return data.ToArray();
    }

    private byte Read() => Position < bytes.Length ? bytes[Position++] : throw Error("Unexpected end of input");
    private int Hex(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        _ => throw Error("Invalid hex digit")};
    private static PdfValue String(byte[] data) => new()
    {
        Type = "string",
        Base64 = Convert.ToBase64String(data)
    };
    public InvalidDataException Error(string message) => new($"{message} at byte {Position}.");
}
