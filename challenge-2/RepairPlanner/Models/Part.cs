using System;
using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace RepairPlanner.Models
{
    public sealed class Part
    {
        [JsonPropertyName("id")]
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("partNumber")]
        [JsonProperty("partNumber")]
        public string PartNumber { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        [JsonProperty("description")]
        public string? Description { get; set; }

        [JsonPropertyName("category")]
        [JsonProperty("category")]
        public string Category { get; set; } = string.Empty;

        [JsonPropertyName("quantityAvailable")]
        [JsonProperty("quantityAvailable")]
        public int QuantityAvailable { get; set; }

        [JsonPropertyName("unitOfMeasure")]
        [JsonProperty("unitOfMeasure")]
        public string? UnitOfMeasure { get; set; }
    }
}
