using Newtonsoft.Json;

namespace TestProject.Models
{
    public class DirectoryContentResponse
    {
        [JsonProperty("current_path")]
        public string CurrentPath { get; set; } = string.Empty;

        [JsonProperty("parent_path")]
        public string? ParentPath { get; set; }

        [JsonProperty("items")]
        public IList<FileSystemItem> Items { get; set; } = [];

        [JsonProperty("file_count")]
        public int FileCount { get; set; }

        [JsonProperty("directory_count")]
        public int DirectoryCount { get; set; }

        [JsonProperty("total_size")]
        public long TotalSize { get; set; }
    }
}