using System.Globalization;
using System.Text;

namespace Mirelia.Query.Parsing;

internal enum TokenType
{
    Ident,
    String,
    Number,
    UnitNumber, // digits immediately followed by a unit suffix, e.g. "30d", "3m"
    Date,
    AtNow,
    Operator, // = != < <= > >= =~ !~ ^= $= ~=
    Plus,
    Minus,
    Bang,
    AndAnd,
    OrOr,
    LParen,
    RParen,
    Comma,
    End,
}

internal readonly record struct Token(TokenType Type, string Text, int Position)
{
    public decimal Number => decimal.Parse(Text, CultureInfo.InvariantCulture);
}

/// <summary>Tokenizer for the query DSL. Keywords are resolved by the parser.</summary>
internal static class Lexer
{
    internal static List<Token> Tokenize(string input)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < input.Length)
        {
            char c = input[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            int start = i;
            switch (c)
            {
                case '(':
                    tokens.Add(new Token(TokenType.LParen, "(", start));
                    i++;
                    continue;
                case ')':
                    tokens.Add(new Token(TokenType.RParen, ")", start));
                    i++;
                    continue;
                case ',':
                    tokens.Add(new Token(TokenType.Comma, ",", start));
                    i++;
                    continue;
                case '+':
                    tokens.Add(new Token(TokenType.Plus, "+", start));
                    i++;
                    continue;
                case '-':
                    tokens.Add(new Token(TokenType.Minus, "-", start));
                    i++;
                    continue;
                case '"':
                    tokens.Add(ReadString(input, ref i));
                    continue;
                case '@':
                    tokens.Add(ReadAtWord(input, ref i));
                    continue;
            }

            if (TryReadOperator(input, ref i, out Token op))
            {
                tokens.Add(op);
                continue;
            }

            if (char.IsAsciiDigit(c))
            {
                tokens.Add(ReadNumberOrDate(input, ref i));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                while (i < input.Length && (char.IsLetterOrDigit(input[i]) || input[i] == '_'))
                {
                    i++;
                }

                tokens.Add(new Token(TokenType.Ident, input[start..i], start));
                continue;
            }

            throw new QueryParseException($"Unexpected character '{c}'", start);
        }

        tokens.Add(new Token(TokenType.End, string.Empty, input.Length));
        return tokens;
    }

    private static bool TryReadOperator(string input, ref int i, out Token token)
    {
        int start = i;
        string rest = input[i..];
        foreach (string op in (string[])["<=", ">=", "!=", "=~", "!~", "^=", "$=", "~=", "<", ">", "="])
        {
            if (rest.StartsWith(op, StringComparison.Ordinal))
            {
                i += op.Length;
                token = new Token(TokenType.Operator, op, start);
                return true;
            }
        }

        if (rest.StartsWith("&&", StringComparison.Ordinal))
        {
            i += 2;
            token = new Token(TokenType.AndAnd, "&&", start);
            return true;
        }

        if (rest.StartsWith("||", StringComparison.Ordinal))
        {
            i += 2;
            token = new Token(TokenType.OrOr, "||", start);
            return true;
        }

        if (rest.StartsWith("!", StringComparison.Ordinal))
        {
            i += 1;
            token = new Token(TokenType.Bang, "!", start);
            return true;
        }

        token = default;
        return false;
    }

    private static Token ReadString(string input, ref int i)
    {
        int start = i;
        i++; // opening quote
        var sb = new StringBuilder();
        while (i < input.Length)
        {
            char c = input[i];
            if (c == '\\' && i + 1 < input.Length && (input[i + 1] == '"' || input[i + 1] == '\\'))
            {
                sb.Append(input[i + 1]);
                i += 2;
                continue;
            }

            if (c == '"')
            {
                i++;
                return new Token(TokenType.String, sb.ToString(), start);
            }

            sb.Append(c);
            i++;
        }

        throw new QueryParseException("Unterminated string literal", start);
    }

    private static Token ReadAtWord(string input, ref int i)
    {
        int start = i;
        i++; // '@'
        int wordStart = i;
        while (i < input.Length && char.IsLetter(input[i]))
        {
            i++;
        }

        string word = input[wordStart..i];
        return word.Equals("now", StringComparison.OrdinalIgnoreCase)
            ? new Token(TokenType.AtNow, "@now", start)
            : throw new QueryParseException($"Unknown token '@{word}'", start);
    }

    private static Token ReadNumberOrDate(string input, ref int i)
    {
        int start = i;

        // A date literal starts as four digits followed by '-'; disambiguate
        // before ordinary number lexing since the DSL has no arithmetic.
        if (TryReadDate(input, ref i, start, out Token date))
        {
            return date;
        }

        while (i < input.Length && char.IsAsciiDigit(input[i]))
        {
            i++;
        }

        if (i < input.Length && input[i] == '.' && i + 1 < input.Length && char.IsAsciiDigit(input[i + 1]))
        {
            i++;
            while (i < input.Length && char.IsAsciiDigit(input[i]))
            {
                i++;
            }
        }

        int numberEnd = i;
        while (i < input.Length && char.IsLetter(input[i]))
        {
            i++;
        }

        return i > numberEnd
            ? new Token(TokenType.UnitNumber, input[start..i], start)
            : new Token(TokenType.Number, input[start..numberEnd], start);
    }

    private static bool TryReadDate(string input, ref int i, int start, out Token token)
    {
        token = default;
        // YYYY-MM or YYYY-MM-DD, the latter with optional THH:MM[:SS].
        // (A bare YYYY lexes as a number; date-keyed fields interpret it.)
        if (start + 7 > input.Length
            || !AllDigits(input, start, 4)
            || input[start + 4] != '-'
            || !AllDigits(input, start + 5, 2))
        {
            return false;
        }

        int end = start + 7;
        if (end + 3 <= input.Length && input[end] == '-' && AllDigits(input, end + 1, 2))
        {
            end += 3;
            if (end + 6 <= input.Length && input[end] == 'T'
                && AllDigits(input, end + 1, 2) && input[end + 3] == ':' && AllDigits(input, end + 4, 2))
            {
                end += 6;
                if (end + 3 <= input.Length && input[end] == ':' && AllDigits(input, end + 1, 2))
                {
                    end += 3;
                }
            }
        }

        // "2025-081" and friends are malformed dates, not date-then-number.
        if (end < input.Length && char.IsAsciiDigit(input[end]))
        {
            throw new QueryParseException(
                $"Invalid date literal '{input[start..(end + 1)]}'", start);
        }

        string text = input[start..end];
        if (!DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out _))
        {
            throw new QueryParseException($"Invalid date literal '{text}'", start);
        }

        i = end;
        token = new Token(TokenType.Date, text, start);
        return true;
    }

    private static bool AllDigits(string input, int start, int count)
    {
        if (start + count > input.Length)
        {
            return false;
        }

        for (int j = start; j < start + count; j++)
        {
            if (!char.IsAsciiDigit(input[j]))
            {
                return false;
            }
        }

        return true;
    }
}
