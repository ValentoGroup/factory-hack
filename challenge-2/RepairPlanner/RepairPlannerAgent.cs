using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Azure.AI.Projects;
using Azure.AI.Projects.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using RepairPlanner.Models;
using RepairPlanner.Services;

namespace RepairPlanner
{
    public sealed class RepairPlannerAgent
    {
        private readonly AIProjectClient projectClient;
        private readonly CosmosDbService cosmosDb;
        private readonly IFaultMappingService faultMapping;
        private readonly string modelDeploymentName;
        private readonly ILogger<RepairPlannerAgent> logger;

        public RepairPlannerAgent(AIProjectClient projectClient, CosmosDbService cosmosDb, IFaultMappingService faultMapping, string modelDeploymentName, ILogger<RepairPlannerAgent> logger)
        {
            this.projectClient = projectClient ?? throw new ArgumentNullException(nameof(projectClient));
            this.cosmosDb = cosmosDb ?? throw new ArgumentNullException(nameof(cosmosDb));
            this.faultMapping = faultMapping ?? throw new ArgumentNullException(nameof(faultMapping));
            this.modelDeploymentName = modelDeploymentName ?? throw new ArgumentNullException(nameof(modelDeploymentName));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }
        private const string AgentName = "RepairPlannerAgent";
        private const string AgentInstructions = """
You are a Repair Planner Agent for tire manufacturing equipment.
Generate a repair plan with tasks, timeline, and resource allocation.
Return the response as valid JSON matching the WorkOrder schema.

Output JSON with these fields:
- workOrderNumber, machineId, title, description
- type: "corrective" | "preventive" | "emergency"
- priority: "critical" | "high" | "medium" | "low"
- status, assignedTo (technician id or null), notes
- estimatedDuration: integer (minutes, e.g. 60 not "60 minutes")
- partsUsed: [{ partId, partNumber, quantity }]
- tasks: [{ sequence, title, description, estimatedDurationMinutes (integer), requiredSkills, safetyNotes }]

IMPORTANT: All duration fields must be integers representing minutes (e.g. 90), not strings.

Rules:
- Assign the most qualified available technician
- Include only relevant parts; empty array if none needed
- Tasks must be ordered and actionable
""";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };

        public async Task EnsureAgentVersionAsync(CancellationToken ct = default)
        {
            logger.LogInformation("Creating agent '{Agent}' with model '{Model}'", AgentName, modelDeploymentName);
            var definition = new PromptAgentDefinition(model: modelDeploymentName)
            {
                Instructions = AgentInstructions
            };

            var versionResponse = await projectClient.Agents.CreateAgentVersionAsync(AgentName, new AgentVersionCreationOptions(definition), ct).ConfigureAwait(false);
            logger.LogInformation("Agent version: {Version}", versionResponse.Value);
        }

