using System;
using System.Collections.Generic;
using System.IO.Abstractions.TestingHelpers;
using API.Services;
using API.Structs;
using Xunit;

namespace API.Tests.Services;

public class FileSystemTests
{
    [Fact]
    public void FileHasNotBeenModifiedSinceCreation()
    {
        var file = new MockFileData("Testing is meh.")
        {
            LastWriteTime = DateTimeOffset.Now.Subtract(TimeSpan.FromMinutes(1))
        };
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            { @"c:\myfile.txt", file }
        });

        var fileService = new FileService(fileSystem);

        Assert.False(fileService.HasFileBeenModifiedSince(new FileMetadata(@"c:\myfile.txt"), DateTime.Now));
    }

    [Fact]
    public void FileHasBeenModifiedSinceCreation()
    {
        var file = new MockFileData("Testing is meh.")
        {
            LastWriteTime = DateTimeOffset.Now
        };
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            { @"c:\myfile.txt", file }
        });

        var fileService = new FileService(fileSystem);

        Assert.True(fileService.HasFileBeenModifiedSince(new FileMetadata(@"c:\myfile.txt"), DateTime.Now.Subtract(TimeSpan.FromMinutes(1))));
    }
}
