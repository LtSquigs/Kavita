using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Xml.Serialization;
using API.Archive;
using API.Data.Metadata;
using API.Entities;
using API.Entities.Enums;
using API.Extensions;
using API.Services.Tasks;
using API.Structs;
using CsvHelper;
using Kavita.Common;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Writers;
using Xunit.Sdk;

namespace API.Services;

#nullable enable


public interface IArchiveService
{
    void ExtractArchive(FileMetadata archivePath, string extractPath);
    int GetNumberOfPagesFromArchive(FileMetadata archivePath);
    string GetCoverImage(FileMetadata archivePath, string fileName, string outputDirectory, EncodeFormat format, CoverImageSize size = CoverImageSize.Default);
    bool IsValidArchive(FileMetadata archivePath);
    ComicInfo? GetComicInfo(FileMetadata archivePath);
    ArchiveLibrary CanOpen(FileMetadata archivePath);
    bool ArchiveNeedsFlattening(ZipArchive archive);
    /// <summary>
    /// Creates a zip file form the listed files and outputs to the temp folder. This will combine into one zip of multiple zips.
    /// </summary>
    /// <param name="files">List of files to be zipped up. Should be full file paths.</param>
    /// <param name="tempFolder">Temp folder name to use for preparing the files. Will be created and deleted</param>
    /// <returns>Path to the temp zip</returns>
    /// <exception cref="KavitaException"></exception>
    string CreateZipForDownload(IEnumerable<string> files, string tempFolder);
    /// <summary>
    /// Creates a zip file form the listed files and outputs to the temp folder. This will extract each archive and combine them into one zip.
    /// </summary>
    /// <param name="files">List of files to be zipped up. Should be full file paths.</param>
    /// <param name="tempFolder">Temp folder name to use for preparing the files. Will be created and deleted</param>
    /// <returns>Path to the temp zip</returns>
    /// <exception cref="KavitaException"></exception>
    string CreateZipFromFoldersForDownload(IList<MangaFile> files, string tempFolder, Func<Tuple<string, float>, Task> progressCallback);

    public Task<Stream> CreateZipStream(FileMetadata archivePath);

    public List<PageInfo> GetPages(FileMetadata archivePath);
}

/// <summary>
/// Responsible for manipulating Archive files. Used by <see cref="CacheService"/> and <see cref="ScannerService"/>
/// </summary>
// ReSharper disable once ClassWithVirtualMembersNeverInherited.Global
public class ArchiveService : IArchiveService
{
    private readonly ILogger<ArchiveService> _logger;
    private readonly IDirectoryService _directoryService;
    private readonly IImageService _imageService;
    private readonly IMediaErrorService _mediaErrorService;
    private const string ComicInfoFilename = "ComicInfo.xml";

    public ArchiveService(ILogger<ArchiveService> logger, IDirectoryService directoryService,
        IImageService imageService, IMediaErrorService mediaErrorService)
    {
        _logger = logger;
        _directoryService = directoryService;
        _imageService = imageService;
        _mediaErrorService = mediaErrorService;
    }

    /// <summary>
    /// Checks if a File can be opened. Requires up to 2 opens of the filestream.
    /// </summary>
    /// <param name="archivePath"></param>
    /// <returns></returns>
    public virtual ArchiveLibrary CanOpen(FileMetadata archivePath)
    {
        var filePath = archivePath.Path;
        if (string.IsNullOrEmpty(filePath) || !(File.Exists(filePath) && Tasks.Scanner.Parser.Parser.IsArchive(filePath) || Tasks.Scanner.Parser.Parser.IsEpub(filePath))) return ArchiveLibrary.NotSupported;

        var ext = _directoryService.FileSystem.Path.GetExtension(filePath).ToUpper();
        if (ext.Equals(".CBR") || ext.Equals(".RAR")) return ArchiveLibrary.SharpCompress;

        try
        {
            using var a2 = ZipFile.OpenRead(filePath);
            return ArchiveLibrary.Default;
        }
        catch (Exception)
        {
            try
            {
                using var a1 = ArchiveFactory.Open(filePath);
                return ArchiveLibrary.SharpCompress;
            }
            catch (Exception)
            {
                return ArchiveLibrary.NotSupported;
            }
        }
    }

