using ChessRealms.Platform.Infrastructure;
using ChessRealms.Platform.Infrastructure.Identity;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddInfrastructure();
builder.Services.AddConfirmationEmailWorker();
builder.Services.AddControllers();
builder.Services.AddOpenApi();

WebApplication app = builder.Build();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();
app.MapDefaultEndpoints();
app.Run();
