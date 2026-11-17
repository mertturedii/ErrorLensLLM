using ErrorLensLLM.Dashboard;
using ErrorLensLLM.Dashboard.Components;
using ErrorLensLLM.Storage.PostgreSql;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Serve component styles and blazor.web.js from the build output in every environment; demo.sh runs it outside Development
builder.WebHost.UseStaticWebAssets();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

builder.Services.Configure<DashboardOptions>(builder.Configuration.GetSection(DashboardOptions.Section));
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(
    builder.Configuration.GetConnectionString("ErrorLens") ?? throw new InvalidOperationException("ConnectionStrings:ErrorLens is missing.")));
builder.Services.AddSingleton<PostgresLogStore>();
builder.Services.AddSingleton<PostgresIncidentStore>();
builder.Services.AddSingleton<PostgresAnomalyStore>();
builder.Services.AddSingleton<PostgresExplanationStore>();
builder.Services.AddSingleton<PostgresDashboardStore>();
builder.Services.AddSingleton<DashboardFormat>();
builder.Services.AddSingleton<ModuleColors>();
builder.Services.AddScoped<DashboardSession>();

var app = builder.Build();

// The dashboard can start before the collector has ever run
await app.Services.GetRequiredService<PostgresLogStore>().EnsureSchemaAsync();
await app.Services.GetRequiredService<PostgresIncidentStore>().EnsureSchemaAsync();
await app.Services.GetRequiredService<PostgresAnomalyStore>().EnsureSchemaAsync();
await app.Services.GetRequiredService<PostgresExplanationStore>().EnsureSchemaAsync();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);

app.UseStaticFiles();
app.UseAntiforgery();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