    IEnumerable<ZipArchiveEntry> GetEntries(ReadOnlyCollection<ZipArchiveEntry> entries, FileMetadata fileMetadata, bool forceImages = false)
    {
        if (!forceImages && !fileMetadata.HasPageRange()) {
            return entries.Where(e => !Tasks.Scanner.Parser.Parser.HasBlacklistedFolderInPath(e.FullName));
        }

        var imageEntries = entries
            .Where(e => 
                !Path.EndsInDirectorySeparator(e.FullName) &&
                !Tasks.Scanner.Parser.Parser.HasBlacklistedFolderInPath(e.FullName) && 
                !e.FullName.StartsWith(Tasks.Scanner.Parser.Parser.MacOsMetadataFileStartsWith) &&
                Tasks.Scanner.Parser.Parser.IsImage(e.FullName)
            ).OrderByNatural(e => e.FullName.GetFullPathWithoutExtension());

        if (forceImages && !fileMetadata.HasPageRange()) {
            return imageEntries;
        }

        var min = fileMetadata.MinRange();
        var max = fileMetadata.MaxRange();

        var coverEntry = imageEntries.FirstOrDefault(f => Tasks.Scanner.Parser.Parser.IsCoverImage(f.FullName));
        var slicedEntries = imageEntries.Where(f => !Tasks.Scanner.Parser.Parser.IsCoverImage(f.FullName)).ToList().GetRange(min, max-min+1);

        if (coverEntry != null && min == 0) {
            slicedEntries.Add(coverEntry);
        }

        return slicedEntries;
    }

    IEnumerable<IArchiveEntry> GetEntries(IEnumerable<IArchiveEntry> entries, FileMetadata fileMetadata, bool forceImages = false)
    {
        if (!forceImages && !fileMetadata.HasPageRange()) {
            return entries.Where(e => !Tasks.Scanner.Parser.Parser.HasBlacklistedFolderInPath(Path.GetDirectoryName(e.Key) ?? string.Empty));;
        }

        var imageEntries = entries
            .Where(e =>
                e.Key != null && 
                !Path.EndsInDirectorySeparator(e.Key) &&
                !e.IsDirectory &&
                !Tasks.Scanner.Parser.Parser.HasBlacklistedFolderInPath(Path.GetDirectoryName(e.Key) ?? string.Empty) &&
                !e.Key.StartsWith(Tasks.Scanner.Parser.Parser.MacOsMetadataFileStartsWith) &&
                Tasks.Scanner.Parser.Parser.IsImage(e.Key)
            ).OrderByNatural(e => (e.Key ?? string.Empty).GetFullPathWithoutExtension());

        if (forceImages && !fileMetadata.HasPageRange()) {
            return imageEntries;
        }

        var min = fileMetadata.MinRange();
        var max = fileMetadata.MaxRange();

        var coverEntry = imageEntries.FirstOrDefault(f => Tasks.Scanner.Parser.Parser.IsCoverImage(f.Key));
        var slicedEntries = imageEntries.Where(f => !Tasks.Scanner.Parser.Parser.IsCoverImage(f.Key)).ToList().GetRange(min, max-min+1);

        if (coverEntry != null && min == 0) {
            slicedEntries.Add(coverEntry);
        }

        return slicedEntries;
    }
    public List<PageInfo> GetPages(FileMetadata archivePath)
    {
        if (string.IsNullOrEmpty(archivePath.Path) || !IsValidArchive(archivePath)) return [];
        try
        {
            var libraryHandler = CanOpen(archivePath);
            switch (libraryHandler)
            {
                case ArchiveLibrary.Default:
                {
                    using var archive = ZipFile.OpenRead(archivePath.Path);

                    var entries = GetEntries(archive.Entries, archivePath, true);
                    return entries.Select((e, idx) => new PageInfo(e.FullName, idx, e.CompressedLength)).ToList();
                }
                case ArchiveLibrary.SharpCompress:
                {
                    using var archive = ArchiveFactory.Open(archivePath.Path);
                    var entries = GetEntries(archive.Entries, archivePath, true);
                    return entries.Select((e, idx) => new PageInfo(e.Key ?? "", idx, e.CompressedSize)).ToList();
                }
                case ArchiveLibrary.NotSupported:
                    _logger.LogWarning("[GetEntryInfo] This archive cannot be read: {ArchivePath}. Defaulting to no cover image", archivePath);
                    return [];
                default:
                    _logger.LogWarning("[GetEntryInfo] There was an exception when reading archive stream: {ArchivePath}. Defaulting to no cover image", archivePath);
                    return [];
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GetEntryInfo] There was an exception when reading archive stream: {ArchivePath}. Defaulting to no cover image", archivePath);
            _mediaErrorService.ReportMediaIssue(archivePath.Path, MediaErrorProducer.ArchiveService,
                "This archive cannot be read or not supported", ex); // TODO: Localize this
        }

        return [];
    }

