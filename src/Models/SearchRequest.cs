using Newtonsoft.Json;

namespace TestProject.Models
{
    public class SearchRequest
    {
        [JsonProperty("query")]
        public string Query { get; set; } = string.Empty;

        [JsonProperty("path")]
        public string? Path { get; set; }

        [JsonProperty("include_subdirectories")]
        public bool IncludeSubdirectories { get; set; } = true;
    }
}