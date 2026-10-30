IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

IResourceBuilder<PostgresDatabaseResource> database = builder.AddPostgres("postgres")
    .WithImageTag("18.3")
    .WithDataVolume()
    .AddDatabase("platform");

IResourceBuilder<ProjectResource> migrations = builder.AddProject<Projects.ChessRealms_Platform_MigrationWorker>("migrations")
    .WithReference(database)
    .WaitFor(database);

builder.AddProject<Projects.ChessRealms_Platform_Api>("api")
    .WithReference(database)
    .WaitForCompletion(migrations)
    .WithHttpHealthCheck("/health");

builder.AddJavaScriptApp("web", "../../src/cr-platform-web-app", "start")
    .WithHttpEndpoint(targetPort: 4200)
    .WithExternalHttpEndpoints();

builder.Build().Run();
