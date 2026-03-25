using Microsoft.Extensions.Caching.Memory;
using System.Collections.Concurrent;
using TestProject.Models;

namespace TestProject.Services
{
    public class FileSystemService : IFileSystemService, IDisposable
    {
        private readonly string _homeDirectory;
        private readonly ILogger<FileSystemService> _logger;
        private readonly IMemoryCache _memoryCache;
        private readonly ConcurrentDictionary<string, byte> _searchCacheKeys = new();
        private readonly ConcurrentDictionary<string, byte> _directoryCacheKeys = new();
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private readonly int _maxDegreeOfParallelism;
        private bool _disposed = false;

        public FileSystemService(IConfiguration configuration, ILogger<FileSystemService> logger, IMemoryCache memoryCache)
        {
            _homeDirectory = configuration["FileSystem:HomeDirectory"] ?? Environment.CurrentDirectory;
            _logger = logger;
            _memoryCache = memoryCache;

            // Configure parallel processing settings
            var maxDegreeOfParallelism = configuration.GetValue("ParallelProcessing:MaxDegreeOfParallelism", 0);
            _maxDegreeOfParallelism = maxDegreeOfParallelism <= 0 ? Environment.ProcessorCount : maxDegreeOfParallelism;

            // Ensure home directory exists
            if (!Directory.Exists(_homeDirectory))
            {
                Directory.CreateDirectory(_homeDirectory);
            }
        }

        public async Task<DirectoryContentResponse> GetDirectoryContentsAsync(string path, CancellationToken cancellationToken = default)
        {
            var cacheKey = GetCacheKey("directory_contents", path);

            if (_memoryCache.TryGetValue(cacheKey, out DirectoryContentResponse? cachedResult))
            {
                _logger.LogDebug("Directory contents served from cache: Path='{Path}', CacheKey='{CacheKey}'", path, cacheKey);
                return cachedResult!;
            }

            var fullPath = GetSafePath(path);
            if (!Directory.Exists(fullPath))
                throw new DirectoryNotFoundException($"Directory not found: {path}");

            var directoryInfo = new DirectoryInfo(fullPath);
            var items = new ConcurrentBag<FileSystemItem>();
            var fileCount = 0;
            var directoryCount = 0;
            var totalSize = 0L;

            // Process all items in parallel
            var allItems = directoryInfo.EnumerateFileSystemInfos();
            await Task.Run(() => 
                Parallel.ForEach(allItems, GetParallelOptions(cancellationToken), item =>
                    ProcessFileSystemItem(item, items, ref fileCount, ref directoryCount, ref totalSize, cancellationToken)), cancellationToken);

            var sortedItems = items.OrderBy(i => !i.IsDirectory).ThenBy(i => i.Name).ToList();

            var result = new DirectoryContentResponse
            {
                CurrentPath = GetRelativePath(fullPath),
                ParentPath = GetParentPath(path),
                Items = sortedItems,
                FileCount = fileCount,
                DirectoryCount = directoryCount,
                TotalSize = totalSize
            };

            CacheResult(cacheKey, result, TimeSpan.FromMinutes(15), _directoryCacheKeys);
            return result;
        }

        public async Task<IList<FileSystemItem>> SearchFilesAsync(SearchRequest request, CancellationToken cancellationToken = default)
        {
            var cacheKey = GetCacheKey("search", request.Path ?? "/", request.Query, request.IncludeSubdirectories.ToString());

            if (_memoryCache.TryGetValue(cacheKey, out IList<FileSystemItem>? cachedResults))
            {
                _logger.LogDebug("Search results served from cache: Query='{Query}', Path='{Path}', CacheKey='{CacheKey}'", request.Query, request.Path ?? "/", cacheKey);
                return cachedResults!;
            }

            var searchPath = GetSafePath(request.Path ?? "");
            var searchOption = request.IncludeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var items = new ConcurrentBag<FileSystemItem>();

            try
            {
                var directoryInfo = new DirectoryInfo(searchPath);
                var pattern = $"*{request.Query}*";

                // Search both files and directories in parallel
                await Task.Run(() =>
                {
                    var allItems = directoryInfo.EnumerateFileSystemInfos(pattern, searchOption);
                    Parallel.ForEach(allItems, GetParallelOptions(cancellationToken), item =>
                        ProcessFileSystemItemSimple(item, items, cancellationToken));
                }, cancellationToken);

                var results = items.OrderBy(r => !r.IsDirectory).ThenBy(r => r.Name).ToList();

                CacheResult(cacheKey, results, TimeSpan.FromMinutes(15), _searchCacheKeys);
                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching files with query: {Query}", request.Query);
                throw;
            }
        }

