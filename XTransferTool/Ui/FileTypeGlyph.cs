using System;
using System.IO;
using Avalonia.Media;

namespace XTransferTool.Ui;

public static class FileTypeGlyph
{
    private enum Kind
    {
        Default,
        Pdf,
        Presentation,
        Word,
        Sheet,
        Image,
        Video,
        Audio,
        Text,
        Archive
    }

    public static (Geometry Geometry, IBrush Brush) CreateVisuals(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        var kind = Classify(ext);
        return kind switch
        {
            Kind.Pdf => (PdfGeometry, Hex("#E53935")),
            Kind.Presentation => (SlideGeometry, Hex("#E65100")),
            Kind.Word => (DocGeometry, Hex("#2B579A")),
            Kind.Sheet => (SheetGeometry, Hex("#217346")),
            Kind.Image => (ImageGeometry, Hex("#AB47BC")),
            Kind.Video => (VideoGeometry, Hex("#EC407A")),
            Kind.Audio => (AudioGeometry, Hex("#26A69A")),
            Kind.Text => (TextLinesGeometry, Hex("#78909C")),
            Kind.Archive => (ArchiveGeometry, Hex("#FFA726")),
            _ => (DocGeometry, Hex("#7C5CFF"))
        };
    }

    private static Kind Classify(string ext) => ext switch
    {
        ".pdf" => Kind.Pdf,
        ".ppt" or ".pptx" or ".key" or ".odp" => Kind.Presentation,
        ".doc" or ".docx" or ".odt" or ".rtf" => Kind.Word,
        ".xls" or ".xlsx" or ".ods" or ".csv" => Kind.Sheet,
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".tif" or ".tiff" or ".svg" or ".ico" or ".heic" => Kind.Image,
        ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm" or ".m4v" or ".flv" => Kind.Video,
        ".mp3" or ".wav" or ".flac" or ".aac" or ".m4a" or ".ogg" or ".wma" or ".opus" => Kind.Audio,
        ".txt" or ".md" or ".log" or ".json" or ".xml" or ".yaml" or ".yml" or ".ini" or ".cfg" or ".toml" or ".env" => Kind.Text,
        ".cs" or ".ts" or ".js" or ".tsx" or ".jsx" or ".py" or ".java" or ".go" or ".rs" or ".cpp" or ".c" or ".h" or ".sql" or ".sh" or ".ps1" or ".html" or ".htm" or ".css" or ".scss" => Kind.Text,
        ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz" or ".zst" => Kind.Archive,
        _ => Kind.Default
    };

    private static IBrush Hex(string s) => new SolidColorBrush(Color.Parse(s));

    // 24×24 logical units, fold-corner document
    private static readonly Geometry DocGeometry = Geometry.Parse(
        "M 6 2 L 6 22 L 18 22 L 18 9 L 11 2 Z M 11 2 L 11 9 L 18 9");

    private static readonly Geometry PdfGeometry = Geometry.Parse(
        "M 6 2 L 6 22 L 18 22 L 18 9 L 11 2 Z M 11 2 L 11 9 L 18 9 M 9 13 H 15 M 9 16 H 15 M 9 19 H 12");

    private static readonly Geometry SlideGeometry = Geometry.Parse(
        "M 5 5 H 19 V 17 H 5 Z M 5 8 H 19 M 8 11 H 16 M 8 14 H 14");

    private static readonly Geometry SheetGeometry = Geometry.Parse(
        "M 6 3 H 18 V 21 H 6 Z M 8 7 H 16 M 8 11 H 16 M 8 15 H 14 M 8 19 H 16");

    private static readonly Geometry ImageGeometry = Geometry.Parse(
        "M 5 5 H 19 V 19 H 5 Z M 7 16 L 10 12 L 13 15 L 17 10 V 17 H 7 Z M 14 8 A 1.2 1.2 0 1 0 14 8.01 Z");

    private static readonly Geometry VideoGeometry = Geometry.Parse(
        "M 5 7 H 14 V 17 H 5 Z M 15 10 L 20 12.5 L 15 15 Z");

    private static readonly Geometry AudioGeometry = Geometry.Parse(
        "M 9 16 A 2.5 2.5 0 1 0 9 16.01 Z M 11 6 V 14 M 11 6 L 16 4 V 16 L 11 14");

    private static readonly Geometry TextLinesGeometry = Geometry.Parse(
        "M 6 5 H 18 M 6 9 H 18 M 6 13 H 15 M 6 17 H 18");

    private static readonly Geometry ArchiveGeometry = Geometry.Parse(
        "M 8 3 H 16 V 6 H 8 Z M 6 6 H 18 V 21 H 6 Z M 9 6 V 21 M 12 6 V 21");
}
