using System;
using System.Diagnostics.CodeAnalysis;
using API.Services.Tasks.Scanner.Parser;
using ExCSS;

namespace API.Structs;
public record struct PageInfo {
    [SetsRequiredMembers]
    public PageInfo(string name, long number, long size, bool isCover)
    {
        Name = name;
        Number = number;
        Size = size;
        IsCover = isCover;
    }
    public required string Name { get; set; }
    public required long Number { get; set; }
    public long Size { get; set; } = -1;
    public bool IsCover { get; set; } = false;
}