    public int GetNumberOfPagesFromArchive(FileMetadata archivePath)
    {
        if (!IsValidArchive(archivePath))
        {
            _logger.LogError("Archive {ArchivePath} could not be found", archivePath);
            return 0;
        }

        try
        {
            var libraryHandler = CanOpen(archivePath);
            switch (libraryHandler)
            {
                case ArchiveLibrary.Default:
                {
                    using var archive = ZipFile.OpenRead(archivePath.Path);
                    return GetEntries(archive.Entries, archivePath).Count(e => 
                        !Tasks.Scanner.Parser.Parser.HasBlacklistedFolderInPath(e.FullName)
                        && Tasks.Scanner.Parser.Parser.IsImage(e.FullName)
                    );
                }
                case ArchiveLibrary.SharpCompress:
                {
                    using var archive = ArchiveFactory.Open(archivePath.Path);
                    return GetEntries(archive.Entries, archivePath).Count(entry =>
                        !entry.IsDirectory
                        && !Tasks.Scanner.Parser.Parser.HasBlacklistedFolderInPath(Path.GetDirectoryName(entry.Key) ?? string.Empty)
                        && Tasks.Scanner.Parser.Parser.IsImage(entry.Key)
                    );
                }
                case ArchiveLibrary.NotSupported:
                    _logger.LogWarning("[GetNumberOfPagesFromArchive] This archive cannot be read: {ArchivePath}. Defaulting to 0 pages", archivePath);
                    return 0;
                default:
                    _logger.LogWarning("[GetNumberOfPagesFromArchive] There was an exception when reading archive stream: {ArchivePath}. Defaulting to 0 pages", archivePath);
                    return 0;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GetNumberOfPagesFromArchive] There was an exception when reading archive stream: {ArchivePath}. Defaulting to 0 pages", archivePath);
            _mediaErrorService.ReportMediaIssue(archivePath.Path, MediaErrorProducer.ArchiveService,
                "This archive cannot be read or not supported", ex);
            return 0;
        }
    }

    /// <summary>
    /// Finds the first instance of a folder entry and returns it
    /// </summary>
    /// <param name="entryFullNames"></param>
    /// <returns>Entry name of match, null if no match</returns>
    public static string? FindFolderEntry(IEnumerable<string> entryFullNames)
    {
        var result = entryFullNames
            .Where(path => !(Path.EndsInDirectorySeparator(path) || Tasks.Scanner.Parser.Parser.HasBlacklistedFolderInPath(path) || path.StartsWith(Tasks.Scanner.Parser.Parser.MacOsMetadataFileStartsWith)))
            .OrderByNatural(Path.GetFileNameWithoutExtension)
            .FirstOrDefault(Tasks.Scanner.Parser.Parser.IsCoverImage);

        return string.IsNullOrEmpty(result) ? null : result;
    }

    /// <summary>
    /// Returns first entry that is an image and is not in a blacklisted folder path. Uses <see cref="EnumerableExtensions.OrderByNatural"/> for ordering files
    /// </summary>
    /// <param name="entryFullNames"></param>
    /// <param name="archiveName"></param>
    /// <returns>Entry name of match, null if no match</returns>
    public static string? FirstFileEntry(IEnumerable<string> entryFullNames, string archiveName)
    {
        // First check if there are any files that are not in a nested folder before just comparing by filename. This is needed
        // because NaturalSortComparer does not work with paths and doesn't seem 001.jpg as before chapter 1/001.jpg.
        var fullNames = entryFullNames
            .Where(path => !(Path.EndsInDirectorySeparator(path) || Tasks.Scanner.Parser.Parser.HasBlacklistedFolderInPath(path) || path.StartsWith(Tasks.Scanner.Parser.Parser.MacOsMetadataFileStartsWith)) && Tasks.Scanner.Parser.Parser.IsImage(path))
            .OrderByNatural(c => c.GetFullPathWithoutExtension())
            .ToList();
        if (fullNames.Count == 0) return null;

        var nonNestedFile = fullNames.Where(entry => (Path.GetDirectoryName(entry) ?? string.Empty).Equals(archiveName))
            .OrderByNatural(c => c.GetFullPathWithoutExtension())
            .FirstOrDefault();

        if (!string.IsNullOrEmpty(nonNestedFile)) return nonNestedFile;

        // Check the first folder and sort within that to see if we can find a file, else fallback to first file with basic sort.
        // Get first folder, then sort within that
        var firstDirectoryFile = fullNames.OrderByNatural(Path.GetDirectoryName!).FirstOrDefault();
        if (!string.IsNullOrEmpty(firstDirectoryFile))
        {
            var firstDirectory = Path.GetDirectoryName(firstDirectoryFile);
            if (!string.IsNullOrEmpty(firstDirectory))
            {
                var firstDirectoryResult = fullNames.Where(f => firstDirectory.Equals(Path.GetDirectoryName(f)))
                    .OrderByNatural(Path.GetFileNameWithoutExtension)
                    .FirstOrDefault();

                if (!string.IsNullOrEmpty(firstDirectoryResult)) return firstDirectoryResult;
            }
        }

        var result = fullNames
            .OrderByNatural(Path.GetFileNameWithoutExtension)
            .FirstOrDefault();

        return string.IsNullOrEmpty(result) ? null : result;
    }


