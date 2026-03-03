using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace RepairPlanner.Models
{
    public sealed class Technician
    {
        [JsonPropertyName("id")]
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("department")]
        [JsonProperty("department")]
        public string Department { get; set; } = string.Empty;

        [JsonPropertyName("skills")]
        [JsonProperty("skills")]
        public List<string> Skills { get; set; } = new List<string>();

        [JsonPropertyName("isAvailable")]
        [JsonProperty("isAvailable")]
        public bool IsAvailable { get; set; } = true;

        [JsonPropertyName("nextAvailableAt")]
        [JsonProperty("nextAvailableAt")]
        public DateTimeOffset? NextAvailableAt { get; set; }

        [JsonPropertyName("notes")]
        [JsonProperty("notes")]
        public string? Notes { get; set; }
    }
}
