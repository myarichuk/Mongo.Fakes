using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.GridFS;
using Xunit;

namespace Mongo.Fakes.Server.Tests.E2E;

public class GridFsE2ETests : IAsyncLifetime
{
    private MongoFakeServer? _server;
    private IMongoClient? _client;

    public async Task InitializeAsync()
    {
        var backend = new BsonFileBackend(Path.Combine(Directory.GetCurrentDirectory(), "Fixtures"));
        _server = new MongoFakeServer(backend, port: 0);
        await _server.StartAsync();

        var settings = new MongoClientSettings
        {
            DirectConnection = true,
            ServerSelectionTimeout = TimeSpan.FromSeconds(5),
            Server = new MongoServerAddress("127.0.0.1", _server.Port)
        };
        _client = new MongoClient(settings);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_server != null)
            await _server.DisposeAsync();
    }

    [Fact]
    public async Task UploadFromBytesAsync_Should_Store_And_DownloadAsBytesAsync_Should_Retrieve_Small_File()
    {
        var db = _client!.GetDatabase("testdb");
        var bucket = new GridFSBucket(db);

        var sourceBytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var fileId = await bucket.UploadFromBytesAsync("testfile.bin", sourceBytes);

        var downloadedBytes = await bucket.DownloadAsBytesAsync(fileId);

        Assert.Equal(sourceBytes, downloadedBytes);
    }

    [Fact]
    public async Task UploadFromBytesAsync_Should_Handle_Large_File_Spanning_Multiple_Chunks()
    {
        var db = _client!.GetDatabase("testdb");
        var bucket = new GridFSBucket(db);

        // Create a file larger than default chunk size (255KB)
        // Use 3x chunk size to ensure multiple chunks
        var sourceBytes = new byte[256 * 1024 * 3];
        for (int i = 0; i < sourceBytes.Length; i++)
        {
            sourceBytes[i] = (byte)(i % 256);
        }

        var fileId = await bucket.UploadFromBytesAsync("largefile.bin", sourceBytes);

        var downloadedBytes = await bucket.DownloadAsBytesAsync(fileId);

        Assert.Equal(sourceBytes, downloadedBytes);
    }

    [Fact]
    public async Task UploadFromStream_Should_Support_Streaming_Upload()
    {
        var db = _client!.GetDatabase("testdb");
        var bucket = new GridFSBucket(db);

        var sourceBytes = new byte[100 * 1024]; // 100KB
        for (int i = 0; i < sourceBytes.Length; i++)
        {
            sourceBytes[i] = (byte)(i % 256);
        }

        ObjectId fileId;
        using (var sourceStream = new MemoryStream(sourceBytes))
        {
            fileId = await bucket.UploadFromStreamAsync("stream_file.bin", sourceStream);
        }

        var downloadedBytes = await bucket.DownloadAsBytesAsync(fileId);

        Assert.Equal(sourceBytes, downloadedBytes);
    }

    [Fact]
    public async Task DownloadToStream_Should_Support_Streaming_Download()
    {
        var db = _client!.GetDatabase("testdb");
        var bucket = new GridFSBucket(db);

        var sourceBytes = new byte[50 * 1024]; // 50KB
        for (int i = 0; i < sourceBytes.Length; i++)
        {
            sourceBytes[i] = (byte)(i % 256);
        }

        var fileId = await bucket.UploadFromBytesAsync("download_stream.bin", sourceBytes);

        using var downloadStream = new MemoryStream();
        await bucket.DownloadToStreamAsync(fileId, downloadStream);

        Assert.Equal(sourceBytes, downloadStream.ToArray());
    }

    [Fact]
    public async Task OpenUploadStream_Should_Support_Chunked_Streaming_Upload()
    {
        var db = _client!.GetDatabase("testdb");
        var bucket = new GridFSBucket(db);

        var sourceBytes = new byte[200 * 1024]; // 200KB
        for (int i = 0; i < sourceBytes.Length; i++)
        {
            sourceBytes[i] = (byte)(i % 256);
        }

        ObjectId fileId;
        using (var uploadStream = await bucket.OpenUploadStreamAsync("chunked_upload.bin"))
        {
            await uploadStream.WriteAsync(sourceBytes, 0, sourceBytes.Length);
            fileId = uploadStream.Id;
        }

        var downloadedBytes = await bucket.DownloadAsBytesAsync(fileId);

        Assert.Equal(sourceBytes, downloadedBytes);
    }

    [Fact]
    public async Task OpenDownloadStream_Should_Support_Chunked_Streaming_Download()
    {
        var db = _client!.GetDatabase("testdb");
        var bucket = new GridFSBucket(db);

        var sourceBytes = new byte[150 * 1024]; // 150KB
        for (int i = 0; i < sourceBytes.Length; i++)
        {
            sourceBytes[i] = (byte)(i % 256);
        }

        var fileId = await bucket.UploadFromBytesAsync("chunked_download.bin", sourceBytes);

        using var downloadStream = await bucket.OpenDownloadStreamAsync(fileId);
        var downloadedBytes = new byte[sourceBytes.Length];
        int totalRead = 0;
        int bytesRead;
        while ((bytesRead = downloadStream.Read(downloadedBytes, totalRead, sourceBytes.Length - totalRead)) > 0)
        {
            totalRead += bytesRead;
        }

        Assert.Equal(sourceBytes, downloadedBytes);
    }

    [Fact]
    public async Task Find_Should_Filter_Files_By_Filename()
    {
        var db = _client!.GetDatabase("testdb");
        var bucket = new GridFSBucket(db);

        var testBytes = new byte[] { 1, 2, 3, 4, 5 };
        await bucket.UploadFromBytesAsync("file1.txt", testBytes);
        await bucket.UploadFromBytesAsync("file2.txt", testBytes);
        await bucket.UploadFromBytesAsync("other.bin", testBytes);

        var filter = Builders<GridFSFileInfo>.Filter.Eq("filename", "file1.txt");
        var cursor = await bucket.FindAsync(filter);
        var files = await cursor.ToListAsync();

        Assert.Single(files);
        Assert.Equal("file1.txt", files[0].Filename);
    }

    [Fact]
    public async Task DeleteAsync_Should_Remove_File_And_All_Chunks()
    {
        var db = _client!.GetDatabase("testdb");
        var bucket = new GridFSBucket(db);

        var sourceBytes = new byte[300 * 1024]; // 300KB, multiple chunks
        for (int i = 0; i < sourceBytes.Length; i++)
        {
            sourceBytes[i] = (byte)(i % 256);
        }

        var fileId = await bucket.UploadFromBytesAsync("delete_test.bin", sourceBytes);

        // Verify file exists
        var foundBefore = await bucket.FindAsync(Builders<GridFSFileInfo>.Filter.Eq("_id", fileId));
        var filesBefore = await foundBefore.ToListAsync();
        Assert.Single(filesBefore);

        // Delete file
        await bucket.DeleteAsync(fileId);

        // Verify file is gone
        var foundAfter = await bucket.FindAsync(Builders<GridFSFileInfo>.Filter.Eq("_id", fileId));
        var filesAfter = await foundAfter.ToListAsync();
        Assert.Empty(filesAfter);
    }

    [Fact]
    public async Task RenameAsync_Should_Update_File_Metadata()
    {
        var db = _client!.GetDatabase("testdb");
        var bucket = new GridFSBucket(db);

        var sourceBytes = new byte[] { 1, 2, 3, 4, 5 };
        var fileId = await bucket.UploadFromBytesAsync("original_name.txt", sourceBytes);

        await bucket.RenameAsync(fileId, "renamed_file.txt");

        var files = await bucket.FindAsync(Builders<GridFSFileInfo>.Filter.Eq("_id", fileId));
        var fileInfo = await files.FirstOrDefaultAsync();

        Assert.NotNull(fileInfo);
        Assert.Equal("renamed_file.txt", fileInfo.Filename);
    }

    [Fact]
    public async Task UploadFromBytesAsync_With_Metadata_Should_Store_And_Retrieve_Custom_Metadata()
    {
        var db = _client!.GetDatabase("testdb");
        var bucket = new GridFSBucket(db);

        var sourceBytes = new byte[] { 1, 2, 3, 4, 5 };
        var metadata = new BsonDocument
        {
            { "description", "Test file with metadata" },
            { "author", "TestAuthor" },
            { "tags", new BsonArray { "test", "metadata" } }
        };

        var options = new GridFSUploadOptions { Metadata = metadata };
        var fileId = await bucket.UploadFromBytesAsync("metadata_file.txt", sourceBytes, options);

        var files = await bucket.FindAsync(Builders<GridFSFileInfo>.Filter.Eq("_id", fileId));
        var fileInfo = await files.FirstOrDefaultAsync();

        Assert.NotNull(fileInfo);
        Assert.NotNull(fileInfo.Metadata);
        Assert.Equal("Test file with metadata", fileInfo.Metadata["description"].AsString);
        Assert.Equal("TestAuthor", fileInfo.Metadata["author"].AsString);
        Assert.Equal(2, fileInfo.Metadata["tags"].AsBsonArray.Count);
    }

    [Fact]
    public async Task DownloadAsBytesAsync_With_Unknown_FileId_Should_Throw_GridFSFileNotFoundException()
    {
        var db = _client!.GetDatabase("testdb");
        var bucket = new GridFSBucket(db);

        var unknownFileId = ObjectId.GenerateNewId();

        var ex = await Assert.ThrowsAsync<GridFSFileNotFoundException>(() =>
            bucket.DownloadAsBytesAsync(unknownFileId));

        Assert.NotNull(ex);
    }

    [Fact]
    public async Task Multiple_Uploads_Should_Maintain_Separate_Identities()
    {
        var db = _client!.GetDatabase("testdb");
        var bucket = new GridFSBucket(db);

        var bytes1 = new byte[] { 1, 2, 3 };
        var bytes2 = new byte[] { 4, 5, 6 };
        var bytes3 = new byte[] { 7, 8, 9 };

        var fileId1 = await bucket.UploadFromBytesAsync("file1.bin", bytes1);
        var fileId2 = await bucket.UploadFromBytesAsync("file2.bin", bytes2);
        var fileId3 = await bucket.UploadFromBytesAsync("file3.bin", bytes3);

        var downloaded1 = await bucket.DownloadAsBytesAsync(fileId1);
        var downloaded2 = await bucket.DownloadAsBytesAsync(fileId2);
        var downloaded3 = await bucket.DownloadAsBytesAsync(fileId3);

        Assert.Equal(bytes1, downloaded1);
        Assert.Equal(bytes2, downloaded2);
        Assert.Equal(bytes3, downloaded3);
    }
}