    /// <summary>
    /// Generates byte array of cover image.
    /// Given a path to a compressed file <see cref="Tasks.Scanner.Parser.Parser.ArchiveFileExtensions"/>, will ensure the first image (respects directory structure) is returned unless
    /// a folder/cover.(image extension) exists in the the compressed file (if duplicate, the first is chosen)
    ///
    /// This skips over any __MACOSX folder/file iteration.
    /// </summary>
    /// <remarks>This always creates a thumbnail</remarks>
    /// <param name="archivePath"></param>
    /// <param name="fileName">File name to use based on context of entity.</param>
    /// <param name="outputDirectory">Where to output the file, defaults to covers directory</param>
    /// <param name="format">When saving the file, use encoding</param>
    /// <returns></returns>
    public string GetCoverImage(FileMetadata archivePath, string fileName, string outputDirectory, EncodeFormat format, CoverImageSize size = CoverImageSize.Default)
    {
        if (string.IsNullOrEmpty(archivePath.Path) || !IsValidArchive(archivePath)) return string.Empty;
        try
        {
            var libraryHandler = CanOpen(archivePath);
            switch (libraryHandler)
            {
                case ArchiveLibrary.Default:
                {
                    using var archive = ZipFile.OpenRead(archivePath.Path);
                    var entries = GetEntries(archive.Entries, archivePath);
                    var entryName = string.IsNullOrEmpty(archivePath.CoverFile) ? FindCoverImageFilename(archivePath, entries.Select(e => e.FullName)) : archivePath.CoverFile;
                    var entry = entries.Single(e => e.FullName == entryName);

                    using var stream = entry.Open();
                    return _imageService.WriteCoverThumbnail(stream, fileName, outputDirectory, format, size);
                }
                case ArchiveLibrary.SharpCompress:
                {
                    using var archive = ArchiveFactory.Open(archivePath.Path);
                    var entries = GetEntries(archive.Entries, archivePath);
                    var entryNames = entries.Where(archiveEntry => !archiveEntry.IsDirectory).Select(e => e.Key).ToList();

                    var entryName = string.IsNullOrEmpty(archivePath.CoverFile) ? FindCoverImageFilename(archivePath, entryNames) : archivePath.CoverFile;
                    var entry = entries.Single(e => e.Key == entryName);

                    using var stream = entry.OpenEntryStream();
                    return _imageService.WriteCoverThumbnail(stream, fileName, outputDirectory, format, size);
                }
                case ArchiveLibrary.NotSupported:
                    _logger.LogWarning("[GetCoverImage] This archive cannot be read: {ArchivePath}. Defaulting to no cover image", archivePath);
                    return string.Empty;
                default:
                    _logger.LogWarning("[GetCoverImage] There was an exception when reading archive stream: {ArchivePath}. Defaulting to no cover image", archivePath);
                    return string.Empty;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GetCoverImage] There was an exception when reading archive stream: {ArchivePath}. Defaulting to no cover image", archivePath);
            _mediaErrorService.ReportMediaIssue(archivePath.Path, MediaErrorProducer.ArchiveService,
                "This archive cannot be read or not supported", ex); // TODO: Localize this
        }

        return string.Empty;
    }

    /// <summary>
    /// Given a list of image paths (assume within an archive), find the filename that corresponds to the cover
    /// </summary>
    /// <param name="archivePath"></param>
    /// <param name="entryNames"></param>
    /// <returns></returns>
    public static string? FindCoverImageFilename(FileMetadata archivePath, IEnumerable<string> entryNames)
    {
        var entryName = FindFolderEntry(entryNames) ?? FirstFileEntry(entryNames, Path.GetFileName(archivePath.Path));
        return entryName;
    }

