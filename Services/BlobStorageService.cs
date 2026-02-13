using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using System.Collections.Concurrent;

namespace fluid_durable_agent.Services;

public class BlobStorageService
{
    private readonly BlobServiceClient _blobServiceClient;
    private readonly string _defaultContainerName;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly TimeSpan _cacheExpiration = TimeSpan.FromMinutes(15);
    private const int MaxCacheSize = 100;

    private class CacheEntry
    {
        public string Content { get; set; } = string.Empty;
        public DateTime ExpiresAt { get; set; }
        public DateTime LastAccessedAt { get; set; }
    }

    public BlobStorageService(IConfiguration configuration)
    {
        if (configuration == null)
        {
            throw new ArgumentNullException(nameof(configuration), "IConfiguration is null");
        }

        string connectionString = configuration["BlobStorageConnectionString"] 
            ?? throw new InvalidOperationException("BlobStorageConnectionString not found in configuration");
        
        _defaultContainerName = configuration["BlobStorageContainerName"] ?? "documents";
        
        try
        {
            _blobServiceClient = new BlobServiceClient(connectionString);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to create BlobServiceClient with connection string: {connectionString}", ex);
        }
    }

    /// <summary>
    /// Gets a blob container client for the specified or default container
    /// </summary>
    /// <param name="containerName">Optional container name. Uses default if not specified.</param>
    public async Task<BlobContainerClient> GetContainerClientAsync(string? containerName = null)
    {
        var container = containerName ?? _defaultContainerName;
        var containerClient = _blobServiceClient.GetBlobContainerClient(container);
        await containerClient.CreateIfNotExistsAsync();
        return containerClient;
    }

    /// <summary>
    /// Reads a file from blob storage
    /// </summary>
    /// <param name="folderPath">The folder path within the container (e.g., "forms/v1")</param>
    /// <param name="fileName">The file name (e.g., "template.json")</param>
    /// <param name="containerName">Optional container name. Uses default if not specified.</param>
    /// <returns>The file content as a string</returns>
    public async Task<string> ReadFileAsync(string folderPath, string fileName, string? containerName = null)
    {
        string blobPath = string.IsNullOrEmpty(folderPath) 
            ? fileName 
            : $"{folderPath.TrimEnd('/')}/{fileName}";
        
        string cacheKey = GetCacheKey(blobPath, containerName);

        // Check cache first
        if (_cache.TryGetValue(cacheKey, out var cachedEntry))
        {
            if (DateTime.UtcNow < cachedEntry.ExpiresAt)
            {
                // Update last accessed time
                cachedEntry.LastAccessedAt = DateTime.UtcNow;
                return cachedEntry.Content;
            }
            else
            {
                // Remove expired entry
                _cache.TryRemove(cacheKey, out _);
            }
        }

        // Fetch from blob storage
        var containerClient = await GetContainerClientAsync(containerName);
        var blobClient = containerClient.GetBlobClient(blobPath);
        
        if (!await blobClient.ExistsAsync())
        {
            throw new FileNotFoundException($"Blob not found: {blobPath} at container {containerName ?? _defaultContainerName   }");
        }

        var response = await blobClient.DownloadContentAsync();
        string content = response.Value.Content.ToString();

        // Evict old entries if cache is full
        if (_cache.Count >= MaxCacheSize)
        {
            EvictOldestEntry();
        }

        // Cache the result
        var cacheEntry = new CacheEntry
        {
            Content = content,
            ExpiresAt = DateTime.UtcNow.Add(_cacheExpiration),
            LastAccessedAt = DateTime.UtcNow
        };
        _cache[cacheKey] = cacheEntry;

        return content;
    }

    /// <summary>
    /// Reads a file from blob storage as a stream
    /// </summary>
    /// <param name="folderPath">The folder path within the container</param>
    /// <param name="fileName">The file name</param>
    /// <param name="containerName">Optional container name. Uses default if not specified.</param>
    /// <returns>A stream of the file content</returns>
    public async Task<Stream> ReadFileAsStreamAsync(string folderPath, string fileName, string? containerName = null)
    {
        var containerClient = await GetContainerClientAsync(containerName);
        string blobPath = string.IsNullOrEmpty(folderPath) 
            ? fileName 
            : $"{folderPath.TrimEnd('/')}/{fileName}";
        
        var blobClient = containerClient.GetBlobClient(blobPath);
        
        if (!await blobClient.ExistsAsync())
        {
            throw new FileNotFoundException($"Blob not found: {blobPath}");
        }

        return await blobClient.OpenReadAsync();
    }

    /// <summary>
    /// Writes a file to blob storage
    /// </summary>
    /// <param name="folderPath">The folder path within the container</param>
    /// <param name="fileName">The file name</param>
    /// <param name="content">The content to write</param>
    /// <param name="containerName">Optional container name. Uses default if not specified.</param>
    /// <param name="overwrite">Whether to overwrite if the file exists</param>
    public async Task WriteFileAsync(string folderPath, string fileName, string content, string? containerName = null, bool overwrite = true)
    {
        var containerClient = await GetContainerClientAsync(containerName);
        string blobPath = string.IsNullOrEmpty(folderPath) 
            ? fileName 
            : $"{folderPath.TrimEnd('/')}/{fileName}";
        
        var blobClient = containerClient.GetBlobClient(blobPath);
        await blobClient.UploadAsync(BinaryData.FromString(content), overwrite);
    }

