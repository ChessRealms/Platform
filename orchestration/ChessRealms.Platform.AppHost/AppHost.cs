using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

bool mailpitEnabled = builder.Configuration.GetValue<bool>("Mailpit:Enabled");
bool development = builder.Environment.IsDevelopment();
string? e2eRunId = builder.Configuration["IdentityE2E:RunId"];
if (mailpitEnabled && !development)
{
    throw new InvalidOperationException("Mailpit:Enabled is allowed only in Development.");
}

if (e2eRunId is not null && (!development || !mailpitEnabled || !Guid.TryParseExact(e2eRunId, "N", out _)))
{
    throw new InvalidOperationException("Identity E2E requires Development, Mailpit, and a valid run ID.");
}

var postgres = builder.AddPostgres("postgres").WithImageTag("18.3");
string? e2eLabel = e2eRunId is null ? null : $"org.chessrealms.identity-e2e={e2eRunId}";
if (e2eLabel is null)
{
    postgres.WithDataVolume();
}
else
{
    // Mount tmpfs at the image's default volume path and connect through Docker's published port.
    postgres.WithContainerRuntimeArgs("--label", e2eLabel, "--tmpfs", "/var/lib/postgresql")
        .WithEndpoint("tcp", endpoint => endpoint.IsProxied = false);
}

IResourceBuilder<PostgresDatabaseResource> database = postgres.AddDatabase("platform");

IResourceBuilder<ProjectResource> migrations = builder.AddProject<Projects.ChessRealms_Platform_MigrationWorker>("migrations")
    .WithReference(database)
    .WaitFor(database);

IResourceBuilder<ProjectResource> api = (development
        ? builder.AddProject<Projects.ChessRealms_Platform_Api>("api", launchProfileName: "https")
        : builder.AddProject<Projects.ChessRealms_Platform_Api>("api"))
    .WithReference(database)
    .WaitForCompletion(migrations)
    .WithHttpHealthCheck("/health");

var web = builder.AddJavaScriptApp("web", "../../src/cr-platform-web-app", "start")
    .WithReference(api)
    .WaitFor(api);

if (development)
{
    // Aspire manages the certificate files for the local Angular HTTPS endpoint.
#pragma warning disable ASPIRECERTIFICATES001
    web.WithHttpsEndpoint(env: "PORT")
        .WithHttpsDeveloperCertificate()
        .WithHttpsCertificateConfiguration(context =>
        {
            context.Arguments.Add("--");
            context.Arguments.Add("--ssl");
            context.Arguments.Add("--ssl-cert");
            context.Arguments.Add(context.CertificatePath);
            context.Arguments.Add("--ssl-key");
            context.Arguments.Add(context.KeyPath);
            return Task.CompletedTask;
        });
#pragma warning restore ASPIRECERTIFICATES001

    if (e2eLabel is not null)
    {
        web.WithNpm(install: false).WithHttpHealthCheck("/login", endpointName: "https");
    }

    if (mailpitEnabled)
    {
        if (builder.Configuration.GetValue<bool?>("Identity:EmailConfirmation:AutoConfirmOnRegistration") is true)
        {
            throw new InvalidOperationException("Mailpit requires Identity:EmailConfirmation:AutoConfirmOnRegistration=false.");
        }

        IResourceBuilder<ContainerResource> mailpit = builder.AddContainer("mailpit", "axllent/mailpit", "v1.34.0")
            .WithHttpEndpoint(targetPort: 8025, name: "http", isProxied: false)
            .WithEndpoint(targetPort: 1025, scheme: "tcp", name: "smtp", isProxied: false)
            .WithHttpHealthCheck("/api/v1/messages")
            .WithEnvironment("MP_MAX_MESSAGES", "200")
            .WithEnvironment("MP_MAX_AGE", "24h")
            .WithEnvironment("MP_MAX_MESSAGE_SIZE", "1")
            .WithEnvironment("MP_DISABLE_VERSION_CHECK", "true")
            .WithEnvironment("MP_BLOCK_REMOTE_CSS_AND_FONTS", "true");
        if (e2eLabel is not null)
        {
            mailpit.WithContainerRuntimeArgs("--label", e2eLabel);
        }

        EndpointReference smtp = mailpit.GetEndpoint("smtp");
        api.WithReference(smtp)
            .WaitFor(mailpit)
            .WithEnvironment("Identity__EmailConfirmation__AutoConfirmOnRegistration", "false")
            .WithEnvironment("Identity__EmailConfirmation__Smtp__Host", smtp.Property(EndpointProperty.Host))
            .WithEnvironment("Identity__EmailConfirmation__Smtp__Port", smtp.Property(EndpointProperty.Port))
            .WithEnvironment("Identity__EmailConfirmation__Smtp__FromAddress", "no-reply@chessrealms.test")
            .WithEnvironment("Identity__EmailConfirmation__Smtp__Security", "None")
            .WithEnvironment("Identity__EmailConfirmation__ConfirmationUrl",
                $"{web.GetEndpoint("https").Property(EndpointProperty.Url)}/confirm-email")
            .WithEnvironment("Identity__PasswordReset__ResetUrl",
                $"{web.GetEndpoint("https").Property(EndpointProperty.Url)}/reset-password");
    }
    else
    {
        api.WithEnvironment("Identity__EmailConfirmation__AutoConfirmOnRegistration",
            builder.Configuration["Identity:EmailConfirmation:AutoConfirmOnRegistration"] ?? "true");
    }
}

if (!development)
{
    web.WithHttpEndpoint(targetPort: 4200);
}
web.WithExternalHttpEndpoints();

builder.Build().Run();