    /// <summary>
    /// Given an archive stream, will assess whether directory needs to be flattened so that the extracted archive files are directly
    /// under extract path and not nested in subfolders. See <see cref="DirectoryService"/> Flatten method.
    /// </summary>
    /// <param name="archive">An opened archive stream</param>
    /// <returns></returns>
    public bool ArchiveNeedsFlattening(ZipArchive archive)
    {
        // Sometimes ZipArchive will list the directory and others it will just keep it in the FullName
        return archive.Entries.Count > 0 &&
               !Path.HasExtension(archive.Entries[0].FullName) ||
               archive.Entries.Any(e => e.FullName.Contains(Path.AltDirectorySeparatorChar) && !Tasks.Scanner.Parser.Parser.HasBlacklistedFolderInPath(e.FullName));
    }

    public async Task<Stream> CreateZipStream(FileMetadata archivePath) {
        if (!archivePath.HasPageRange()) {
            return File.OpenRead(archivePath.Path);
        }

        var memStream = new MemoryStream(0);
        try
        {
            if (!File.Exists(archivePath.Path)) {
                await memStream.DisposeAsync();
                return memStream;
            }

            var libraryHandler = CanOpen(archivePath);
            switch (libraryHandler)
            {
                case ArchiveLibrary.Default:
                {
                    using var archive = ZipFile.OpenRead(archivePath.Path);
                    using var destArchive = ArchiveFactory.Create(ArchiveType.Zip);

                    var entries = GetEntries(archive.Entries, archivePath);

                    if (archivePath.HasPageRange() && archivePath.MinRange() == 0) {
                        var allEntries = GetEntries(archive.Entries, new FileMetadata(archivePath.Path));
                        var infoEntry = allEntries.FirstOrDefault(x => (x.FullName ?? x.Name) == ComicInfoFilename) ??
                                        allEntries.FirstOrDefault(x => IsComicInfoArchiveEntry(x.FullName, x.Name));
                        
                        if (infoEntry != null) {
                            entries = entries.Append(infoEntry);
                        }
                    }

                    foreach(var entry in entries) {
                        using var stream = entry.Open();
                        var entryMemoryStream = new MemoryStream();
                        await stream.CopyToAsync(entryMemoryStream);
                        entryMemoryStream.Seek(0, SeekOrigin.Begin);
                        destArchive.AddEntry(entry.FullName, entryMemoryStream, true, entry.Length, entry.LastWriteTime.DateTime);
                    }

                    destArchive.SaveTo(memStream, new WriterOptions(CompressionType.Deflate) { LeaveStreamOpen = true });
                    memStream.Seek(0, SeekOrigin.Begin);
                    return memStream;
                }
                case ArchiveLibrary.SharpCompress:
                {
                    using var archive = ArchiveFactory.Open(archivePath.Path);
                    using var destArchive = ArchiveFactory.Create(archive.Type);
                    
                    var entries = GetEntries(archive.Entries, archivePath);

                    var compressionType = entries.First().CompressionType;
                    
                    if (archivePath.HasPageRange() && archivePath.MinRange() == 0) {
                        var allEntries = GetEntries(archive.Entries, new FileMetadata(archivePath.Path));
                        var infoEntry = allEntries.FirstOrDefault(x => x.Key == ComicInfoFilename) ??
                                        allEntries.FirstOrDefault(x => IsComicInfoArchiveEntry(Path.GetDirectoryName(x.Key), x.Key ?? string.Empty));
                        if (infoEntry != null) {
                            entries = entries.Append(infoEntry);
                        }
                    };
                    
                    foreach(var entry in entries) {
                        var entryMemoryStream = new MemoryStream();
                        using var stream  = entry.OpenEntryStream();
                        await stream.CopyToAsync(entryMemoryStream);
                        entryMemoryStream.Seek(0, SeekOrigin.Begin);
                        destArchive.AddEntry(entry.Key ?? "", entryMemoryStream, true, entry.Size, entry.LastModifiedTime);
                    }


                    destArchive.SaveTo(memStream, new WriterOptions(compressionType) { LeaveStreamOpen = true });
                    memStream.Seek(0, SeekOrigin.Begin);
                    return memStream;
                }
                case ArchiveLibrary.NotSupported:
                    _logger.LogWarning("[CreateZipStream] This archive cannot be read: {ArchivePath}", archivePath);
                    return memStream;
                default:
                    _logger.LogWarning(
                        "[CreateZipStream] There was an exception when reading archive stream: {ArchivePath}",
                        archivePath);
                    return memStream;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CreateZipStream] There was an exception when reading archive stream: {Filepath}", archivePath);
            _mediaErrorService.ReportMediaIssue(archivePath.Path, MediaErrorProducer.ArchiveService,
                "This archive cannot be read or not supported", ex);
        }

        return memStream;
    }

