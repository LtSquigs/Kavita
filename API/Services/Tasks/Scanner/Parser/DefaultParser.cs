using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using API.Data.Metadata;
using API.Entities.Enums;
using API.Structs;
using DotNet.Globbing.Token;
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
    ParserInfo[] Parse(string filePath, string rootPath, string libraryRoot, LibraryType type, ComicInfo? comicInfo = null, bool parseVolumeChapters = false);
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
    public abstract ParserInfo[] Parse(string filePath, string rootPath, string libraryRoot, LibraryType type, ComicInfo? comicInfo = null, bool parseVolumeChapters = false);

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

        if (info.ComicInfo.Pages.Length > 0) {
            var cover = Array.Find(info.ComicInfo.Pages, (p) => p.GetPageType() == PageType.InnerCover || p.GetPageType() == PageType.FrontCover);
            info.FileMetadata = new FileMetadata(info.FileMetadata.Path, info.FileMetadata.PageRange, info.FileMetadata.FileSize, cover != null ? cover.Image : -1);
        }

    }

    public abstract bool IsApplicable(string filePath, LibraryType type);

    protected static bool IsEmptyOrDefault(string volumes, string chapters)
    {
        return (string.IsNullOrEmpty(chapters) || chapters == Parser.DefaultChapter) &&
               (string.IsNullOrEmpty(volumes) || volumes == Parser.LooseLeafVolume);
    }

    /// <summary>
    /// Converts the parsed chapters from ParseVolumeChapters into ParserInfo objects
    /// </summary>
    /// <param name="baseParserInfo">The parser info of the suspected volume</param>
    /// <param name="type">The library type of the volume being parsed</param>
    /// <param name="pages">An ordered list of PageInfo that represents the pages in the volume.</param>
    /// <param name="chapters">List of ParsedChapter objects that need to be converted into parser info.</param>
    private static ParserInfo[] ParsedChaptersToInfo (ParserInfo baseParserInfo, LibraryType type, List<PageInfo> pages, List<ParsedChapter> chapters) {
        return chapters.Select((bookmark, idx) => {
            // For the first chapter in our list, we set the startSpan to 0 just to ensure the full
            // volume is included (e.g. if a bookmark is set 5 pages in, we still want those 5 pages)
            var startSpan = idx == 0 ? 0 : bookmark.Page;
            // The end of the span is always defined to be up to the page before the next span or
            // the end of the volume if its the last span
            var endSpan = idx == chapters.Count -1 ? pages.Count - 1 : chapters[idx + 1].Page - 1;
            var parserInfo = baseParserInfo.Clone();
     
            Page? cover = null;

            if (parserInfo.ComicInfo != null) {
                parserInfo.ComicInfo.PageCount = endSpan - startSpan + 1;
                parserInfo.ComicInfo.TitleSort = Parser.ParseChapterTitle(bookmark.TitleStr, type);
                parserInfo.ComicInfo.Title = Parser.ParseChapterTitle(bookmark.TitleStr, type);
                parserInfo.ComicInfo.Number = bookmark.Chapter;
                
                cover = Array.Find(parserInfo.ComicInfo.Pages, p => p.Image >= startSpan && p.Image <= endSpan && (p.GetPageType() == PageType.InnerCover || p.GetPageType() == PageType.FrontCover));
            } else {
                parserInfo.ComicInfo = new ComicInfo() {
                    PageCount = endSpan - startSpan + 1,
                    TitleSort = Parser.ParseChapterTitle(bookmark.TitleStr, type),
                    Title = Parser.ParseChapterTitle(bookmark.TitleStr, type),
                    Number = bookmark.Chapter
                };
            }
            var size = pages.GetRange(startSpan, endSpan - startSpan + 1).Sum(f => f.Size);
            parserInfo.Chapters = bookmark.Chapter;
            parserInfo.FileMetadata = new FileMetadata(parserInfo.FileMetadata.Path, startSpan + "-" + endSpan, size, cover != null ? cover.Image - startSpan : -1);

            return parserInfo;
        }).ToArray();
    }

    /// <summary>
    /// Attempts to parse chapters from inside a volume file.
    /// </summary>
    /// <param name="baseParserInfo">The parser info of the suspected volume</param>
    /// <param name="type">The library type of the volume being parsed</param>
    /// <param name="pages">An ordered list of PageInfo that represents the pages in the volume.</param>
    public static ParserInfo[] ParseVolumeChapters(ParserInfo baseParserInfo, LibraryType type, List<PageInfo> pages) {
        // We only want to try to extract chapters from files that have been clearly
        // marked as having a volume, but somehow don't have chapters 
        if (baseParserInfo.IsSpecial) return [baseParserInfo];
        if (baseParserInfo.Chapters != Parser.DefaultChapter) return [baseParserInfo];
        if (baseParserInfo.Volumes == Parser.LooseLeafVolume) return [baseParserInfo];

        // First we attempt to parse chapters based off of the Page array of the ComicInfo
        // We look for Bookmarks that meet a specific format to parse the chapter number
        // and the title
        if (baseParserInfo.ComicInfo != null) {
            var chaptersFromInfo = baseParserInfo.ComicInfo.Pages.Select((p) => {
                string chapter = Parser.ParseChapter(p.Bookmark, type);
                return new ParsedChapter() { Page = p.Image, Chapter = chapter, TitleStr = p.Bookmark};
            }).Where(y => y.Chapter != Parser.DefaultChapter).ToList();

            if (chaptersFromInfo.Any()) {
                return ParsedChaptersToInfo(baseParserInfo, type, pages, chaptersFromInfo);
            }
        }

        // If we do not find any chapters from the ComicInfo, we attempt to use the filenames
        // in the volume to detect chapter boundaries and titles.
        var allDaiz = true;
        var tagCount = new Dictionary<string, int>();
        var chapterPagesCount = new Dictionary<string, int>();
        var tagChapters = new Dictionary<string, HashSet<string>>();

        var parsedChapters = new List<(bool success, string chapter, HashSet<string> tags)>();
        for(var idx  = 0; idx < pages.Count; idx++) {
            var page = pages[idx];

            if (!allDaiz) continue;

            var parsedInfo = Parser.ParseDaizInfo(page.Name);
            if (!parsedInfo.success) {
                allDaiz = false;
                continue;
            }

            if(chapterPagesCount.ContainsKey(parsedInfo.chapter)) {
                chapterPagesCount[parsedInfo.chapter] = chapterPagesCount[parsedInfo.chapter] + 1;
            } else {
                chapterPagesCount[parsedInfo.chapter] = 1;
            }

            var tags = parsedInfo.tags;
            if (tags != null) {
                foreach(var tag in tags) {
                    if(tagCount.ContainsKey(tag)) {
                        tagCount[tag] = tagCount[tag] + 1;
                    } else {
                        tagCount[tag] = 1;
                    }

                    if (tagChapters.ContainsKey(tag)) {
                        tagChapters[tag].Add(tag);
                    } else {
                        tagChapters[tag] = new HashSet<string>([tag]);
                    }
                }
            }

            parsedChapters.Add(parsedInfo);
        }
        if (!allDaiz) return [baseParserInfo];

        var chaptersFromPages = parsedChapters.Select((p, idx) => {
            var chapter = p.chapter;
            var tags = p.tags;
            // We attempt to use the extra tags from the filename to discover what the
            // title of the chapter is. In order to do that we look for a tag that fulfills
            // the following conditions:
            //   1. Is not present on multiple chapters in the list (eliminates group tags, scan tags, format tags, etc.)
            //   2. Is present for all files in the chapter (eliminates one off tags like ToC or Cover)
            //   3. Is not one of the known tags for special chapters/formats (e.g. OneShot, Omake, etc)
            var title = string.Empty;
            if (tags != null) {
                foreach(var tag in tags) {
                    Console.WriteLine(tag);
                    Console.WriteLine(tagChapters[tag].Count);
                    // Present on multiple chapters then not the title
                    if (tagChapters[tag].Count > 1) {
                        continue;
                    }
                    Console.WriteLine(tagCount[tag]);
                    Console.WriteLine(chapterPagesCount[chapter]);
                    // If not present on every page in chapter then not title
                    if (tagCount[tag] != chapterPagesCount[chapter]) {
                        continue;
                    }
                    // If one of the common format tag values then not title
                    if (Parser.DaizSpecialPattern.IsMatch(tag)) {
                        continue;
                    }

                    Console.WriteLine($"title = {tag}");
                    title = tag;
                    break;
                }
            }

            // We treat the chapters formatted with and x like 15x1 as if its a decimal 15.1
            string chapterFormatted = chapter;
            if (chapterFormatted.IndexOf('.') != -1) {
                chapterFormatted = chapterFormatted.Replace("x", string.Empty);
            } else {
                chapterFormatted = chapterFormatted.Replace("x", ".");
            }

            chapterFormatted = Parser.FormatValue(chapterFormatted, false);
            return new ParsedChapter() { Page = idx, Chapter = chapterFormatted, TitleStr = $"Chapter {chapterFormatted} - {title}"};
        });

        var dedupedChapters = chaptersFromPages.GroupBy((x) => x.Chapter).Select((x) => x.First()).ToList();
        return ParsedChaptersToInfo(baseParserInfo, type, pages, dedupedChapters);
    }
}