        public async Task<byte[]> DownloadFileAsync(string filePath, CancellationToken cancellationToken = default)
        {
            var fullPath = GetSafePath(filePath);

            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"File not found: {filePath}");

            return await File.ReadAllBytesAsync(fullPath, cancellationToken);
        }

        public async Task<string> UploadFileAsync(IFormFile file, string targetDirectory, CancellationToken cancellationToken = default)
        {
            var targetPath = GetSafePath(targetDirectory);

            if (!Directory.Exists(targetPath))
                Directory.CreateDirectory(targetPath);

            var filePath = GetUniqueFilePath(targetPath, file.FileName);

            using var stream = new FileStream(filePath, FileMode.Create);
            await file.CopyToAsync(stream, cancellationToken);

            await InvalidateCacheAsync(targetDirectory, cancellationToken);
            return GetRelativePath(filePath);
        }

        public async Task<bool> DeleteFileOrDirectoryAsync(string path, CancellationToken cancellationToken = default)
        {
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                var fullPath = GetSafePath(path);
                var parentPath = GetParentPath(path);

                if (File.Exists(fullPath))
                {
                    await Task.Run(() => File.Delete(fullPath), cancellationToken);
                    await InvalidateCacheAsync(parentPath ?? "/", cancellationToken);
                    return true;
                }
                else if (Directory.Exists(fullPath))
                {
                    await Task.Run(() => Directory.Delete(fullPath, true), cancellationToken);
                    await InvalidateCacheAsync(path, cancellationToken);
                    await InvalidateCacheAsync(parentPath ?? "/", cancellationToken);
                    return true;
                }
                else
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting: {Path}", path);
                return false;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task<bool> MoveFileOrDirectoryAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        {
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                var fullSourcePath = GetSafePath(sourcePath);
                var fullDestinationPath = GetSafePath(destinationPath);

                try
                {
                    if (File.Exists(fullSourcePath))
                    {
                        await Task.Run(() => File.Move(fullSourcePath, fullDestinationPath), cancellationToken);

                        var sourceParent = GetParentPath(sourcePath);
                        var destinationParent = GetParentPath(destinationPath);
                        await InvalidateCacheAsync(sourceParent ?? "/", cancellationToken);
                        await InvalidateCacheAsync(destinationParent ?? "/", cancellationToken);

                        return true;
                    }
                    else if (Directory.Exists(fullSourcePath))
                    {
                        await Task.Run(() => Directory.Move(fullSourcePath, fullDestinationPath), cancellationToken);

                        var sourceParent = GetParentPath(sourcePath);
                        var destinationParent = GetParentPath(destinationPath);
                        await InvalidateCacheAsync(sourceParent ?? "/", cancellationToken);
                        await InvalidateCacheAsync(destinationParent ?? "/", cancellationToken);
                        await InvalidateCacheAsync(sourcePath, cancellationToken);
                        await InvalidateCacheAsync(destinationPath, cancellationToken);

                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error moving from {Source} to {Destination}", sourcePath, destinationPath);
                    return false;
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task<bool> CopyFileOrDirectoryAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        {
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                var fullSourcePath = GetSafePath(sourcePath);
                var fullDestinationPath = GetSafePath(destinationPath);

                try
                {
                    if (File.Exists(fullSourcePath))
                    {
                        await Task.Run(() => File.Copy(fullSourcePath, fullDestinationPath, true), cancellationToken);

                        var destinationParent = GetParentPath(destinationPath);
                        await InvalidateCacheAsync(destinationParent ?? "/", cancellationToken);

                        return true;
                    }
                    else if (Directory.Exists(fullSourcePath))
                    {
                        await Task.Run(() => CopyDirectory(fullSourcePath, fullDestinationPath, cancellationToken), cancellationToken);

                        var destinationParent = GetParentPath(destinationPath);
                        await InvalidateCacheAsync(destinationParent ?? "/", cancellationToken);
                        await InvalidateCacheAsync(destinationPath, cancellationToken);

                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error copying from {Source} to {Destination}", sourcePath, destinationPath);
                    return false;
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private string GetSafePath(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath) || relativePath == "/")
                return _homeDirectory;

            // Remove leading slash and normalize separators
            relativePath = relativePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);

            var fullPath = Path.Combine(_homeDirectory, relativePath);

            // Ensure the path is within the home directory (security check)
            var normalizedHome = Path.GetFullPath(_homeDirectory);
            var normalizedPath = Path.GetFullPath(fullPath);

            if (!normalizedPath.StartsWith(normalizedHome))
            {
                throw new UnauthorizedAccessException("Access denied: Path is outside allowed directory");
            }

            return normalizedPath;
        }

        private string GetRelativePath(string fullPath)
        {
            var relativePath = Path.GetRelativePath(_homeDirectory, fullPath);
            
            // Handle the case when we're at the root directory
            if (relativePath == ".")
                return "/";
            
            return "/" + relativePath.Replace(Path.DirectorySeparatorChar, '/');
        }

        private static string? GetParentPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "/")
                return null;

            var parentPath = Path.GetDirectoryName(path.TrimEnd('/'));
            return string.IsNullOrEmpty(parentPath) ? "/" : parentPath.Replace(Path.DirectorySeparatorChar, '/');
        }

        private long GetDirectorySize(string directoryPath, CancellationToken cancellationToken = default)
        {
            try
            {
                var dirInfo = new DirectoryInfo(directoryPath);
                if (!dirInfo.Exists) return 0;

                // Use EnumerateFiles for single-pass calculation - much more memory efficient
                // This avoids creating an array of all FileInfo objects upfront
                var files = dirInfo.EnumerateFiles("*", SearchOption.AllDirectories);

                long totalSize = 0;
                var fileCount = 0;

                // Process files in a single enumeration
                Parallel.ForEach(files, GetParallelOptions(cancellationToken), file =>
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        Interlocked.Add(ref totalSize, file.Length);
                        Interlocked.Increment(ref fileCount);
                    }
                    catch
                    {
                        // Ignore files that can't be accessed (permissions, etc.)
                    }
                });

                return totalSize;
            }
            catch (OperationCanceledException)
            {
                throw; // Re-throw cancellation exceptions
            }
            catch
            {
                return 0;
            }
        }

