var builder = DistributedApplication.CreateBuilder(args);

// Credentials are Aspire parameters: the user names below are not secrets, and the passwords are
// generated on first run and kept in this project's user secrets, never in source (ADR-0021).
var messagingUser = builder.AddParameter("messaging-username", "elevator");
var databaseUser = builder.AddParameter("database-username", "elevator");

// Same broker major version as the messaging tests (rabbitmq:4.1), with the management UI.
var messaging = builder.AddRabbitMQ("messaging", messagingUser)
    .WithManagementPlugin()
    .WithImageTag("4.1-management")
    .WithDataVolume();

// Same server version as the persistence tests; the volume keeps the elevator's state across runs.
var database = builder.AddPostgres("postgres", databaseUser)
    .WithImageTag("17.6-alpine")
    .WithDataVolume()
    .AddDatabase("elevatordb");

// Two engines: one leads and drives the car, the other stands by to take over (ADR-0019).
builder.AddProject<Projects.ElevatorSystem_Engine>("engine")
    .WithReplicas(2)
    .WithReference(messaging)
    .WithReference(database)
    .WaitFor(messaging)
    .WaitFor(database)
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.ElevatorSystem_Api>("api")
    .WithEnvironment("Hosting__Mode", "Distributed")
    .WithReference(messaging)
    .WaitFor(messaging)
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

await builder.Build().RunAsync();
