var builder = DistributedApplication.CreateBuilder(args);

// RabbitMQ and PostgreSQL resources are added in phase 3 and referenced by both services.
builder.AddProject<Projects.ElevatorSystem_Engine>("engine")
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.ElevatorSystem_Api>("api")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

await builder.Build().RunAsync();
