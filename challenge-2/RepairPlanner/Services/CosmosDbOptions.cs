namespace RepairPlanner.Services
{
    public sealed class CosmosDbOptions
    {
        public string Endpoint { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = "FactoryMaintenance";

        // Container names and their partition keys per spec
        public string TechniciansContainer { get; set; } = "Technicians"; // partition key: department
        public string PartsContainer { get; set; } = "PartsInventory"; // partition key: category
        public string WorkOrdersContainer { get; set; } = "WorkOrders"; // partition key: status
    }
}