    /// <summary>
    /// Creates a zip file form the listed files and outputs to the temp folder.
    /// </summary>
    /// <param name="files">List of files to be zipped up. Should be full file paths.</param>
    /// <param name="tempFolder">Temp folder name to use for preparing the files. Will be created and deleted</param>
    /// <returns>Path to the temp zip</returns>
    /// <exception cref="KavitaException"></exception>
    public string CreateZipForDownload(IEnumerable<string> files, string tempFolder)
    {
        var dateString = DateTime.UtcNow.ToShortDateString().Replace("/", "_");

        var tempLocation = Path.Join(_directoryService.TempDirectory, $"{tempFolder}_{dateString}");
        var potentialExistingFile = _directoryService.FileSystem.FileInfo.New(Path.Join(_directoryService.TempDirectory, $"kavita_{tempFolder}_{dateString}.zip"));
        if (potentialExistingFile.Exists)
        {
            // A previous download exists, just return it immediately
            return potentialExistingFile.FullName;
        }

        _directoryService.ExistOrCreate(tempLocation);

        if (!_directoryService.CopyFilesToDirectory(files, tempLocation))
        {
            throw new KavitaException("bad-copy-files-for-download");
        }

        var zipPath = Path.Join(_directoryService.TempDirectory, $"kavita_{tempFolder}_{dateString}.zip");
        try
        {
            ZipFile.CreateFromDirectory(tempLocation, zipPath);
            // Remove the folder as we have the zip
            _directoryService.ClearAndDeleteDirectory(tempLocation);
        }
        catch (AggregateException ex)
        {
            _logger.LogError(ex, "There was an issue creating temp archive");
            throw new KavitaException("generic-create-temp-archive");
        }

        return zipPath;
    }

    public string CreateZipFromFoldersForDownload(IList<MangaFile> files, string tempFolder, Func<Tuple<string, float>, Task> progressCallback)
    {
        var dateString = DateTime.UtcNow.ToShortDateString().Replace("/", "_");

        var potentialExistingFile = _directoryService.FileSystem.FileInfo.New(Path.Join(_directoryService.TempDirectory, $"kavita_{tempFolder}_{dateString}.cbz"));
        if (potentialExistingFile.Exists)
        {
            // A previous download exists, just return it immediately
            return potentialExistingFile.FullName;
        }

        // Extract all the files to a temp directory and create zip on that
        var tempLocation = Path.Join(_directoryService.TempDirectory, $"{tempFolder}_{dateString}");
        var totalFiles = files.Count + 1;
        var count = 1f;
        try
        {
            _directoryService.ExistOrCreate(tempLocation);
            foreach (var file in files)
            {
                var tempPath = Path.Join(tempLocation, _directoryService.FileSystem.Path.GetFileNameWithoutExtension(_directoryService.FileSystem.FileInfo.New(file.FileMetadata.Path).Name));
                progressCallback(Tuple.Create(_directoryService.FileSystem.FileInfo.New(file.FileMetadata.Path).Name, (1.0f * totalFiles) / count));
                if (Tasks.Scanner.Parser.Parser.IsArchive(file.FileMetadata.Path))
                {
                    ExtractArchive(file.FileMetadata, tempPath);
                }
                else
                {
                    _directoryService.CopyFileToDirectory(file.FileMetadata.Path, tempPath);
                }

                count++;
            }
        }
        catch
        {
            throw new KavitaException("bad-copy-files-for-download");
        }

        var zipPath = Path.Join(_directoryService.TempDirectory, $"kavita_{tempFolder}_{dateString}.cbz");
        try
        {
            ZipFile.CreateFromDirectory(tempLocation, zipPath);
            // Remove the folder as we have the zip
            _directoryService.ClearAndDeleteDirectory(tempLocation);
        }
        catch (AggregateException ex)
        {
            _logger.LogError(ex, "There was an issue creating temp archive");
            throw new KavitaException("generic-create-temp-archive");
        }

        return zipPath;
    }