        public async Task<WorkOrder> PlanAndCreateWorkOrderAsync(DiagnosedFault fault, CancellationToken ct = default)
        {
            if (fault == null) throw new ArgumentNullException(nameof(fault));

            // 1. Determine required skills and parts
            var requiredSkills = faultMapping.GetRequiredSkills(fault.FaultType ?? string.Empty).ToList();
            var requiredParts = faultMapping.GetRequiredParts(fault.FaultType ?? string.Empty).ToList();
            logger.LogInformation("Planning repair for {Machine}, fault={Fault}", fault.MachineId, fault.FaultType);

            // 2. Query technicians and parts from Cosmos DB
            var technicians = await cosmosDb.GetTechniciansBySkillsAsync(requiredSkills, ct).ConfigureAwait(false);
            var parts = await cosmosDb.GetPartsByPartNumbersAsync(requiredParts, ct).ConfigureAwait(false);

            // 3. Build prompt
            var prompt = BuildPrompt(fault, requiredSkills, requiredParts, technicians, parts);

            try
            {
                // 4. Ensure agent version exists and invoke
                await EnsureAgentVersionAsync(ct).ConfigureAwait(false);
                var agent = projectClient.GetAIAgent(AgentName);
                logger.LogInformation("Invoking agent '{Agent}'", AgentName);
                var response = await agent.RunAsync(prompt, thread: null, options: null, cancellationToken: ct).ConfigureAwait(false);
                var text = response.Text ?? string.Empty;
                // strip markdown fences or backticks the agent may have added
                var cleaned = CleanAgentOutput(text);

                // 5. Parse response
                WorkOrder? wo = null;
                try
                {
                    wo = JsonSerializer.Deserialize<WorkOrder>(cleaned, JsonOptions);
                }
                catch (Exception parseEx)
                {
                    logger.LogError(parseEx, "Failed to parse agent response as WorkOrder JSON. Original response: {Response}\nCleaned: {Cleaned}", text, cleaned);
                }

                if (wo == null)
                {
                    // Fallback: create a draft work order with basic information
                    wo = new WorkOrder
                    {
                        WorkOrderNumber = $"WO-{Guid.NewGuid():N}" ,
                        MachineId = fault.MachineId,
                        Title = $"Repair required: {fault.FaultType}",
                        Description = fault.Details ?? string.Empty,
                        Type = "corrective",
                        Priority = "medium",
                        Status = "draft",
                        EstimatedDuration = 60,
                        Notes = "Agent response could not be parsed; created draft work order.",
                    };
                }

                // 6. Post-process: ensure tasks ordered and durations integers
                if (wo.Tasks != null && wo.Tasks.Any())
                {
                    wo.Tasks = wo.Tasks.OrderBy(t => t.Sequence).ToList();
                    foreach (var t in wo.Tasks)
                    {
                        if (t.EstimatedDurationMinutes <= 0) t.EstimatedDurationMinutes = 30;
                        t.Sequence = Math.Max(1, t.Sequence);
                    }
                }

                // 7. Assign technician if not assigned
                if (string.IsNullOrWhiteSpace(wo.AssignedTo) && technicians.Any())
                {
                    wo.AssignedTo = technicians.First().Id;
                }

                // 8. Ensure partsUsed: if empty, populate from requiredParts
                if ((wo.PartsUsed == null || !wo.PartsUsed.Any()) && requiredParts.Any())
                {
                    wo.PartsUsed = new List<WorkOrderPartUsage>();
                    foreach (var pn in requiredParts)
                    {
                        var found = parts.FirstOrDefault(p => string.Equals(p.PartNumber, pn, StringComparison.OrdinalIgnoreCase));
                        wo.PartsUsed.Add(new WorkOrderPartUsage
                        {
                            PartId = found?.Id ?? string.Empty,
                            PartNumber = pn,
                            Quantity = 1
                        });
                    }
                }
                else if (wo.PartsUsed != null && wo.PartsUsed.Any() && parts.Any())
                {
                    // fill missing PartId fields where possible
                    foreach (var pu in wo.PartsUsed)
                    {
                        if (string.IsNullOrWhiteSpace(pu.PartId))
                        {
                            var found = parts.FirstOrDefault(p => string.Equals(p.PartNumber, pu.PartNumber, StringComparison.OrdinalIgnoreCase));
                            if (found != null) pu.PartId = found.Id;
                        }
                    }
                }

                // 9. Ensure basic fields
                if (string.IsNullOrWhiteSpace(wo.WorkOrderNumber)) wo.WorkOrderNumber = $"WO-{Guid.NewGuid():N}";
                if (wo.EstimatedDuration <= 0 && wo.Tasks != null) wo.EstimatedDuration = wo.Tasks.Sum(t => t.EstimatedDurationMinutes);
                if (wo.CreatedAt == default) wo.CreatedAt = DateTimeOffset.UtcNow;
                wo.UpdatedAt = DateTimeOffset.UtcNow;

                // 9b. Calculate priority from fault severity if agent didn't provide a stronger value
                var computedPriority = CalculatePriority(fault);
                if (string.IsNullOrWhiteSpace(wo.Priority) || string.Equals(wo.Priority, "medium", StringComparison.OrdinalIgnoreCase))
                {
                    wo.Priority = computedPriority;
                }

                // 10. Save to Cosmos DB
                var saved = await cosmosDb.CreateWorkOrderAsync(wo, ct).ConfigureAwait(false);
                if (!saved)
                {
                    logger.LogWarning("Work order {WO} was not saved to Cosmos DB.", wo.WorkOrderNumber);
                }

                return wo;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error while planning work order for fault {FaultId}", fault.Id);
                throw;
            }
        }

