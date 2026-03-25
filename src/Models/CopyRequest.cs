using Newtonsoft.Json;

namespace TestProject.Models
{
    public class CopyRequest
    {
        [JsonProperty("source_path")]
        public string SourcePath { get; set; } = string.Empty;

        [JsonProperty("destination_path")]
        public string DestinationPath { get; set; } = string.Empty;
    }
}