    /// <summary>
    /// Test if the archive path exists and an archive
    /// </summary>
    /// <param name="archivePath"></param>
    /// <returns></returns>
    public bool IsValidArchive(FileMetadata archivePath)
    {
        if (!File.Exists(archivePath.Path))
        {
            _logger.LogWarning("Archive {ArchivePath} could not be found", archivePath);
            return false;
        }

        if (Tasks.Scanner.Parser.Parser.IsArchive(archivePath.Path)) return true;

        _logger.LogWarning("Archive {ArchivePath} is not a valid archive", archivePath);
        return false;
    }

    private static bool IsComicInfoArchiveEntry(string? fullName, string name)
    {
        if (fullName == null) return false;
        return !Tasks.Scanner.Parser.Parser.HasBlacklistedFolderInPath(fullName)
               && name.EndsWith(ComicInfoFilename, StringComparison.OrdinalIgnoreCase)
               && !name.StartsWith(Tasks.Scanner.Parser.Parser.MacOsMetadataFileStartsWith);
    }

    /// <summary>
    /// This can be null if nothing is found or any errors occur during access
    /// </summary>
    /// <param name="archivePath"></param>
    /// <returns></returns>
    public ComicInfo? GetComicInfo(FileMetadata archivePath)
    {
        if (!IsValidArchive(archivePath)) return null;

        try
        {
            if (!File.Exists(archivePath.Path)) return null;

            var libraryHandler = CanOpen(archivePath);
            switch (libraryHandler)
            {
                case ArchiveLibrary.Default:
                {
                    using var archive = ZipFile.OpenRead(archivePath.Path);
                    var entries = GetEntries(archive.Entries, archivePath);
                    var entry = entries.FirstOrDefault(x => (x.FullName ?? x.Name) == ComicInfoFilename) ??
                        entries.FirstOrDefault(x => IsComicInfoArchiveEntry(x.FullName, x.Name));
                    if (entry != null)
                    {
                        using var stream = entry.Open();
                        return Deserialize(stream);
                    }

                    break;
                }
                case ArchiveLibrary.SharpCompress:
                {
                    using var archive = ArchiveFactory.Open(archivePath.Path);
                    var entries = GetEntries(archive.Entries, archivePath);
                    var entry = entries.FirstOrDefault(entry => entry.Key == ComicInfoFilename) ??
                        entries.FirstOrDefault(entry =>
                        IsComicInfoArchiveEntry(Path.GetDirectoryName(entry.Key), entry.Key));

                    if (entry != null)
                    {
                        using var stream = entry.OpenEntryStream();
                        var info = Deserialize(stream);
                        return info;
                    }

                    break;
                }
                case ArchiveLibrary.NotSupported:
                    _logger.LogWarning("[GetComicInfo] This archive cannot be read: {ArchivePath}", archivePath);
                    return null;
                default:
                    _logger.LogWarning(
                        "[GetComicInfo] There was an exception when reading archive stream: {ArchivePath}",
                        archivePath);
                    return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GetComicInfo] There was an exception when reading archive stream: {Filepath}", archivePath);
            _mediaErrorService.ReportMediaIssue(archivePath.Path, MediaErrorProducer.ArchiveService,
                "This archive cannot be read or not supported", ex);
        }

        return null;
    }

    /// <summary>
    /// Strips out empty tags before deserializing
    /// </summary>
    /// <param name="stream"></param>
    /// <returns></returns>
    private static ComicInfo? Deserialize(Stream stream)
    {
        var comicInfoXml = XDocument.Load(stream);
        comicInfoXml.Descendants()
            .Where(e => !e.Descendants().Any() && e.Name.ToString().ToUpper() != "PAGE" && (e.IsEmpty || string.IsNullOrWhiteSpace(e.Value)))
            .Remove();
            
        var serializer = new XmlSerializer(typeof(ComicInfo));
        using var reader = comicInfoXml.Root?.CreateReader();
        if (reader == null) return null;

        var info  = (ComicInfo?) serializer.Deserialize(reader);
        ComicInfo.CleanComicInfo(info);
        return info;

    }


    private void ExtractArchiveEntities(IEnumerable<IArchiveEntry> entries, string extractPath)
    {
        _directoryService.ExistOrCreate(extractPath);
        // TODO: Look into a Parallel.ForEach
        foreach (var entry in entries)
        {
            entry.WriteToDirectory(extractPath, new ExtractionOptions()
            {
                ExtractFullPath = true, // Don't flatten, let the flattener ensure correct order of nested folders
                Overwrite = false
            });
        }
    }

