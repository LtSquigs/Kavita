using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using API.Data.Metadata;
using API.Entities.Enums;
using API.Structs;
using Microsoft.Extensions.Logging;

namespace API.Services.Tasks.Scanner.Parser;
#nullable enable

record struct ParsedChapter {
    public int Page;
    public string Chapter;
    public string TitleStr;
}
public interface IDefaultParser
{
    ParserInfo[] Parse(string filePath, string rootPath, string libraryRoot, LibraryType type, ComicInfo? comicInfo = null, bool extractChapters = false);
    void ParseFromFallbackFolders(string filePath, string rootPath, LibraryType type, ref ParserInfo ret);
    bool IsApplicable(string filePath, LibraryType type);
}

/// <summary>
/// This is an implementation of the Parser that is the basis for everything
/// </summary>
public abstract class DefaultParser(IDirectoryService directoryService) : IDefaultParser
{

    /// <summary>
    /// Parses information out of a file path. Can fallback to using directory name if Series couldn't be parsed
    /// from filename.
    /// </summary>
    /// <param name="filePath"></param>
    /// <param name="rootPath">Root folder</param>
    /// <param name="type">Allows different Regex to be used for parsing.</param>
    /// <returns><see cref="ParserInfo"/> or null if Series was empty</returns>
    public abstract ParserInfo[] Parse(string filePath, string rootPath, string libraryRoot, LibraryType type, ComicInfo? comicInfo = null, bool extractChapters = false);

    /// <summary>
    /// Fills out <see cref="ParserInfo"/> by trying to parse volume, chapters, and series from folders
    /// </summary>
    /// <param name="filePath"></param>
    /// <param name="rootPath"></param>
    /// <param name="type"></param>
    /// <param name="ret">Expects a non-null ParserInfo which this method will populate</param>
    public void ParseFromFallbackFolders(string filePath, string rootPath, LibraryType type, ref ParserInfo ret)
    {
        var fallbackFolders = directoryService.GetFoldersTillRoot(rootPath, filePath)
            .Where(f => !Parser.IsSpecial(f, type))
            .ToList();

        if (fallbackFolders.Count == 0)
        {
            var rootFolderName = directoryService.FileSystem.DirectoryInfo.New(rootPath).Name;
            var series = Parser.ParseSeries(rootFolderName, type);

            if (string.IsNullOrEmpty(series))
            {
                ret.Series = Parser.CleanTitle(rootFolderName, type is LibraryType.Comic);
                return;
            }

            if (!string.IsNullOrEmpty(series) && (string.IsNullOrEmpty(ret.Series) || !rootFolderName.Contains(ret.Series)))
            {
                ret.Series = series;
                return;
            }
        }

        for (var i = 0; i < fallbackFolders.Count; i++)
        {
            var folder = fallbackFolders[i];

            var parsedVolume = Parser.ParseVolume(folder, type);
            var parsedChapter = Parser.ParseChapter(folder, type);

            if (!parsedVolume.Equals(Parser.LooseLeafVolume) || !parsedChapter.Equals(Parser.DefaultChapter))
            {
                if ((string.IsNullOrEmpty(ret.Volumes) || ret.Volumes.Equals(Parser.LooseLeafVolume))
                    && !string.IsNullOrEmpty(parsedVolume) && !parsedVolume.Equals(Parser.LooseLeafVolume))
                {
                    ret.Volumes = parsedVolume;
                }
                if ((string.IsNullOrEmpty(ret.Chapters) || ret.Chapters.Equals(Parser.DefaultChapter))
                    && !string.IsNullOrEmpty(parsedChapter) && !parsedChapter.Equals(Parser.DefaultChapter))
                {
                    ret.Chapters = parsedChapter;
                }
            }

            // Generally users group in series folders. Let's try to parse series from the top folder
            if (!folder.Equals(ret.Series) && i == fallbackFolders.Count - 1)
            {
                var series = Parser.ParseSeries(folder, type);

                if (string.IsNullOrEmpty(series))
                {
                    ret.Series = Parser.CleanTitle(folder, type is LibraryType.Comic);
                    break;
                }

                if (!string.IsNullOrEmpty(series) && (string.IsNullOrEmpty(ret.Series) && !folder.Contains(ret.Series)))
                {
                    ret.Series = series;
                    break;
                }
            }
        }
    }

    protected static void UpdateFromComicInfo(ParserInfo info)
    {
        if (info.ComicInfo == null) return;

        if (!string.IsNullOrEmpty(info.ComicInfo.Volume))
        {
            info.Volumes = info.ComicInfo.Volume;
        }
        if (!string.IsNullOrEmpty(info.ComicInfo.Number))
        {
            info.Chapters = info.ComicInfo.Number;
        }
        if (!string.IsNullOrEmpty(info.ComicInfo.Series))
        {
            info.Series = info.ComicInfo.Series.Trim();
        }
        if (!string.IsNullOrEmpty(info.ComicInfo.LocalizedSeries))
        {
            info.LocalizedSeries = info.ComicInfo.LocalizedSeries.Trim();
        }

        if (!string.IsNullOrEmpty(info.ComicInfo.Format) && Parser.HasComicInfoSpecial(info.ComicInfo.Format))
        {
            info.IsSpecial = true;
            info.Chapters = Parser.DefaultChapter;
            info.Volumes = Parser.SpecialVolume;
        }

        // Patch is SeriesSort from ComicInfo
        if (!string.IsNullOrEmpty(info.ComicInfo.TitleSort))
        {
            info.SeriesSort = info.ComicInfo.TitleSort.Trim();
        }

    }

