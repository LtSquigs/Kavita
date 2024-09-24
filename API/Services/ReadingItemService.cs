using System;
using API.Data.Metadata;
using API.Entities.Enums;
using API.Services.Tasks.Scanner.Parser;
using API.Structs;
using Microsoft.Extensions.Logging;

namespace API.Services;
#nullable enable

public interface IReadingItemService
{
    ComicInfo? GetComicInfo(FileMetadata fileMetadata);
    int GetNumberOfPages(FileMetadata fileMetadata, MangaFormat format);
    string GetCoverImage(FileMetadata fileMetadata, string fileName, MangaFormat format, EncodeFormat encodeFormat, CoverImageSize size = CoverImageSize.Default);
    void Extract(FileMetadata fileFileMetadata, string targetDirectory, MangaFormat format, int imageCount = 1);
    ParserInfo[] ParseFile(string path, string rootPath, string libraryRoot, LibraryType type, bool extractChapters);
}

public class ReadingItemService : IReadingItemService
{
    private readonly IArchiveService _archiveService;
    private readonly IBookService _bookService;
    private readonly IImageService _imageService;
    private readonly IDirectoryService _directoryService;
    private readonly ILogger<ReadingItemService> _logger;
    private readonly BasicParser _basicParser;
    private readonly ComicVineParser _comicVineParser;
    private readonly ImageParser _imageParser;
    private readonly BookParser _bookParser;
    private readonly PdfParser _pdfParser;

    public ReadingItemService(IArchiveService archiveService, IBookService bookService, IImageService imageService,
        IDirectoryService directoryService, ILogger<ReadingItemService> logger)
    {
        _archiveService = archiveService;
        _bookService = bookService;
        _imageService = imageService;
        _directoryService = directoryService;
        _logger = logger;

        _imageParser = new ImageParser(directoryService);
        _basicParser = new BasicParser(directoryService, _imageParser, _archiveService, _logger);
        _bookParser = new BookParser(directoryService, bookService, _basicParser);
        _comicVineParser = new ComicVineParser(directoryService);
        _pdfParser = new PdfParser(directoryService);

    }

    /// <summary>
    /// Gets the ComicInfo for the file if it exists. Null otherwise.
    /// </summary>
    /// <param name="fileMetadata">Fully qualified path of file</param>
    /// <returns></returns>
    public ComicInfo? GetComicInfo(FileMetadata fileMetadata)
    {
        if (Parser.IsEpub(fileMetadata.Path))
        {
            return _bookService.GetComicInfo(fileMetadata.Path);
        }

        if (Parser.IsComicInfoExtension(fileMetadata.Path))
        {
            return _archiveService.GetComicInfo(fileMetadata);
        }

        return null;
    }

    /// <summary>
    /// Processes files found during a library scan.
    /// </summary>
    /// <param name="path">Path of a file</param>
    /// <param name="rootPath"></param>
    /// <param name="type">Library type to determine parsing to perform</param>
    public ParserInfo[] ParseFile(string path, string rootPath, string libraryRoot, LibraryType type, bool extractChapters)
    {
        try
        {
            var infos = Parse(path, rootPath, libraryRoot, type, extractChapters);
            if (infos.Length == 0)
            {
                _logger.LogError("Unable to parse any meaningful information out of file {FilePath}", path);
                return [];
            }

            return infos;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "There was an exception when parsing file {FilePath}", path);
            return [];
        }
    }

    /// <summary>
    ///
    /// </summary>
    /// <param name="fileMetadata"></param>
    /// <param name="format"></param>
    /// <returns></returns>
    public int GetNumberOfPages(FileMetadata fileMetadata, MangaFormat format)
    {
        switch (format)
        {
            case MangaFormat.Archive:
            {
                return _archiveService.GetNumberOfPagesFromArchive(fileMetadata);
            }
            case MangaFormat.Pdf:
            case MangaFormat.Epub:
            {
                return _bookService.GetNumberOfPages(fileMetadata.Path);
            }
            case MangaFormat.Image:
            {
                return 1;
            }
            case MangaFormat.Unknown:
            default:
                return 0;
        }
    }

    public string GetCoverImage(FileMetadata fileMetadata, string fileName, MangaFormat format, EncodeFormat encodeFormat, CoverImageSize size = CoverImageSize.Default)
    {
        if (string.IsNullOrEmpty(fileMetadata.Path) || string.IsNullOrEmpty(fileName))
        {
            return string.Empty;
        }


        return format switch
        {
            MangaFormat.Epub => _bookService.GetCoverImage(fileMetadata.Path, fileName, _directoryService.CoverImageDirectory, encodeFormat, size),
            MangaFormat.Archive => _archiveService.GetCoverImage(fileMetadata, fileName, _directoryService.CoverImageDirectory, encodeFormat, size),
            MangaFormat.Image => _imageService.GetCoverImage(fileMetadata.Path, fileName, _directoryService.CoverImageDirectory, encodeFormat, size),
            MangaFormat.Pdf => _bookService.GetCoverImage(fileMetadata.Path, fileName, _directoryService.CoverImageDirectory, encodeFormat, size),
            _ => string.Empty
        };
    }

    /// <summary>
    /// Extracts the reading item to the target directory using the appropriate method
    /// </summary>
    /// <param name="fileMetadata">File to extract</param>
    /// <param name="targetDirectory">Where to extract to. Will be created if does not exist</param>
    /// <param name="format">Format of the File</param>
    /// <param name="imageCount">If the file is of type image, pass number of files needed. If > 0, will copy the whole directory.</param>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public void Extract(FileMetadata fileMetadata, string targetDirectory, MangaFormat format, int imageCount = 1)
    {
        switch (format)
        {
            case MangaFormat.Archive:
                _archiveService.ExtractArchive(fileMetadata, targetDirectory);
                break;
            case MangaFormat.Image:
                _imageService.ExtractImages(fileMetadata.Path, targetDirectory, imageCount);
                break;
            case MangaFormat.Pdf:
                _bookService.ExtractPdfImages(fileMetadata.Path, targetDirectory);
                break;
            case MangaFormat.Unknown:
            case MangaFormat.Epub:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, null);
        }
    }

    /// <summary>
    /// Parses information out of a file. If file is a book (epub), it will use book metadata regardless of LibraryType
    /// </summary>
    /// <param name="path"></param>
    /// <param name="rootPath"></param>
    /// <param name="type"></param>
    /// <returns></returns>
    private ParserInfo[] Parse(string path, string rootPath, string libraryRoot, LibraryType type, bool extractChapters)
    {
        var fileMetadata = new FileMetadata(path);
        if (_comicVineParser.IsApplicable(path, type))
        {
            return _comicVineParser.Parse(path, rootPath, libraryRoot, type, GetComicInfo(fileMetadata), extractChapters);
        }
        if (_imageParser.IsApplicable(path, type))
        {
            return _imageParser.Parse(path, rootPath, libraryRoot, type, GetComicInfo(fileMetadata), extractChapters);
        }
        if (_bookParser.IsApplicable(path, type))
        {
            return _bookParser.Parse(path, rootPath, libraryRoot, type, GetComicInfo(fileMetadata), extractChapters);
        }
        if (_pdfParser.IsApplicable(path, type))
        {
            return _pdfParser.Parse(path, rootPath, libraryRoot, type, GetComicInfo(fileMetadata), extractChapters);
        }
        if (_basicParser.IsApplicable(path, type))
        {
            return _basicParser.Parse(path, rootPath, libraryRoot, type, GetComicInfo(fileMetadata), extractChapters);
        }

        return [];
    }
}
