var builder = DistributedApplication.CreateBuilder(args);

// Credentials are Aspire parameters: the user names below are not secrets, and the passwords are
// generated on first run and kept in this project's user secrets, never in source (ADR-0021).
var messagingUser = builder.AddParameter("messaging-username", "elevator");
var databaseUser = builder.AddParameter("database-username", "elevator");

// The first admin (ADR-0022). Its password is generated like the others; read it from the dashboard's
// parameters, sign in, and register an authenticator app before any admin capability is available.
var adminEmail = builder.AddParameter("admin-email", "admin@elevator.local");
var adminPassword = builder.AddParameter(
    "admin-password", new GenerateParameterDefault { MinLength = 24, Special = false }, secret: true, persist: true);

// HMAC key that pseudonymises client addresses in the audit trail: 44 base64 characters, 33 bytes.
var auditPseudonymKey = builder.AddParameter(
    "audit-pseudonym-key", new GenerateParameterDefault { MinLength = 44, Special = false }, secret: true, persist: true);

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

// Staff accounts, the audit trail and the Data Protection keys live in the staff schema of the same database.
builder.AddProject<Projects.ElevatorSystem_Api>("api")
    .WithEnvironment("Hosting__Mode", "Distributed")
    .WithEnvironment("Staff__Accounts__0__Email", adminEmail)
    .WithEnvironment("Staff__Accounts__0__Password", adminPassword)
    .WithEnvironment("Staff__Accounts__0__Role", "Admin")
    .WithEnvironment("Audit__ClientPseudonymKey", auditPseudonymKey)
    .WithReference(messaging)
    .WithReference(database)
    .WaitFor(messaging)
    .WaitFor(database)
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

await builder.Build().RunAsync();
