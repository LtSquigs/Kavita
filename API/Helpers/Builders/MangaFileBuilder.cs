﻿using System;
using System.IO;
using API.Entities;
using API.Entities.Enums;
using API.Services.Tasks.Scanner.Parser;
using API.Structs;

namespace API.Helpers.Builders;

public class MangaFileBuilder : IEntityBuilder<MangaFile>
{
    private readonly MangaFile _mangaFile;
    public MangaFile Build() => _mangaFile;

    public MangaFileBuilder(FileMetadata fileMetadata, MangaFormat format, int pages = 0)
    {
        _mangaFile = new MangaFile()
        {
            FileMetadata = fileMetadata.Normalized(),
            Format = format,
            Pages = pages,
            LastModified = File.GetLastWriteTime(fileMetadata.Path),
            LastModifiedUtc = File.GetLastWriteTimeUtc(fileMetadata.Path),
            FileName = Parser.RemoveExtensionIfSupported(fileMetadata.Path)
        };
    }

    public MangaFileBuilder WithFormat(MangaFormat format)
    {
        _mangaFile.Format = format;
        return this;
    }

    public MangaFileBuilder WithPages(int pages)
    {
        _mangaFile.Pages = Math.Max(pages, 0);
        return this;
    }

    public MangaFileBuilder WithExtension(string extension)
    {
        _mangaFile.Extension = extension.ToLowerInvariant();
        return this;
    }

    public MangaFileBuilder WithBytes(long bytes)
    {
        _mangaFile.Bytes = Math.Max(0, bytes);
        return this;
    }

    public MangaFileBuilder WithLastModified(DateTime dateTime)
    {
        _mangaFile.LastModified = dateTime;
        _mangaFile.LastModifiedUtc = dateTime.ToUniversalTime();
        return this;
    }

    public MangaFileBuilder WithId(int id)
    {
        _mangaFile.Id = Math.Max(id, 0);
        return this;
    }
}
