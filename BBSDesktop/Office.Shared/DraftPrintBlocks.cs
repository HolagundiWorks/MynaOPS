// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Text;
using System.Text.Json.Nodes;

namespace Office.Shared;

/// <summary>A print block named in Tren and stored in the drawing's project text.</summary>
public sealed record DraftPrintBlock(string Drawing, string Name, string Description, string Handle);

/// <summary>
/// Reads print blocks from drawing bytes. Tren stores them under the project key
/// <c>printblocks1</c> inside the <c>AAD_PROJECT</c> payload. A DWG that does not carry that text yields nothing.
/// </summary>
public static class DraftPrintBlocks
{
    const int MaxItems = 256;
    const int MaxText = 200;

    public static IReadOnlyList<DraftPrintBlock> Read(byte[]? bytes, string drawing = "")
    {
        if (bytes is null || bytes.Length == 0) return Array.Empty<DraftPrintBlock>();
        // The payload is ASCII JSON. A binary drawing without that text has no blocks to list.
        var text = Encoding.UTF8.GetString(bytes);
        var key = text.IndexOf("printblocks1", StringComparison.Ordinal);
        if (key < 0) return Array.Empty<DraftPrintBlock>();
        var colon = text.IndexOf(':', key + "printblocks1".Length);
        if (colon < 0 || colon - key > 80) return Array.Empty<DraftPrintBlock>();
        var quote = text.IndexOf('"', colon + 1);
        if (quote < 0 || quote - colon > 16) return Array.Empty<DraftPrintBlock>();
        var raw = ReadJsonString(text, quote);
        if (string.IsNullOrEmpty(raw)) return Array.Empty<DraftPrintBlock>();
        JsonNode? node;
        try { node = JsonNode.Parse(raw); }
        catch (System.Text.Json.JsonException) { return Array.Empty<DraftPrintBlock>(); }
        if (node?["items"] is not JsonArray items) return Array.Empty<DraftPrintBlock>();
        var list = new List<DraftPrintBlock>();
        foreach (var item in items)
        {
            if (list.Count >= MaxItems) break;
            if (item is not JsonObject o) continue;
            var name = Clip(Text(o["name"]));
            if (name.Length == 0) continue;
            list.Add(new DraftPrintBlock(drawing, name, Clip(Text(o["description"])), Clip(Text(o["handle"]))));
        }
        return list;
    }

    static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    static string Clip(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var trimmed = value.Trim();
        return trimmed.Length <= MaxText ? trimmed : trimmed[..MaxText];
    }

    static string? ReadJsonString(string text, int openQuote)
    {
        var sb = new StringBuilder();
        for (var i = openQuote + 1; i < text.Length; i++)
        {
            if (sb.Length > 2_000_000) return null;
            var c = text[i];
            if (c == '"') return sb.ToString();
            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }
            if (i + 1 >= text.Length) return null;
            var n = text[++i];
            switch (n)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'u' when i + 4 < text.Length && int.TryParse(text.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out var cp) && cp <= 0xFFFF:
                    sb.Append((char)cp);
                    i += 4;
                    break;
                default:
                    sb.Append(n);
                    break;
            }
        }
        return null;
    }
}
