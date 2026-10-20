var builder = DistributedApplication.CreateBuilder(args);

// Credentials are Aspire parameters: the user names below are not secrets, and the passwords are
// generated on first run and kept in this project's user secrets, never in source (ADR-0021).
var messagingUser = builder.AddParameter("messaging-username", "elevator");
var databaseUser = builder.AddParameter("database-username", "elevator");

// The first admin (ADR-0022). Its password is a secret you choose: the dashboard asks for it on the first run and
// can save it to user secrets, or set it with `dotnet user-secrets set Parameters:admin-password <value>`. The account
// is created once, so the password must stay the same across runs; after signing in, register an authenticator app.
var adminEmail = builder.AddParameter("admin-email", "admin@elevator.local");
var adminPassword = builder.AddParameter("admin-password", secret: true)
    .WithDescription("Password of the first admin account: at least 12 characters, with at least 4 different ones.");

// A test admin that signs in with its password only, no authenticator app (ADR-0030). The default password is public:
// it is for trying the system out. Set the parameter to an empty value to turn the test account off.
var testAdminPassword = builder.AddParameter("test-admin-password", "elevator-test-account")
    .WithDescription("Password of the test admin tester@elevator.local, who signs in without a second factor; empty turns it off.");

// HMAC key that pseudonymises client addresses in the audit trail: 44 base64 characters, 33 bytes. Generated per run
// unless set in user secrets, so locally pseudonyms link only within one run (ADR-0023).
var auditPseudonymKey = builder.AddParameter(
    "audit-pseudonym-key", new GenerateParameterDefault { MinLength = 44, Special = false }, secret: true);

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
    // Admins can start demo scenarios with simulated passengers (ADR-0027); off unless a deployment turns it on.
    .WithEnvironment("Simulation__Enabled", "true")
    .WithReference(messaging)
    .WithReference(database)
    .WaitFor(messaging)
    .WaitFor(database)
    .WithHttpHealthCheck("/health");

// Staff accounts, the audit trail and the Data Protection keys live in the staff schema of the same database.
builder.AddProject<Projects.ElevatorSystem_Api>("api")
    .WithEnvironment("Hosting__Mode", "Distributed")
    .WithEnvironment("Simulation__Enabled", "true")
    .WithEnvironment("Staff__Accounts__0__Email", adminEmail)
    .WithEnvironment("Staff__Accounts__0__Password", adminPassword)
    .WithEnvironment("Staff__Accounts__0__Role", "Admin")
    .WithEnvironment("Staff__TestAccount__Email", "tester@elevator.local")
    .WithEnvironment("Staff__TestAccount__Password", testAdminPassword)
    .WithEnvironment("Audit__ClientPseudonymKey", auditPseudonymKey)
    .WithReference(messaging)
    .WithReference(database)
    .WaitFor(messaging)
    .WaitFor(database)
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

await builder.Build().RunAsync();