    private void ExtractArchiveEntries(ZipArchive archive, string extractPath,  IEnumerable<ZipArchiveEntry>? entries = null)
    {
        var needsFlattening = ArchiveNeedsFlattening(archive);
        if (!archive.HasFiles() && !needsFlattening) return;

        if (entries != null) {
            foreach (var entry in entries)
            {
                string destinationPath = Path.GetFullPath(Path.Combine(extractPath, entry.FullName));
                _directoryService.ExistOrCreate(Path.GetDirectoryName(destinationPath) ?? string.Empty);
                entry.ExtractToFile(destinationPath, true);
            }
        } else {
            archive.ExtractToDirectory(extractPath, true);
        }


        if (!needsFlattening) return;

        _logger.LogDebug("Extracted archive is nested in root folder, flattening...");
        _directoryService.Flatten(extractPath);
    }

    /// <summary>
    /// Extracts an archive to a temp cache directory. Returns path to new directory. If temp cache directory already exists,
    /// will return that without performing an extraction. Returns empty string if there are any invalidations which would
    /// prevent operations to perform correctly (missing archivePath file, empty archive, etc).
    /// </summary>
    /// <param name="archivePath">A valid file to an archive file.</param>
    /// <param name="extractPath">Path to extract to</param>
    /// <returns></returns>
    public void ExtractArchive(FileMetadata archivePath, string extractPath)
    {
        if (!IsValidArchive(archivePath)) return;

        if (_directoryService.FileSystem.Directory.Exists(extractPath)) return;

        if (!_directoryService.FileSystem.File.Exists(archivePath.Path))
        {
            _logger.LogError("{Archive} does not exist on disk", archivePath);
            throw new KavitaException($"{archivePath} does not exist on disk");
        }

        var sw = Stopwatch.StartNew();

        try
        {
            var libraryHandler = CanOpen(archivePath);
            switch (libraryHandler)
            {
                case ArchiveLibrary.Default:
                {
                    using var archive = ZipFile.OpenRead(archivePath.Path);
                    var entries = GetEntries(archive.Entries, archivePath);

                    if (archivePath.HasPageRange() && archivePath.MinRange() == 0) {
                        var allEntries = GetEntries(archive.Entries, new FileMetadata(archivePath.Path));
                        var infoEntry = allEntries.FirstOrDefault(x => (x.FullName ?? x.Name) == ComicInfoFilename) ??
                                        allEntries.FirstOrDefault(x => IsComicInfoArchiveEntry(x.FullName, x.Name));
                        
                        if (infoEntry != null) {
                            entries = entries.Append(infoEntry);
                        }
                    }
                    
                    ExtractArchiveEntries(archive,  extractPath, entries.Count() == archive.Entries.Count ? null : entries);
                    break;
                }
                case ArchiveLibrary.SharpCompress:
                {
                    using var archive = ArchiveFactory.Open(archivePath.Path);
                    var entries = GetEntries(archive.Entries, archivePath);

                    if (archivePath.HasPageRange() && archivePath.MinRange() == 0) {
                        var allEntries = GetEntries(archive.Entries, new FileMetadata(archivePath.Path));
                        var infoEntry = allEntries.FirstOrDefault(x => x.Key == ComicInfoFilename) ??
                                        allEntries.FirstOrDefault(x => IsComicInfoArchiveEntry(Path.GetDirectoryName(x.Key), x.Key ?? string.Empty));
                        if (infoEntry != null) {
                            entries = entries.Append(infoEntry);
                        }
                    };
                    
                    ExtractArchiveEntities(entries, extractPath);
                    break;
                }
                case ArchiveLibrary.NotSupported:
                    _logger.LogWarning("[ExtractArchive] This archive cannot be read: {ArchivePath}", archivePath);
                    return;
                default:
                    _logger.LogWarning("[ExtractArchive] There was an exception when reading archive stream: {ArchivePath}", archivePath);
                    return;
            }

        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ExtractArchive] There was a problem extracting {ArchivePath} to {ExtractPath}",archivePath, extractPath);
            _mediaErrorService.ReportMediaIssue(archivePath.Path, MediaErrorProducer.ArchiveService,
                "This archive cannot be read or not supported", ex);
            throw new KavitaException(
                $"There was an error when extracting {archivePath}. Check the file exists, has read permissions or the server OS can support all path characters.");
        }
        _logger.LogDebug("Extracted archive to {ExtractPath} in {ElapsedMilliseconds} milliseconds", extractPath, sw.ElapsedMilliseconds);
    }
}
