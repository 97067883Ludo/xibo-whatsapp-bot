using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Schermpostvak.Api;
using Schermpostvak.Api.Data;
using Schermpostvak.Api.Endpoints;
using Schermpostvak.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.Configure<WebhookOptions>(builder.Configuration.GetSection("Webhook"));
builder.Services.Configure<EvolutionOptions>(builder.Configuration.GetSection("Evolution"));
builder.Services.Configure<XiboOptions>(builder.Configuration.GetSection("Xibo"));

var connectionString = builder.Configuration.GetConnectionString("Default")!;
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(connectionString));

builder.Services.AddHttpClient<EvolutionClient>((sp, http) =>
{
    var o = sp.GetRequiredService<IOptions<EvolutionOptions>>().Value;
    http.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/");
    http.DefaultRequestHeaders.Add("apikey", o.ApiKey);
    http.Timeout = TimeSpan.FromMinutes(2);
});

builder.Services.AddSingleton<XiboTokenStore>();
builder.Services.AddHttpClient<XiboClient>((sp, http) =>
{
    var o = sp.GetRequiredService<IOptions<XiboOptions>>().Value;
    if (!string.IsNullOrEmpty(o.BaseUrl)) http.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/api/");
    http.Timeout = TimeSpan.FromMinutes(5);
});

builder.Services.AddScoped<MediaProcessor>();
builder.Services.AddHostedService<Worker>();

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

// Create the database and media folder on first start
var dbPath = new SqliteConnectionStringBuilder(connectionString).DataSource;
var dbDir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
if (!string.IsNullOrEmpty(dbDir)) Directory.CreateDirectory(dbDir);
Directory.CreateDirectory(app.Services.GetRequiredService<IOptions<StorageOptions>>().Value.Root);
using (var scope = app.Services.CreateScope())
{
    // EnsureCreated keeps the skeleton simple; switch to EF migrations before the schema starts changing.
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapWebhookEndpoints();
app.MapApiEndpoints();
app.MapFallbackToFile("index.html");

app.Run();
