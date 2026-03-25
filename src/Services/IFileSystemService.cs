using TestProject.Models;

namespace TestProject.Services
{
    public interface IFileSystemService
    {
        Task<DirectoryContentResponse> GetDirectoryContentsAsync(string path, CancellationToken cancellationToken = default);
        Task<IList<FileSystemItem>> SearchFilesAsync(SearchRequest request, CancellationToken cancellationToken = default);
        Task<byte[]> DownloadFileAsync(string filePath, CancellationToken cancellationToken = default);
        Task<string> UploadFileAsync(IFormFile file, string targetDirectory, CancellationToken cancellationToken = default);
        Task<bool> DeleteFileOrDirectoryAsync(string path, CancellationToken cancellationToken = default);
    }
}