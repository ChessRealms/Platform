using System.Threading.RateLimiting;

using ChessRealms.Platform.Api;
using ChessRealms.Platform.Api.Controllers;
using ChessRealms.Platform.Infrastructure;
using ChessRealms.Platform.Infrastructure.Identity;
using ChessRealms.Platform.Infrastructure.Identity.Sessions;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddInfrastructure();
builder.Services.AddIdentityAuthentication(builder.Configuration);
builder.Services.AddConfirmationEmailWorker();
builder.Services.AddHostedService<LoginSessionCleanupWorker>();
builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(PasswordResetController.RateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1)
            }));
});

WebApplication app = builder.Build();
app.UseExceptionHandler();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapDefaultEndpoints();
app.Run();