    /// <summary>
    /// Lists all files in a folder
    /// </summary>
    /// <param name="folderPath">The folder path within the container</param>
    /// <param name="containerName">Optional container name. Uses default if not specified.</param>
    /// <returns>List of blob names</returns>
    public async Task<List<string>> ListFilesAsync(string folderPath, string? containerName = null)
    {
        var containerClient = await GetContainerClientAsync(containerName);
        string prefix = string.IsNullOrEmpty(folderPath) 
            ? "" 
            : $"{folderPath.TrimEnd('/')}/";
        
        var blobs = new List<string>();
        await foreach (var blobItem in containerClient.GetBlobsAsync(prefix: prefix))
        {
            blobs.Add(blobItem.Name);
        }
        
        return blobs;
    }

    /// <summary>
    /// Checks if a file exists
    /// </summary>
    /// <param name="folderPath">The folder path within the container</param>
    /// <param name="fileName">The file name</param>
    /// <param name="containerName">Optional container name. Uses default if not specified.</param>
    /// <returns>True if the file exists</returns>
    public async Task<bool> FileExistsAsync(string folderPath, string fileName, string? containerName = null)
    {
        var containerClient = await GetContainerClientAsync(containerName);
        string blobPath = string.IsNullOrEmpty(folderPath) 
            ? fileName 
            : $"{folderPath.TrimEnd('/')}/{fileName}";
        
        var blobClient = containerClient.GetBlobClient(blobPath);
        return await blobClient.ExistsAsync();
    }

    /// <summary>
    /// Deletes a file from blob storage
    /// </summary>
    /// <param name="folderPath">The folder path within the container</param>
    /// <param name="fileName">The file name</param>
    /// <param name="containerName">Optional container name. Uses default if not specified.</param>
    public async Task DeleteFileAsync(string folderPath, string fileName, string? containerName = null)
    {
        var containerClient = await GetContainerClientAsync(containerName);
        string blobPath = string.IsNullOrEmpty(folderPath) 
            ? fileName 
            : $"{folderPath.TrimEnd('/')}/{fileName}";
        
        var blobClient = containerClient.GetBlobClient(blobPath);
        await blobClient.DeleteIfExistsAsync();

        // Invalidate cache
        string cacheKey = GetCacheKey(blobPath, containerName);
        _cache.TryRemove(cacheKey, out _);
    }

    /// <summary>
    /// Forces a refresh of a cached file by invalidating its cache entry and reloading from blob storage
    /// </summary>
    /// <param name="folderPath">The folder path within the container</param>
    /// <param name="fileName">The file name</param>
    /// <param name="containerName">Optional container name. Uses default if not specified.</param>
    /// <returns>The refreshed file content as a string</returns>
    public async Task<string> RefreshFileAsync(string folderPath, string fileName, string? containerName = null)
    {
        string blobPath = string.IsNullOrEmpty(folderPath) 
            ? fileName 
            : $"{folderPath.TrimEnd('/')}/{fileName}";
        
        string cacheKey = GetCacheKey(blobPath, containerName);
        
        // Remove from cache to force refresh
        _cache.TryRemove(cacheKey, out _);
        
        // Read file (which will cache the new version)
        return await ReadFileAsync(folderPath, fileName, containerName);
    }

    /// <summary>
    /// Clears all cached entries
    /// </summary>
    public void ClearCache()
    {
        _cache.Clear();
    }

    /// <summary>
    /// Clears the cache entry for a specific file
    /// </summary>
    /// <param name="folderPath">The folder path within the container</param>
    /// <param name="fileName">The file name</param>
    /// <param name="containerName">Optional container name. Uses default if not specified.</param>
    public void ClearCacheEntry(string folderPath, string fileName, string? containerName = null)
    {
        string blobPath = string.IsNullOrEmpty(folderPath) 
            ? fileName 
            : $"{folderPath.TrimEnd('/')}/{fileName}";
        
        string cacheKey = GetCacheKey(blobPath, containerName);
        _cache.TryRemove(cacheKey, out _);
    }

    /// <summary>
    /// Generates a cache key for a blob
    /// </summary>
    private string GetCacheKey(string blobPath, string? containerName)
    {
        string container = containerName ?? _defaultContainerName;
        return $"{container}:{blobPath}";
    }

    /// <summary>
    /// Evicts the least recently accessed entry from the cache
    /// </summary>
    private void EvictOldestEntry()
    {
        if (_cache.IsEmpty)
            return;

        var oldestKey = _cache
            .OrderBy(kvp => kvp.Value.LastAccessedAt)
            .FirstOrDefault()
            .Key;

        if (oldestKey != null)
        {
            _cache.TryRemove(oldestKey, out _);
        }
    }
}
