using Newtonsoft.Json;

namespace TestProject.Models
{
    public class FileSystemItem
    {
        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("path")]
        public string Path { get; set; } = string.Empty;

        [JsonProperty("size")]
        public long Size { get; set; }

        [JsonProperty("last_modified")]
        public DateTime LastModified { get; set; }

        [JsonProperty("is_directory")]
        public bool IsDirectory { get; set; }
    }
}