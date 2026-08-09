using System.Text;

namespace PZAdvancedServerManager.Core.Pz;

public enum TextDiffKind
{
    Unchanged,
    Added,
    Removed,
    Modified
}

public sealed record TextDiffSource(
    string Path,
    string EncodingName,
    long Bytes,
    int TotalLines,
    bool Truncated);

public sealed record TextDiffCell(
    int? LineNumber,
    string Text,
    TextDiffKind Kind,
    string Prefix,
    string Changed,
    string Suffix);

public sealed record TextDiffRow(
    int Index,
    TextDiffCell? Left,
    TextDiffCell? Right,
    int? ChangeIndex);

public sealed record TextConflictDiff(
    TextDiffSource Left,
    TextDiffSource Right,
    IReadOnlyList<TextDiffRow> Rows,
    int AddedLines,
    int RemovedLines,
    int ModifiedLines,
    int ChangeCount,
    bool UsedFallback)
{
    public int UnchangedLines => Rows.Count(row => row.ChangeIndex is null);
}

public sealed class TextConflictDiffService
{
    public const long MaximumFileBytes = 2L * 1024 * 1024;
    public const int MaximumLines = 12_000;
    private const int MaximumEditDistance = 1_200;
    private const int FallbackLookAhead = 48;
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lua", ".txt", ".json", ".xml", ".ini", ".cfg", ".conf", ".info", ".properties",
        ".csv", ".tsv", ".md", ".yml", ".yaml", ".toml", ".html", ".htm", ".css", ".js",
        ".ts", ".po", ".pot", ".sql", ".log", ".bat", ".cmd", ".ps1", ".sh"
    };

    public static bool IsSupportedPath(string path) => SupportedExtensions.Contains(Path.GetExtension(path));

    public TextConflictDiff Compare(string leftPath, string rightPath, bool ignoreWhitespace = false)
    {
        var left = ReadText(leftPath);
        var right = ReadText(rightPath);
        var operations = BuildOperations(left.Lines, right.Lines, ignoreWhitespace, out var usedFallback);
        var rows = BuildRows(operations, out var added, out var removed, out var modified, out var changes);
        return new TextConflictDiff(
            new TextDiffSource(leftPath, left.EncodingName, left.Bytes, left.TotalLines, left.Truncated),
            new TextDiffSource(rightPath, right.EncodingName, right.Bytes, right.TotalLines, right.Truncated),
            rows,
            added,
            removed,
            modified,
            changes,
            usedFallback);
    }

    private static TextFileContent ReadText(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The conflict source file no longer exists.", path);
        if (!IsSupportedPath(path)) throw new InvalidDataException("This file type is not supported by the text comparator.");
        var info = new FileInfo(path);
        if (info.Length > MaximumFileBytes) throw new InvalidDataException($"The file exceeds the {MaximumFileBytes / (1024 * 1024)} MiB comparison limit.");

        var bytes = File.ReadAllBytes(path);
        var (text, encodingName) = Decode(bytes);
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var allLines = normalized.Split('\n');
        var truncated = allLines.Length > MaximumLines;
        var lines = truncated ? allLines.Take(MaximumLines).ToArray() : allLines;
        return new TextFileContent(lines, encodingName, info.Length, allLines.Length, truncated);
    }

    private static (string Text, string EncodingName) Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe)
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 LE");
        if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff)
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 BE");
        if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf)
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), "UTF-8 BOM");
        if (bytes.Contains((byte)0)) throw new InvalidDataException("The file contains binary data and cannot be compared as text.");

        try
        {
            return (new UTF8Encoding(false, true).GetString(bytes), "UTF-8");
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.Latin1.GetString(bytes), "Latin-1");
        }
    }

    private static IReadOnlyList<LineOperation> BuildOperations(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right,
        bool ignoreWhitespace,
        out bool usedFallback)
    {
        var equals = ignoreWhitespace
            ? (Func<string, string, bool>)((first, second) => NormalizeWhitespace(first).Equals(NormalizeWhitespace(second), StringComparison.Ordinal))
            : (first, second) => first.Equals(second, StringComparison.Ordinal);
        var prefix = 0;
        while (prefix < left.Count && prefix < right.Count && equals(left[prefix], right[prefix])) prefix++;
        var suffix = 0;
        while (suffix < left.Count - prefix && suffix < right.Count - prefix
               && equals(left[left.Count - suffix - 1], right[right.Count - suffix - 1])) suffix++;

        var leftMiddle = left.Skip(prefix).Take(left.Count - prefix - suffix).ToArray();
        var rightMiddle = right.Skip(prefix).Take(right.Count - prefix - suffix).ToArray();
        var middle = TryMyers(leftMiddle, rightMiddle, equals);
        usedFallback = middle is null;
        middle ??= BuildFallback(leftMiddle, rightMiddle, equals);

        var result = new List<LineOperation>(left.Count + right.Count);
        for (var index = 0; index < prefix; index++) result.Add(new LineOperation(LineOperationKind.Equal, left[index]));
        result.AddRange(middle);
        for (var index = suffix; index > 0; index--) result.Add(new LineOperation(LineOperationKind.Equal, left[left.Count - index]));
        return result;
    }

    private static List<LineOperation>? TryMyers(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right,
        Func<string, string, bool> equals)
    {
        if (left.Count == 0) return right.Select(line => new LineOperation(LineOperationKind.Insert, line)).ToList();
        if (right.Count == 0) return left.Select(line => new LineOperation(LineOperationKind.Delete, line)).ToList();

        var offset = MaximumEditDistance + 1;
        var vector = new int[MaximumEditDistance * 2 + 3];
        vector[offset + 1] = 0;
        var trace = new List<int[]>(Math.Min(MaximumEditDistance + 1, left.Count + right.Count + 1));
        var foundDistance = -1;

        for (var distance = 0; distance <= MaximumEditDistance; distance++)
        {
            trace.Add((int[])vector.Clone());
            for (var diagonal = -distance; diagonal <= distance; diagonal += 2)
            {
                var vectorIndex = offset + diagonal;
                var x = diagonal == -distance || diagonal != distance && vector[vectorIndex - 1] < vector[vectorIndex + 1]
                    ? vector[vectorIndex + 1]
                    : vector[vectorIndex - 1] + 1;
                var y = x - diagonal;
                while (x < left.Count && y < right.Count && equals(left[x], right[y]))
                {
                    x++;
                    y++;
                }
                vector[vectorIndex] = x;
                if (x < left.Count || y < right.Count) continue;
                foundDistance = distance;
                break;
            }
            if (foundDistance >= 0) break;
        }

        if (foundDistance < 0) return null;
        var operations = new List<LineOperation>(left.Count + right.Count);
        var currentX = left.Count;
        var currentY = right.Count;
        for (var distance = foundDistance; distance > 0; distance--)
        {
            var previous = trace[distance];
            var diagonal = currentX - currentY;
            var vectorIndex = offset + diagonal;
            var previousDiagonal = diagonal == -distance || diagonal != distance && previous[vectorIndex - 1] < previous[vectorIndex + 1]
                ? diagonal + 1
                : diagonal - 1;
            var previousX = previous[offset + previousDiagonal];
            var previousY = previousX - previousDiagonal;
            while (currentX > previousX && currentY > previousY)
            {
                operations.Add(new LineOperation(LineOperationKind.Equal, left[currentX - 1]));
                currentX--;
                currentY--;
            }
            if (currentX == previousX)
            {
                operations.Add(new LineOperation(LineOperationKind.Insert, right[currentY - 1]));
                currentY--;
            }
            else
            {
                operations.Add(new LineOperation(LineOperationKind.Delete, left[currentX - 1]));
                currentX--;
            }
        }
        while (currentX > 0 && currentY > 0)
        {
            operations.Add(new LineOperation(LineOperationKind.Equal, left[currentX - 1]));
            currentX--;
            currentY--;
        }
        while (currentX > 0) operations.Add(new LineOperation(LineOperationKind.Delete, left[--currentX]));
        while (currentY > 0) operations.Add(new LineOperation(LineOperationKind.Insert, right[--currentY]));
        operations.Reverse();
        return operations;
    }

    private static List<LineOperation> BuildFallback(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right,
        Func<string, string, bool> equals)
    {
        var result = new List<LineOperation>(left.Count + right.Count);
        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < left.Count && rightIndex < right.Count)
        {
            if (equals(left[leftIndex], right[rightIndex]))
            {
                result.Add(new LineOperation(LineOperationKind.Equal, left[leftIndex]));
                leftIndex++;
                rightIndex++;
                continue;
            }

            var match = FindNearbyMatch(left, right, leftIndex, rightIndex, equals);
            if (match is null)
            {
                result.Add(new LineOperation(LineOperationKind.Delete, left[leftIndex++]));
                result.Add(new LineOperation(LineOperationKind.Insert, right[rightIndex++]));
                continue;
            }
            for (var index = 0; index < match.Value.LeftOffset; index++) result.Add(new LineOperation(LineOperationKind.Delete, left[leftIndex++]));
            for (var index = 0; index < match.Value.RightOffset; index++) result.Add(new LineOperation(LineOperationKind.Insert, right[rightIndex++]));
        }
        while (leftIndex < left.Count) result.Add(new LineOperation(LineOperationKind.Delete, left[leftIndex++]));
        while (rightIndex < right.Count) result.Add(new LineOperation(LineOperationKind.Insert, right[rightIndex++]));
        return result;
    }

    private static (int LeftOffset, int RightOffset)? FindNearbyMatch(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right,
        int leftIndex,
        int rightIndex,
        Func<string, string, bool> equals)
    {
        (int LeftOffset, int RightOffset)? best = null;
        for (var leftOffset = 0; leftOffset <= FallbackLookAhead && leftIndex + leftOffset < left.Count; leftOffset++)
        {
            for (var rightOffset = 0; rightOffset <= FallbackLookAhead && rightIndex + rightOffset < right.Count; rightOffset++)
            {
                if (leftOffset == 0 && rightOffset == 0 || !equals(left[leftIndex + leftOffset], right[rightIndex + rightOffset])) continue;
                if (best is null || leftOffset + rightOffset < best.Value.LeftOffset + best.Value.RightOffset)
                    best = (leftOffset, rightOffset);
            }
        }
        return best;
    }

    private static IReadOnlyList<TextDiffRow> BuildRows(
        IReadOnlyList<LineOperation> operations,
        out int added,
        out int removed,
        out int modified,
        out int changeCount)
    {
        var rows = new List<TextDiffRow>();
        var leftLine = 1;
        var rightLine = 1;
        added = 0;
        removed = 0;
        modified = 0;
        changeCount = 0;
        var operationIndex = 0;
        while (operationIndex < operations.Count)
        {
            if (operations[operationIndex].Kind == LineOperationKind.Equal)
            {
                var text = operations[operationIndex++].Text;
                rows.Add(new TextDiffRow(rows.Count, Cell(leftLine++, text, TextDiffKind.Unchanged), Cell(rightLine++, text, TextDiffKind.Unchanged), null));
                continue;
            }

            var deleted = new List<string>();
            var inserted = new List<string>();
            while (operationIndex < operations.Count && operations[operationIndex].Kind != LineOperationKind.Equal)
            {
                var operation = operations[operationIndex++];
                if (operation.Kind == LineOperationKind.Delete) deleted.Add(operation.Text);
                else inserted.Add(operation.Text);
            }
            changeCount++;
            var blockSize = Math.Max(deleted.Count, inserted.Count);
            for (var index = 0; index < blockSize; index++)
            {
                TextDiffCell? leftCell = null;
                TextDiffCell? rightCell = null;
                if (index < deleted.Count && index < inserted.Count)
                {
                    (leftCell, rightCell) = ModifiedCells(leftLine++, deleted[index], rightLine++, inserted[index]);
                    modified++;
                }
                else if (index < deleted.Count)
                {
                    leftCell = Cell(leftLine++, deleted[index], TextDiffKind.Removed);
                    removed++;
                }
                else
                {
                    rightCell = Cell(rightLine++, inserted[index], TextDiffKind.Added);
                    added++;
                }
                rows.Add(new TextDiffRow(rows.Count, leftCell, rightCell, changeCount));
            }
        }
        return rows;
    }

    private static (TextDiffCell Left, TextDiffCell Right) ModifiedCells(int leftLine, string left, int rightLine, string right)
    {
        var prefixLength = 0;
        while (prefixLength < left.Length && prefixLength < right.Length && left[prefixLength] == right[prefixLength]) prefixLength++;
        var suffixLength = 0;
        while (suffixLength < left.Length - prefixLength && suffixLength < right.Length - prefixLength
               && left[left.Length - suffixLength - 1] == right[right.Length - suffixLength - 1]) suffixLength++;
        var leftChangedLength = left.Length - prefixLength - suffixLength;
        var rightChangedLength = right.Length - prefixLength - suffixLength;
        return (
            new TextDiffCell(leftLine, left, TextDiffKind.Modified, left[..prefixLength], left.Substring(prefixLength, leftChangedLength), suffixLength == 0 ? string.Empty : left[^suffixLength..]),
            new TextDiffCell(rightLine, right, TextDiffKind.Modified, right[..prefixLength], right.Substring(prefixLength, rightChangedLength), suffixLength == 0 ? string.Empty : right[^suffixLength..]));
    }

    private static TextDiffCell Cell(int line, string text, TextDiffKind kind) => new(line, text, kind, string.Empty, text, string.Empty);

    private static string NormalizeWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingWhitespace = false;
        foreach (var character in value.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                pendingWhitespace = true;
                continue;
            }
            if (pendingWhitespace && builder.Length > 0) builder.Append(' ');
            pendingWhitespace = false;
            builder.Append(character);
        }
        return builder.ToString();
    }

    private enum LineOperationKind { Equal, Insert, Delete }
    private sealed record LineOperation(LineOperationKind Kind, string Text);
    private sealed record TextFileContent(IReadOnlyList<string> Lines, string EncodingName, long Bytes, int TotalLines, bool Truncated);
}
