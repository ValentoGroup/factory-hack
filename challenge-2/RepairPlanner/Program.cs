using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using RepairPlanner.Models;
using RepairPlanner.Services;
using RepairPlanner;

var loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole(options => {
	options.SingleLine = true;
	options.TimestampFormat = "hh:mm:ss ";
}));

var logger = loggerFactory.CreateLogger("RepairPlanner.Main");

try
{
	// Read configuration from environment variables
	var aiEndpoint = Environment.GetEnvironmentVariable("AZURE_AI_PROJECT_ENDPOINT") ?? "https://example.azureai.net";
	var modelDeployment = Environment.GetEnvironmentVariable("MODEL_DEPLOYMENT_NAME") ?? "gpt-4o";

	var cosmosOptions = new CosmosDbOptions
	{
		Endpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT") ?? string.Empty,
		Key = Environment.GetEnvironmentVariable("COSMOS_KEY") ?? string.Empty,
		DatabaseName = Environment.GetEnvironmentVariable("COSMOS_DATABASE_NAME") ?? "FactoryMaintenance"
	};

	// Create clients and services
	var projectClient = new AIProjectClient(new Uri(aiEndpoint), new DefaultAzureCredential());
	await using var cosmosService = new CosmosDbService(cosmosOptions, loggerFactory.CreateLogger<CosmosDbService>());
	var faultMapping = new FaultMappingService();

	var agent = new RepairPlannerAgent(projectClient, cosmosService, faultMapping, modelDeployment, loggerFactory.CreateLogger<RepairPlannerAgent>());

	// Sample diagnosed fault
	var sampleFault = new DiagnosedFault
	{
		Id = Guid.NewGuid().ToString(),
		FaultType = "curing_temperature_excessive",
		MachineId = "CURE-001",
		Timestamp = DateTimeOffset.UtcNow,
		Confidence = 0.92,
		Details = "Curing press temperature exceeded setpoint by 15C during last cycle",
		Telemetry = new Dictionary<string, object>
		{
			["temperature"] = 230,
			["setpoint"] = 215,
			["cycleId"] = "cycle-42"
		}
	};

	logger.LogInformation("Starting repair planning for fault {FaultId} ({FaultType})", sampleFault.Id, sampleFault.FaultType);

	var workOrder = await agent.PlanAndCreateWorkOrderAsync(sampleFault);

	var json = JsonSerializer.Serialize(workOrder, new JsonSerializerOptions { WriteIndented = true });
	Console.WriteLine("Generated WorkOrder:");
	Console.WriteLine(json);

}
catch (Exception ex)
{
	logger.LogError(ex, "Unhandled error in RepairPlanner program");
}

