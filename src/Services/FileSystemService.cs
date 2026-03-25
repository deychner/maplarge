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
            var cacheKey = GetDirectoryCacheKey(path);

            // Try to get from cache first
            if (_memoryCache.TryGetValue(cacheKey, out DirectoryContentResponse? cachedResult))
            {
                _logger.LogDebug("Directory contents served from cache: {Path} (key: {CacheKey})", path, cacheKey);
                return cachedResult!;
            }

            var fullPath = GetSafePath(path);
            if (!Directory.Exists(fullPath))
            {
                throw new DirectoryNotFoundException($"Directory not found: {path}");
            }

            // Use Task.Run for CPU-intensive operations to avoid blocking
            var result = await Task.Run(() =>
            {
                var items = new ConcurrentBag<FileSystemItem>();
                var fileCount = 0;
                var directoryCount = 0;
                var totalSize = 0L;

                // Use EnumerateDirectories and EnumerateFiles for better memory efficiency
                // This avoids creating arrays of all entries upfront
                var directoryInfo = new DirectoryInfo(fullPath);
                var directories = directoryInfo.EnumerateDirectories();
                var files = directoryInfo.EnumerateFiles();

                // Configure parallel options with cancellation token
                var parallelOptions = GetParallelOptions(cancellationToken);

                // Process directories in parallel with streaming enumeration
                Parallel.ForEach(directories, parallelOptions, directory =>
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        var item = new FileSystemItem
                        {
                            Name = directory.Name,
                            Path = GetRelativePath(directory.FullName),
                            IsDirectory = true,
                            LastModified = directory.LastWriteTime,
                            Size = GetDirectorySize(directory.FullName, cancellationToken)
                        };
                        items.Add(item);
                        Interlocked.Increment(ref directoryCount);
                        Interlocked.Add(ref totalSize, item.Size);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Error processing directory: {Directory}", directory.FullName);
                    }
                });

                // Process files in parallel with streaming enumeration
                Parallel.ForEach(files, parallelOptions, file =>
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        var item = new FileSystemItem
                        {
                            Name = file.Name,
                            Path = GetRelativePath(file.FullName),
                            IsDirectory = false,
                            LastModified = file.LastWriteTime,
                            Size = file.Length
                        };
                        items.Add(item);
                        Interlocked.Increment(ref fileCount);
                        Interlocked.Add(ref totalSize, item.Size);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Error processing file: {File}", file.FullName);
                    }
                });

                // Sort: directories first, then files, both alphabetically
                var sortedItems = items.OrderBy(i => !i.IsDirectory).ThenBy(i => i.Name).ToList();

                return new DirectoryContentResponse
                {
                    CurrentPath = GetRelativePath(fullPath),
                    ParentPath = GetParentPath(path),
                    Items = sortedItems,
                    FileCount = fileCount,
                    DirectoryCount = directoryCount,
                    TotalSize = totalSize
                };
            }, cancellationToken);

            // Cache the result for 5 minutes and track the cache key
            _memoryCache.Set(cacheKey, result, TimeSpan.FromMinutes(5));
            _directoryCacheKeys.TryAdd(cacheKey, 0); // Track this cache key
            _logger.LogDebug("Directory contents cached for path: '{Path}' using cache key: '{CacheKey}'", path, cacheKey);

            return result;
        }

        public async Task<IList<FileSystemItem>> SearchFilesAsync(SearchRequest request, CancellationToken cancellationToken = default)
        {
            var cacheKey = GetSearchCacheKey(request);

            // Try to get from cache first
            if (_memoryCache.TryGetValue(cacheKey, out IList<FileSystemItem>? cachedResults))
            {
                _logger.LogDebug("Search results served from cache: {Query}", request.Query);
                return cachedResults!;
            }

            var searchPath = GetSafePath(request.Path ?? "");

            try
            {
                // Use Task.Run for file system operations to avoid blocking
                var results = await Task.Run(() =>
                {
                    var items = new ConcurrentBag<FileSystemItem>();
                    var searchOption = request.IncludeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                    var directoryInfo = new DirectoryInfo(searchPath);

                    // Configure parallel options with cancellation token
                    var parallelOptions = GetParallelOptions(cancellationToken);

                    // Search files and directories in parallel using streaming enumeration
                    var searchTasks = new List<Task>
                    {
                        // Search files with streaming enumeration
                        Task.Run(() =>
                        {
                            try
                            {
                                var files = directoryInfo.EnumerateFiles($"*{request.Query}*", searchOption);
                                Parallel.ForEach(files, parallelOptions, file =>
                                {
                                    cancellationToken.ThrowIfCancellationRequested();

                                    try
                                    {
                                        items.Add(new FileSystemItem
                                        {
                                            Name = file.Name,
                                            Path = GetRelativePath(file.FullName),
                                            IsDirectory = false,
                                            LastModified = file.LastWriteTime,
                                            Size = file.Length
                                        });
                                    }
                                    catch (Exception ex) when (ex is not OperationCanceledException)
                                    {
                                        _logger.LogWarning(ex, "Error processing file: {File}", file.FullName);
                                    }
                                });
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                _logger.LogWarning(ex, "Error searching files in: {Path}", searchPath);
                            }
                        }, cancellationToken),

                        // Search directories with streaming enumeration
                        Task.Run(() =>
                        {
                            try
                            {
                                var directories = directoryInfo.EnumerateDirectories($"*{request.Query}*", searchOption);
                                Parallel.ForEach(directories, parallelOptions, directory =>
                                {
                                    cancellationToken.ThrowIfCancellationRequested();

                                    try
                                    {
                                        items.Add(new FileSystemItem
                                        {
                                            Name = directory.Name,
                                            Path = GetRelativePath(directory.FullName),
                                            IsDirectory = true,
                                            LastModified = directory.LastWriteTime,
                                            Size = GetDirectorySize(directory.FullName, cancellationToken)
                                        });
                                    }
                                    catch (Exception ex) when (ex is not OperationCanceledException)
                                    {
                                        _logger.LogWarning(ex, "Error processing directory: {Directory}", directory.FullName);
                                    }
                                });
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                _logger.LogWarning(ex, "Error searching directories in: {Path}", searchPath);
                            }
                        }, cancellationToken)
                    };

                    // Wait for both search operations to complete
                    Task.WaitAll([.. searchTasks], cancellationToken);

                    return items.OrderBy(r => !r.IsDirectory).ThenBy(r => r.Name).ToList();
                }, cancellationToken);

                // Cache search results for 15 minutes
                _memoryCache.Set(cacheKey, results, TimeSpan.FromMinutes(15));
                _searchCacheKeys.TryAdd(cacheKey, 0); // Use 0 as placeholder value
                _logger.LogDebug("Search results cached: {Query} for 15 minutes", request.Query);

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
            {
                throw new FileNotFoundException($"File not found: {filePath}");
            }

            // Use async file reading to avoid blocking
            return await File.ReadAllBytesAsync(fullPath, cancellationToken);
        }

        public async Task<string> UploadFileAsync(IFormFile file, string targetDirectory, CancellationToken cancellationToken = default)
        {
            var targetPath = GetSafePath(targetDirectory);

            if (!Directory.Exists(targetPath))
            {
                Directory.CreateDirectory(targetPath);
            }

            var fileName = file.FileName;
            var filePath = Path.Combine(targetPath, fileName);

            // Handle duplicate file names
            var counter = 1;
            var originalFileName = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);

            while (File.Exists(filePath))
            {
                fileName = $"{originalFileName}_{counter}{extension}";
                filePath = Path.Combine(targetPath, fileName);
                counter++;
            }

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream, cancellationToken);
            }

            // Invalidate directory cache for the target directory
            await InvalidateDirectoryCacheAsync(targetDirectory, cancellationToken);
            _logger.LogDebug("Cache invalidated after file upload: {Directory}", targetDirectory);

            return GetRelativePath(filePath);
        }

        public async Task<bool> DeleteFileOrDirectoryAsync(string path, CancellationToken cancellationToken = default)
        {
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                var fullPath = GetSafePath(path);
                var parentPath = GetParentPath(path);

                try
                {
                    if (File.Exists(fullPath))
                    {
                        await Task.Run(() => File.Delete(fullPath), cancellationToken);

                        // Invalidate cache for the parent directory
                        await InvalidateDirectoryCacheAsync(parentPath ?? "/", cancellationToken);
                        _logger.LogDebug("Cache invalidated after file deletion: {Path}", path);

                        return true;
                    }
                    else if (Directory.Exists(fullPath))
                    {
                        await Task.Run(() => Directory.Delete(fullPath, true), cancellationToken);

                        // Comprehensive cache invalidation for directory deletion
                        await InvalidateDirectoryCacheAsync(path, cancellationToken);
                        await InvalidateDirectoryCacheAsync(parentPath ?? "/", cancellationToken);
                        _logger.LogDebug("Cache invalidated after directory deletion: {Path}", path);

                        return true;
                    }
                    return false;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error deleting: {Path}", path);
                    return false;
                }
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

                        // Invalidate cache for both source and destination directories
                        var sourceParent = GetParentPath(sourcePath);
                        var destinationParent = GetParentPath(destinationPath);
                        await InvalidateDirectoryCacheAsync(sourceParent ?? "/", cancellationToken);
                        await InvalidateDirectoryCacheAsync(destinationParent ?? "/", cancellationToken);
                        _logger.LogDebug("Cache invalidated after file move from {Source} to {Destination}", sourcePath, destinationPath);

                        return true;
                    }
                    else if (Directory.Exists(fullSourcePath))
                    {
                        await Task.Run(() => Directory.Move(fullSourcePath, fullDestinationPath), cancellationToken);

                        // Invalidate cache for source, destination, and their parent directories
                        var sourceParent = GetParentPath(sourcePath);
                        var destinationParent = GetParentPath(destinationPath);
                        await InvalidateDirectoryCacheAsync(sourceParent ?? "/", cancellationToken);
                        await InvalidateDirectoryCacheAsync(destinationParent ?? "/", cancellationToken);
                        await InvalidateDirectoryCacheAsync(sourcePath, cancellationToken);
                        await InvalidateDirectoryCacheAsync(destinationPath, cancellationToken);
                        _logger.LogDebug("Cache invalidated after directory move from {Source} to {Destination}", sourcePath, destinationPath);

                        return true;
                    }
                    return false;
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

                        // Invalidate cache for the destination directory
                        var destinationParent = GetParentPath(destinationPath);
                        await InvalidateDirectoryCacheAsync(destinationParent ?? "/", cancellationToken);
                        _logger.LogDebug("Cache invalidated after file copy to {Destination}", destinationPath);

                        return true;
                    }
                    else if (Directory.Exists(fullSourcePath))
                    {
                        await Task.Run(() => CopyDirectory(fullSourcePath, fullDestinationPath, cancellationToken), cancellationToken);

                        // Invalidate cache for the destination directory and the copied directory
                        var destinationParent = GetParentPath(destinationPath);
                        await InvalidateDirectoryCacheAsync(destinationParent ?? "/", cancellationToken);
                        await InvalidateDirectoryCacheAsync(destinationPath, cancellationToken);
                        _logger.LogDebug("Cache invalidated after directory copy to {Destination}", destinationPath);

                        return true;
                    }
                    return false;
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
        private static string GetDirectoryCacheKey(string path)
        {
            var normalizedPath = string.IsNullOrEmpty(path) || path == "/" ? "" : path.TrimStart('/');
            var cacheKey = $"directory_contents_{normalizedPath}";
            return cacheKey;
        }

        private static string GetSearchCacheKey(SearchRequest request)
        {
            var normalizedPath = string.IsNullOrEmpty(request.Path) || request.Path == "/" ? "" : request.Path.TrimStart('/');
            return $"search_{request.Query}_{normalizedPath}_{request.IncludeSubdirectories}";
        }

        private async Task InvalidateDirectoryCacheAsync(string path, CancellationToken cancellationToken = default)
        {
            var cacheKey = GetDirectoryCacheKey(path);
            _memoryCache.Remove(cacheKey);
            _directoryCacheKeys.TryRemove(cacheKey, out _);

            _logger.LogDebug("Invalidated directory cache for path: '{Path}' using cache key: '{CacheKey}'", path, cacheKey);

            // Also invalidate search cache entries that might be affected
            await InvalidateSearchCacheAsync(cancellationToken);
        }

        private async Task InvalidateSearchCacheAsync(CancellationToken cancellationToken = default)
        {
            // Remove all tracked search cache keys in a thread-safe manner
            await Task.Run(() =>
            {
                // Use more efficient collection operations
                var keysToRemove = new List<string>(_searchCacheKeys.Count);

                // Collect keys first to avoid collection modification during enumeration
                foreach (var kvp in _searchCacheKeys)
                {
                    keysToRemove.Add(kvp.Key);
                }

                // Parallel removal for better performance with many cache keys
                if (keysToRemove.Count > 10)
                {
                    Parallel.ForEach(keysToRemove, GetParallelOptions(cancellationToken), key =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        _memoryCache.Remove(key);
                        _searchCacheKeys.TryRemove(key, out _);
                    });
                }
                else
                {
                    // Sequential for small counts to avoid parallel overhead
                    foreach (var key in keysToRemove)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        _memoryCache.Remove(key);
                        _searchCacheKeys.TryRemove(key, out _);
                    }
                }

                if (keysToRemove.Count > 0)
                {
                    _logger.LogDebug("Invalidated {Count} search cache entries", keysToRemove.Count);
                }
            }, cancellationToken);
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