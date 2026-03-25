using Microsoft.AspNetCore.Mvc;
using TestProject.Models;
using TestProject.Services;

namespace TestProject.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class FileSystemController(IFileSystemService fileSystemService, ILogger<FileSystemController> logger) : ControllerBase
    {
        private readonly IFileSystemService _fileSystemService = fileSystemService;
        private readonly ILogger<FileSystemController> _logger = logger;

        [HttpGet("browse")]
        public async Task<IActionResult> BrowseAsync([FromQuery] string path = "", CancellationToken cancellationToken = default)
        {
            try
            {
                var result = await _fileSystemService.GetDirectoryContentsAsync(path, cancellationToken);
                return Ok(result);
            }
            catch (DirectoryNotFoundException)
            {
                return NotFound(new { message = "The directory could not be found." });
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid("The requested directory could not be accessed.");
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Browse operation cancelled or timed out for path: {Path}", path);
                return StatusCode(499, new { message = "Request was cancelled or timed out" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error browsing directory: {Path}", path);
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        [HttpPost("search")]
        public async Task<IActionResult> SearchAsync([FromBody] SearchRequest request, CancellationToken cancellationToken)
        {
            try
            {
                var results = await _fileSystemService.SearchFilesAsync(request, cancellationToken);
                return Ok(results);
            }
            catch (OperationCanceledException)
            {
                return StatusCode(499, new { message = "Search was cancelled or timed out" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching files: {Query}", request.Query);
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        [HttpGet("download")]
        public async Task<IActionResult> DownloadAsync([FromQuery] string path, CancellationToken cancellationToken)
        {
            try
            {
                var fileBytes = await _fileSystemService.DownloadFileAsync(path, cancellationToken);
                var fileName = Path.GetFileName(path);
                return File(fileBytes, "application/octet-stream", fileName);
            }
            catch (FileNotFoundException ex)
            {
                return NotFound(new { message = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Forbid(ex.Message);
            }
            catch (OperationCanceledException)
            {
                return StatusCode(499, new { message = "Download was cancelled or timed out" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error downloading file: {Path}", path);
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        [HttpPost("upload")]
        public async Task<IActionResult> UploadAsync([FromForm] IFormFile file, [FromForm] string targetDirectory = "", CancellationToken cancellationToken = default)
        {
            if (file == null || file.Length == 0)
            {
                return BadRequest(new { message = "No file provided" });
            }

            try
            {
                var filePath = await _fileSystemService.UploadFileAsync(file, targetDirectory, cancellationToken);
                return Ok(new { path = filePath, message = "File uploaded successfully" });
            }
            catch (OperationCanceledException)
            {
                return StatusCode(499, new { message = "Upload was cancelled or timed out" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading file: {FileName}", file.FileName);
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        [HttpDelete("delete")]
        public async Task<IActionResult> DeleteAsync([FromQuery] string path, CancellationToken cancellationToken = default)
        {
            try
            {
                var success = await _fileSystemService.DeleteFileOrDirectoryAsync(path, cancellationToken);
                if (success)
                {
                    return Ok(new { message = "Deleted successfully" });
                }
                return NotFound(new { message = "File or directory not found" });
            }
            catch (OperationCanceledException)
            {
                return StatusCode(499, new { message = "Delete operation was cancelled or timed out" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting: {Path}", path);
                return StatusCode(500, new { message = "Internal server error" });
            }
        }
    }
}