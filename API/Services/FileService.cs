using System;
using System.IO;
using System.IO.Abstractions;
using System.Runtime.Intrinsics.Arm;
using System.Security.Cryptography;
using System.Text;
using System.Text.Unicode;
using API.Extensions;
using API.Structs;

namespace API.Services;

public interface IFileService
{
    IFileSystem GetFileSystem();
    bool HasFileBeenModifiedSince(FileMetadata fileMetadata, DateTime time);
    bool Exists(FileMetadata fileMetadata);
    bool ValidateSha(FileMetadata filepath, string sha);
}

public class FileService : IFileService
{
    private readonly IFileSystem _fileSystem;

    public FileService(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    public FileService() : this(fileSystem: new FileSystem()) { }

    public IFileSystem GetFileSystem()
    {
        return _fileSystem;
    }

    /// <summary>
    /// If the File on disk's last modified time is after passed time
    /// </summary>
    /// <remarks>This has a resolution to the minute. Will ignore seconds and milliseconds</remarks>
    /// <param name="fileMetadata">Full qualified path of file</param>
    /// <param name="time"></param>
    /// <returns></returns>
    public bool HasFileBeenModifiedSince(FileMetadata fileMetadata, DateTime time)
    {
        return !string.IsNullOrEmpty(fileMetadata.Path) && _fileSystem.File.GetLastWriteTime(fileMetadata.Path).Truncate(TimeSpan.TicksPerMinute) > time.Truncate(TimeSpan.TicksPerMinute);
    }

    public bool Exists(FileMetadata fileMetadata)
    {
        return _fileSystem.File.Exists(fileMetadata.Path);
    }

    /// <summary>
    /// Validates the Sha256 hash matches
    /// </summary>
    /// <param name="filepath"></param>
    /// <param name="sha"></param>
    /// <returns></returns>
    public bool ValidateSha(FileMetadata filepath, string sha)
    {
        if (!Exists(filepath)) return false;
        if (string.IsNullOrEmpty(sha)) throw new ArgumentException("Sha cannot be null");

        using var fs = _fileSystem.File.OpenRead(filepath.Path);
        fs.Position = 0;

        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var content = reader.ReadToEnd();

        // Compute SHA hash
        var checksum = SHA256.HashData(Encoding.UTF8.GetBytes(content));

        return BitConverter.ToString(checksum).Replace("-", string.Empty).Equals(sha);

    }
}
