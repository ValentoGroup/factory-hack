using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using RepairPlanner.Models;

namespace RepairPlanner.Services
{
    public sealed class CosmosDbService : IAsyncDisposable
    {
        private readonly CosmosClient _client;
        private readonly CosmosDbOptions _options;
        private readonly ILogger<CosmosDbService> _logger;

        public CosmosDbService(CosmosDbOptions options, ILogger<CosmosDbService> logger)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            var clientOptions = new CosmosClientOptions();
            _client = new CosmosClient(_options.Endpoint, _options.Key, clientOptions);
        }

        // Query available technicians who have any of the provided skills.
        public async Task<IReadOnlyList<Technician>> GetTechniciansBySkillsAsync(IEnumerable<string> skills, CancellationToken ct = default)
        {
            var skillList = (skills ?? Array.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
            if (!skillList.Any()) return Array.Empty<Technician>();

            try
            {
                var container = _client.GetContainer(_options.DatabaseName, _options.TechniciansContainer);
                var query = "SELECT * FROM c WHERE c.isAvailable = true"; // fetch available techs and filter in-memory
                var queryDef = new QueryDefinition(query);
                var iterator = container.GetItemQueryIterator<Technician>(queryDef);
                _logger.LogInformation("CosmosDbService querying available technicians");

                var results = new List<Technician>();
                while (iterator.HasMoreResults)
                {
                    var feed = await iterator.ReadNextAsync(ct).ConfigureAwait(false);
                    results.AddRange(feed.Resource);
                }

                // Filter and rank technicians by number of matching skills (descending)
                var matched = results
                    .Select(t => new { Tech = t, MatchCount = t.Skills?.Count(s => skillList.Contains(s, StringComparer.OrdinalIgnoreCase)) ?? 0 })
                    .Where(x => x.MatchCount > 0)
                    .OrderByDescending(x => x.MatchCount)
                    .ThenBy(x => x.Tech.Name)
                    .Select(x => x.Tech)
                    .ToList();

                _logger.LogInformation("Found {Count} available technicians matching skills", matched.Count);
                return matched;
            }
            catch (CosmosException cx)
            {
                _logger.LogError(cx, "Cosmos DB error querying technicians");
                return Array.Empty<Technician>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error querying technicians");
                return Array.Empty<Technician>();
            }
        }

        // Fetch parts by part numbers (partNumber field)
        public async Task<IReadOnlyList<Part>> GetPartsByPartNumbersAsync(IEnumerable<string> partNumbers, CancellationToken ct = default)
        {
            var partsList = (partNumbers ?? Array.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();
            if (!partsList.Any()) return Array.Empty<Part>();

            try
            {
                var container = _client.GetContainer(_options.DatabaseName, _options.PartsContainer);

                // Build parameterized IN clause
                var parameters = new List<string>();
                var queryText = "SELECT * FROM c WHERE ";
                for (int i = 0; i < partsList.Count; i++)
                {
                    var name = $"@p{i}";
                    parameters.Add(name);
                    queryText += (i == 0) ? $"c.partNumber = {name}" : $" OR c.partNumber = {name}";
                }

                var queryDef = new QueryDefinition(queryText);
                for (int i = 0; i < partsList.Count; i++) queryDef.WithParameter(parameters[i], partsList[i]);

                var iterator = container.GetItemQueryIterator<Part>(queryDef);
                var results = new List<Part>();
                _logger.LogInformation("CosmosDbService fetching parts");
                while (iterator.HasMoreResults)
                {
                    var feed = await iterator.ReadNextAsync(ct).ConfigureAwait(false);
                    results.AddRange(feed.Resource);
                }

                _logger.LogInformation("Fetched {Count} parts", results.Count);
                return results;
            }
            catch (CosmosException cx)
            {
                _logger.LogError(cx, "Cosmos DB error fetching parts");
                return Array.Empty<Part>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error fetching parts");
                return Array.Empty<Part>();
            }
        }

        // Create a work order document. Partition key is `status` per spec.
        public async Task<bool> CreateWorkOrderAsync(WorkOrder workOrder, CancellationToken ct = default)
        {
            if (workOrder == null) throw new ArgumentNullException(nameof(workOrder));

            try
            {
                var container = _client.GetContainer(_options.DatabaseName, _options.WorkOrdersContainer);

                if (string.IsNullOrWhiteSpace(workOrder.Id)) workOrder.Id = Guid.NewGuid().ToString();
                workOrder.CreatedAt = workOrder.CreatedAt == default ? DateTimeOffset.UtcNow : workOrder.CreatedAt;
                workOrder.UpdatedAt = DateTimeOffset.UtcNow;

                var pk = new PartitionKey(workOrder.Status ?? "draft");
                await container.CreateItemAsync(workOrder, pk, cancellationToken: ct).ConfigureAwait(false);
                _logger.LogInformation("Saved work order {WorkOrderNumber} (id={Id}, status={Status}, assignedTo={Assigned})",
                    workOrder.WorkOrderNumber, workOrder.Id, workOrder.Status, workOrder.AssignedTo);
                return true;
            }
            catch (CosmosException cx)
            {
                _logger.LogError(cx, "Cosmos DB error creating work order");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error creating work order");
                return false;
            }
        }

        public ValueTask DisposeAsync()
        {
            _logger.LogDebug("Disposing CosmosClient");
            // CosmosClient v3.x does not expose DisposeAsync; call synchronous Dispose.
            _client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
