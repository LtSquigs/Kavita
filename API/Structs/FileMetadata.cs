using System;
using System.Diagnostics.CodeAnalysis;
using API.Data.Migrations;
using API.Services.Tasks.Scanner.Parser;

namespace API.Structs;
/// <summary>
/// Represents a file on the disk, can represent either a full file or a file with an internal
/// range of pages.
/// </summary>
public record struct FileMetadata {
    [SetsRequiredMembers]
    public FileMetadata(string path, string pageRange = "", long fileSize = -1, int coverIndex = -1)
    {
        Path = path;
        PageRange = pageRange;
        FileSize = fileSize;
        CoverIndex = coverIndex;
    }

    public required string Path { get; set; }
    public string PageRange { get; set; } = string.Empty;
    public long FileSize { get; set; } = -1;
    public int CoverIndex { get; set; } = -1;

    public string ID() {
        return Path + PageRange;
    }
    public int MinRange() {
        return Convert.ToInt32(Math.Floor(Parser.MinNumberFromRange(PageRange)));
    }
    public int MaxRange() {
        return Convert.ToInt32(Math.Floor(Parser.MaxNumberFromRange(PageRange)));
    }

    public bool HasPageRange() {
        return !string.IsNullOrEmpty(PageRange);
    }

    public FileMetadata Normalized()
    {
        return new FileMetadata(Parser.NormalizePath(Path), PageRange, FileSize, CoverIndex);
    }

    public static readonly FileMetadata Empty = new FileMetadata("");
}