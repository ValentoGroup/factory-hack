using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace RepairPlanner.Models
{
    // Brief C# idiom notes for Python developers:
    // - "??" is null-coalescing: returns left if not null, otherwise right.
    // - "??=" is null-coalescing assignment: assigns right only if left is null.
    // - Primary constructors: in C# 12 you can declare parameters on the class header
    // - "await using" disposes asynchronously, similar to Python's "async with".

    public sealed class DiagnosedFault
    {
        [JsonPropertyName("id")]
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("faultType")]
        [JsonProperty("faultType")]
        public string FaultType { get; set; } = string.Empty;

        [JsonPropertyName("machineId")]
        [JsonProperty("machineId")]
        public string MachineId { get; set; } = string.Empty;

        [JsonPropertyName("timestamp")]
        [JsonProperty("timestamp")]
        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

        [JsonPropertyName("confidence")]
        [JsonProperty("confidence")]
        public double? Confidence { get; set; }

        [JsonPropertyName("details")]
        [JsonProperty("details")]
        public string? Details { get; set; }

        [JsonPropertyName("telemetry")]
        [JsonProperty("telemetry")]
        public Dictionary<string, object>? Telemetry { get; set; }
    }
}
