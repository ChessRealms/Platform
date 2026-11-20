using Microsoft.Extensions.Hosting;

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

IResourceBuilder<PostgresDatabaseResource> database = builder.AddPostgres("postgres")
    .WithImageTag("18.3")
    .WithDataVolume()
    .AddDatabase("platform");

IResourceBuilder<ProjectResource> migrations = builder.AddProject<Projects.ChessRealms_Platform_MigrationWorker>("migrations")
    .WithReference(database)
    .WaitFor(database);

IResourceBuilder<ProjectResource> api = builder.AddProject<Projects.ChessRealms_Platform_Api>("api")
    .WithReference(database)
    .WaitForCompletion(migrations)
    .WithHttpHealthCheck("/health");

if (builder.Environment.IsDevelopment())
{
    api.WithEnvironment("Identity__EmailConfirmation__AutoConfirmOnRegistration",
        builder.Configuration["Identity:EmailConfirmation:AutoConfirmOnRegistration"] ?? "true");
}

builder.AddJavaScriptApp("web", "../../src/cr-platform-web-app", "start")
    .WithReference(api)
    .WaitFor(api)
    .WithHttpEndpoint(targetPort: 4200)
    .WithExternalHttpEndpoints();

builder.Build().Run();
