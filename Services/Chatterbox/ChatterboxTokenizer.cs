using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.Tokenizers;

namespace Babel.Player.Services.Chatterbox;

internal sealed class ChatterboxTokenizer
{
    private readonly BpeTokenizer _tokenizer;
    private readonly SpecialTokenScanner _specials;

    private ChatterboxTokenizer(BpeTokenizer tokenizer, SpecialTokenScanner specials)
    {
        _tokenizer = tokenizer;
        _specials = specials;
    }

    public static async Task<ChatterboxTokenizer> LoadAsync(string tokenizerPath, CancellationToken cancellationToken = default)
    {
        var tokenizerText = await File.ReadAllTextAsync(tokenizerPath, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(tokenizerText);
        var root = document.RootElement;
        var model = root.GetProperty("model");
        var vocabulary = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in model.GetProperty("vocab").EnumerateObject())
            vocabulary[entry.Name] = entry.Value.GetInt32();

        var merges = new List<string>();
        foreach (var merge in model.GetProperty("merges").EnumerateArray())
        {
            if (merge.ValueKind is JsonValueKind.String)
            {
                merges.Add(merge.GetString() ?? string.Empty);
                continue;
            }

            if (merge.ValueKind is JsonValueKind.Array)
            {
                var parts = merge.EnumerateArray()
                    .Select(part => part.GetString() ?? string.Empty)
                    .ToArray();
                if (parts.Length == 2)
                    merges.Add($"{parts[0]} {parts[1]}");
            }
        }

        var specialTokens = ReadSpecialTokens(root);
        var options = new BpeOptions(vocabulary)
        {
            Merges = merges,
            SpecialTokens = specialTokens,
            UnknownToken = "[UNK]",
            ByteLevel = false,
        };
        return new ChatterboxTokenizer(BpeTokenizer.Create(options), new SpecialTokenScanner(specialTokens));
    }

    public long[] Encode(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        // Grapheme tokenizer: NFC, then spaces become the dedicated [SPACE] token, matching
        // onnx-community/chatterbox-multilingual-ONNX tokenizer.json (Replace " " -> "[SPACE]").
        var normalized = text.Trim()
            .Normalize(System.Text.NormalizationForm.FormC)
            .Replace(" ", "[SPACE]", StringComparison.Ordinal);
        return EncodeGraphemes(normalized);
    }

    private long[] EncodeGraphemes(string text)
    {
        var ids = new List<long>();
        int index = 0;
        while (index < text.Length)
        {
            if (_specials.TryMatch(text, index, out int specialId, out int specialLength))
            {
                ids.Add(specialId);
                index += specialLength;
                continue;
            }

            int nextSpecial = _specials.FindNext(text, index + 1);
            if (nextSpecial < 0)
                nextSpecial = text.Length;

            var piece = text[index..nextSpecial];
            ids.AddRange(_tokenizer.EncodeToIds(piece, considerPreTokenization: false, considerNormalization: false)
                .Select(static tokenId => (long)tokenId));
            index = nextSpecial;
        }

        return ids.ToArray();
    }

    private static Dictionary<string, int> ReadSpecialTokens(JsonElement root)
    {
        var tokens = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!root.TryGetProperty("added_tokens", out var addedTokens) ||
            addedTokens.ValueKind is not JsonValueKind.Array)
        {
            return tokens;
        }

        foreach (var token in addedTokens.EnumerateArray())
        {
            if (!token.TryGetProperty("content", out var contentElement) ||
                !token.TryGetProperty("id", out var idElement) ||
                contentElement.ValueKind is not JsonValueKind.String ||
                idElement.ValueKind is not JsonValueKind.Number)
            {
                continue;
            }

            var content = contentElement.GetString();
            if (!string.IsNullOrWhiteSpace(content) && idElement.TryGetInt32(out int id))
                tokens[content] = id;
        }

        return tokens;
    }

    private sealed class SpecialTokenScanner
    {
        private readonly (string Token, int Id)[] _tokensByLength;

        public SpecialTokenScanner(IReadOnlyDictionary<string, int> specialTokens)
        {
            _tokensByLength = specialTokens
                .Select(pair => (pair.Key, pair.Value))
                .OrderByDescending(pair => pair.Key.Length)
                .ToArray();
        }

        public bool TryMatch(string text, int index, out int id, out int length)
        {
            foreach (var (token, tokenId) in _tokensByLength)
            {
                if (token.Length > 0 &&
                    index + token.Length <= text.Length &&
                    text.AsSpan(index, token.Length).SequenceEqual(token.AsSpan()))
                {
                    id = tokenId;
                    length = token.Length;
                    return true;
                }
            }

            id = 0;
            length = 0;
            return false;
        }

        public int FindNext(string text, int start)
        {
            int best = -1;
            foreach (var (token, _) in _tokensByLength)
            {
                if (token.Length == 0)
                    continue;
                int found = text.IndexOf(token, start, StringComparison.Ordinal);
                if (found >= 0 && (best < 0 || found < best))
                    best = found;
            }

            return best;
        }
    }
}