    public abstract bool IsApplicable(string filePath, LibraryType type);

    protected static bool IsEmptyOrDefault(string volumes, string chapters)
    {
        return (string.IsNullOrEmpty(chapters) || chapters == Parser.DefaultChapter) &&
               (string.IsNullOrEmpty(volumes) || volumes == Parser.LooseLeafVolume);
    }

    private static ParserInfo[] ParsedChaptersToInfo (ParserInfo baseParserInfo, LibraryType type, List<PageInfo> pages, List<ParsedChapter> chapters) {
        IEnumerable<int> covers = new List<int>();
        if (baseParserInfo.ComicInfo != null) {
            covers = baseParserInfo.ComicInfo.Pages.Select((p) => {
                if (p.GetPageType() == PageType.InnerCover || p.GetPageType() == PageType.FrontCover ) {
                    return p.Image;
                }
                return -1;
            }).Where(y => y != -1);
        }

        return chapters.Select((bookmark, idx) => {
            var startSpan = idx == 0 ? 0 : bookmark.Page;
            var endSpan = idx == chapters.Count -1 ? pages.Count - 1 : chapters[idx + 1].Page - 1;
            var parserInfo = baseParserInfo.Clone();
     
            if (parserInfo.ComicInfo != null) {
                parserInfo.ComicInfo.PageCount = endSpan - startSpan + 1;
                parserInfo.ComicInfo.TitleSort = Parser.ParseBookmarkTitle(bookmark.TitleStr, type);
                parserInfo.ComicInfo.Title = Parser.ParseBookmarkTitle(bookmark.TitleStr, type);
                parserInfo.ComicInfo.Number = bookmark.Chapter;
            } else {
                parserInfo.ComicInfo = new ComicInfo() {
                    PageCount = endSpan - startSpan + 1,
                    TitleSort = Parser.ParseBookmarkTitle(bookmark.TitleStr, type),
                    Title = Parser.ParseBookmarkTitle(bookmark.TitleStr, type),
                    Number = bookmark.Chapter
                };
            }
            var size = pages.GetRange(startSpan, endSpan - startSpan + 1).Sum(f => f.Size);
            var coverIdx = covers.FirstOrDefault(c => c >= startSpan && c <= endSpan, -1);
            var cover = coverIdx != -1 ? pages[coverIdx].Name : string.Empty;
            parserInfo.Chapters = bookmark.Chapter;
            parserInfo.FileMetadata = new FileMetadata(parserInfo.FileMetadata.Path, startSpan + "-" + endSpan, size, cover);

            return parserInfo;
        }).ToArray();
    }

    public static ParserInfo[] ExtractChapters(ParserInfo baseParserInfo, LibraryType type, List<PageInfo> pages) {
        // We only want to try to extract chapters from files that have been clearly
        // marked as having a volume, but somehow don't have chapters 
        if (baseParserInfo.IsSpecial) return [baseParserInfo];
        if (baseParserInfo.Chapters != Parser.DefaultChapter) return [baseParserInfo];
        if (baseParserInfo.Volumes == Parser.LooseLeafVolume) return [baseParserInfo];

        if (baseParserInfo.ComicInfo != null) {
            var chaptersFromInfo = baseParserInfo.ComicInfo.Pages.Select((p) => {
                string chapter = Parser.ParseChapter(p.Bookmark, type);
                return new ParsedChapter() { Page = p.Image, Chapter = chapter, TitleStr = p.Bookmark};
            }).Where(y => y.Chapter != Parser.DefaultChapter).ToList();

            if (chaptersFromInfo.Any()) {
                return ParsedChaptersToInfo(baseParserInfo, type, pages, chaptersFromInfo);
            }
        }

        var chaptersFromPages = pages.Select((f, idx) => {
            string chapter = Parser.ParseChapter(Parser.RemoveEditionTagHolders(f.Name), type);
            var fileParts = Parser.NormalizePath(f.Name).Split(Path.AltDirectorySeparatorChar);
            var titlePart = fileParts.FirstOrDefault(p => {
                return !String.IsNullOrEmpty(Parser.ParseBookmarkTitle(p, type));
            }, string.Empty);
            return new ParsedChapter() { Page = idx, Chapter = chapter, TitleStr = titlePart};
        }).Where(y => y.Chapter != Parser.DefaultChapter);

        if (chaptersFromPages.Any()) {
            var dedupedChapters = chaptersFromPages.GroupBy((x) => x.Chapter).Select((x) => x.First()).ToList();
            return ParsedChaptersToInfo(baseParserInfo, type, pages, dedupedChapters);
        }

        return [baseParserInfo];
    }
}
