using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using API.DTOs.Stats;
using API.Entities;
using API.Entities.Enums;
using API.Structs;
using Microsoft.AspNetCore.StaticFiles;
using MimeTypes;

namespace API.Services;

public interface IDownloadService
{
    Task<Tuple<Stream, string, string>> GetFirstFileDownload(IEnumerable<MangaFile> files);
    string GetContentTypeFromFile(string filepath);
}
public class DownloadService : IDownloadService
{
    private readonly IArchiveService _archiveService;
    private readonly FileExtensionContentTypeProvider _fileTypeProvider = new FileExtensionContentTypeProvider();

    public DownloadService(IArchiveService archiveService) {
        _archiveService = archiveService;
     }

    /// <summary>
    /// Downloads the first file in the file enumerable for download
    /// </summary>
    /// <param name="files"></param>
    /// <returns></returns>
    public async Task<Tuple<Stream, string, string>> GetFirstFileDownload(IEnumerable<MangaFile> files)
    {
        var firstFile = files.First();
        Stream stream;
        if (firstFile.Format == MangaFormat.Archive && firstFile.FileMetadata.HasPageRange()) {
            stream = await _archiveService.CreateZipStream(firstFile.FileMetadata);
        } else {
            stream = File.OpenRead(firstFile.FileMetadata.Path);
        }
        return Tuple.Create(stream, GetContentTypeFromFile(firstFile.FileMetadata.Path), firstFile.GetDownloadName());
    }

    public string GetContentTypeFromFile(string filepath)
    {
        // Figures out what the content type should be based on the file name.
        if (!_fileTypeProvider.TryGetContentType(filepath, out var contentType))
        {
            if (contentType == null)
            {
                // Get extension
                contentType = Path.GetExtension(filepath);
            }

            contentType = Path.GetExtension(filepath).ToLowerInvariant() switch
            {
                ".cbz" => "application/x-cbz",
                ".cbr" => "application/x-cbr",
                ".cb7" => "application/x-cb7",
                ".cbt" => "application/x-cbt",
                ".epub" => "application/epub+zip",
                ".7z" => "application/x-7z-compressed",
                ".7zip" => "application/x-7z-compressed",
                ".rar" => "application/vnd.rar",
                ".zip" => "application/zip",
                ".tar.gz" => "application/gzip",
                ".pdf" => "application/pdf",
                _ => MimeTypeMap.GetMimeType(contentType)
            };
        }

        return contentType!;
    }


}
