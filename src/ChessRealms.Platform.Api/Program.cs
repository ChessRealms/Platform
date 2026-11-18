using ChessRealms.Platform.Api;
using ChessRealms.Platform.Infrastructure;
using ChessRealms.Platform.Infrastructure.Identity;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddInfrastructure();
builder.Services.AddIdentityAuthentication(builder.Configuration);
builder.Services.AddConfirmationEmailWorker();
builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

WebApplication app = builder.Build();
app.UseExceptionHandler();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapDefaultEndpoints();
app.Run();