        private void CopyDirectory(string sourceDir, string destinationDir, CancellationToken cancellationToken = default)
        {
            var dir = new DirectoryInfo(sourceDir);
            if (!dir.Exists)
                throw new DirectoryNotFoundException($"Source directory not found: {sourceDir}");

            Directory.CreateDirectory(destinationDir);

            // Use streaming enumeration for better memory efficiency
            var files = dir.EnumerateFiles();
            var subdirectories = dir.EnumerateDirectories();

            // Copy files in parallel with streaming enumeration
            Parallel.ForEach(files, GetParallelOptions(cancellationToken), file =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var targetFilePath = Path.Combine(destinationDir, file.Name);
                    file.CopyTo(targetFilePath);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Failed to copy file: {Source} to {Destination}",
                        file.FullName, Path.Combine(destinationDir, file.Name));
                }
            });

            // Recursively copy subdirectories - could also be parallelized for very large directory trees
            foreach (var subDir in subdirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var newDestinationDir = Path.Combine(destinationDir, subDir.Name);
                    CopyDirectory(subDir.FullName, newDestinationDir, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Failed to copy directory: {Source} to {Destination}",
                        subDir.FullName, Path.Combine(destinationDir, subDir.Name));
                }
            }
        }

        // Cache helper methods
        private static string GetCacheKey(string prefix, params string[] parts)
        {
            var normalizedParts = parts.Select(p => string.IsNullOrEmpty(p) || p == "/" ? "root" : p.TrimStart('/'));
            return $"{prefix}_{string.Join("_", normalizedParts)}";
        }

        private void CacheResult<T>(string cacheKey, T result, TimeSpan expiration, ConcurrentDictionary<string, byte> keyTracker)
        {
            _memoryCache.Set(cacheKey, result, expiration);
            keyTracker.TryAdd(cacheKey, 0);

            var itemType = result switch
            {
                DirectoryContentResponse => "DirectoryContents",
                IList<FileSystemItem> => "SearchResults", 
                _ => typeof(T).Name
            };

            _logger.LogDebug("Cached {ItemType}: CacheKey='{CacheKey}', Expiration='{Expiration}'", itemType, cacheKey, expiration);
        }

        private void ProcessFileSystemItem(FileSystemInfo item, ConcurrentBag<FileSystemItem> items, ref int fileCount, ref int directoryCount, ref long totalSize, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var fileSystemItem = new FileSystemItem
                {
                    Name = item.Name,
                    Path = GetRelativePath(item.FullName),
                    IsDirectory = item is DirectoryInfo,
                    LastModified = item.LastWriteTime,
                    Size = item is DirectoryInfo dir ? GetDirectorySize(dir.FullName, cancellationToken) : ((FileInfo)item).Length
                };

                items.Add(fileSystemItem);

                if (fileSystemItem.IsDirectory)
                    Interlocked.Increment(ref directoryCount);
                else
                    Interlocked.Increment(ref fileCount);

                Interlocked.Add(ref totalSize, fileSystemItem.Size);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Error processing item: {Item}", item.FullName);
            }
        }

        private void ProcessFileSystemItemSimple(FileSystemInfo item, ConcurrentBag<FileSystemItem> items, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var fileSystemItem = new FileSystemItem
                {
                    Name = item.Name,
                    Path = GetRelativePath(item.FullName),
                    IsDirectory = item is DirectoryInfo,
                    LastModified = item.LastWriteTime,
                    Size = item is DirectoryInfo dir ? GetDirectorySize(dir.FullName, cancellationToken) : ((FileInfo)item).Length
                };

                items.Add(fileSystemItem);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Error processing item: {Item}", item.FullName);
            }
        }

        private static string GetUniqueFilePath(string directory, string fileName)
        {
            var filePath = Path.Combine(directory, fileName);

            if (!File.Exists(filePath))
                return filePath;

            var originalFileName = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);
            var counter = 1;

            do
            {
                var uniqueFileName = $"{originalFileName}_{counter}{extension}";
                filePath = Path.Combine(directory, uniqueFileName);
                counter++;
            } while (File.Exists(filePath));

            return filePath;
        }

        private async Task InvalidateCacheAsync(string path, CancellationToken cancellationToken = default)
        {
            // Invalidate directory cache
            var cacheKey = GetCacheKey("directory_contents", path);
            _memoryCache.Remove(cacheKey);
            _directoryCacheKeys.TryRemove(cacheKey, out _);

            // Invalidate search cache
            await Task.Run(() =>
            {
                var keysToRemove = _searchCacheKeys.Keys.ToList();

                Parallel.ForEach(keysToRemove, GetParallelOptions(cancellationToken), key =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _memoryCache.Remove(key);
                    _searchCacheKeys.TryRemove(key, out _);
                });

                if (keysToRemove.Count > 0)
                    _logger.LogDebug("Invalidated SearchCache: Count={Count}, CacheKeys=[{CacheKeys}]", keysToRemove.Count, string.Join(", ", keysToRemove.Take(5)) + (keysToRemove.Count > 5 ? "..." : ""));
            }, cancellationToken);

            _logger.LogDebug("Cache invalidated: Path='{Path}', DirectoryCacheKey='{CacheKey}'", path, cacheKey);
        }

        private ParallelOptions GetParallelOptions(CancellationToken token)
        {
            return new ParallelOptions
            {
                CancellationToken = token,
                MaxDegreeOfParallelism = _maxDegreeOfParallelism
            };
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _semaphore?.Dispose();
                _disposed = true;
            }
        }
    }
}