        private static string BuildPrompt(DiagnosedFault fault, IReadOnlyList<string> skills, IReadOnlyList<string> parts, IReadOnlyList<Technician> technicians, IReadOnlyList<Part> inventory)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(AgentInstructions);
            sb.AppendLine();
            sb.AppendLine("Context:");
            sb.AppendLine($"Fault: {fault.FaultType}");
            sb.AppendLine($"MachineId: {fault.MachineId}");
            if (!string.IsNullOrWhiteSpace(fault.Details)) sb.AppendLine($"Details: {fault.Details}");
            sb.AppendLine($"Required skills: {string.Join(", ", skills)}");
            sb.AppendLine($"Candidate parts (from mapping): {string.Join(", ", parts)}");

            sb.AppendLine("\nAvailable technicians (id, name, skills, isAvailable):");
            foreach (var t in technicians)
            {
                sb.AppendLine($"- {t.Id}, {t.Name}, skills=[{string.Join(';', t.Skills ?? new List<string>())}], available={t.IsAvailable}");
            }

            sb.AppendLine("\nInventory (partNumber, qty):");
            foreach (var p in inventory)
            {
                sb.AppendLine($"- {p.PartNumber}, qty={p.QuantityAvailable}");
            }

            sb.AppendLine("\nReturn a single JSON object matching the WorkOrder schema described above.");
            return sb.ToString();
        }

        // compute priority based on telemetry or confidence if agent didn't specify
        private static string CalculatePriority(DiagnosedFault fault)
        {
            if (fault == null) return "medium";

            // If telemetry includes temperature and setpoint, use delta to escalate priority
            try
            {
                if (fault.Telemetry != null)
                {
                    if (fault.Telemetry.TryGetValue("temperature", out var tObj) && fault.Telemetry.TryGetValue("setpoint", out var sObj))
                    {
                        if (TryParseDouble(tObj, out var temp) && TryParseDouble(sObj, out var setpoint))
                        {
                            var delta = temp - setpoint;
                            if (delta >= 15) return "critical";
                            if (delta >= 10) return "high";
                            if (delta >= 5) return "medium";
                        }
                    }
                }
            }
            catch
            {
                // ignore parsing issues
            }

            // Fallback to confidence
            var conf = fault.Confidence ?? 0.0;
            if (conf >= 0.95) return "critical";
            if (conf >= 0.85) return "high";
            if (conf >= 0.6) return "medium";
            return "low";
        }

        private static bool TryParseDouble(object? obj, out double value)
        {
            value = 0.0;
            if (obj == null) return false;
            switch (obj)
            {
                case double d:
                    value = d; return true;
                case float f:
                    value = f; return true;
                case int i:
                    value = i; return true;
                case long l:
                    value = l; return true;
                case string s when double.TryParse(s, out var dv):
                    value = dv; return true;
                default:
                    try { value = Convert.ToDouble(obj); return true; } catch { return false; }
            }
        }

        /// <summary>
        /// Remove leading/trailing markdown fences or backticks from LLM output.
        /// </summary>
        private static string CleanAgentOutput(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            var trimmed = text.Trim();
            // remove triple backticks and optional language specifier
            if (trimmed.StartsWith("```"))
            {
                var end = trimmed.LastIndexOf("```", StringComparison.Ordinal);
                if (end > 2)
                {
                    trimmed = trimmed.Substring(trimmed.IndexOf('\n') + 1, end - trimmed.IndexOf('\n') - 1);
                }
            }
            // also trim single backticks
            trimmed = trimmed.Trim('`', '\n', '\r', ' ');
            return trimmed;
        }
    }
}
