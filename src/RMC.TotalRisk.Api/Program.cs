using System.Text.Json;
using System.Text.Json.Serialization;
using RMC.TotalRisk.Api.Configuration;
using RMC.TotalRisk.Api.DTOs;

var builder = WebApplication.CreateBuilder(args);

// =============================================================================
// Service Configuration
// =============================================================================

// Configure JSON serialization
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.JsonSerializerOptions.WriteIndented = builder.Environment.IsDevelopment();

        // Seatbelt against mid-stream serialization crashes: NaN is a legitimate marker for a
        // risk measure that was not computed and is emitted as the JSON string literal "NaN".
        // ±Infinity is caught earlier by the ResponseFiniteAuditor; this option is the last line
        // of defense. Clients must enable the matching flag on their deserializer.
        options.JsonSerializerOptions.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
    });

// Configure OpenAPI
builder.Services.AddOpenApi();

// Configure CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader());

    // More restrictive policy for production
    options.AddPolicy("Production", policy =>
        policy.WithOrigins(
                builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                ?? new[] { "https://localhost" })
              .AllowAnyMethod()
              .AllowAnyHeader());
});

// Bind the API limits (run throttle, request-size guards)
builder.Services.Configure<ApiOptions>(builder.Configuration.GetSection(ApiOptions.SectionName));

// Register the services shared by REST controllers and MCP tools. Everything is a singleton:
// the compute surface is stateless, so services are pure facades over the model library.
builder.Services.AddSingleton<RMC.TotalRisk.Api.Services.IRiskAnalysisComputeService,
    RMC.TotalRisk.Api.Services.RiskAnalysisComputeService>();
builder.Services.AddSingleton<RMC.TotalRisk.Api.Services.IMetadataService,
    RMC.TotalRisk.Api.Services.MetadataService>();

// MCP server: same service layer as the REST controllers, exposed as tools over the streamable
// HTTP transport. Stateless mode is correct here because the compute surface holds no state at
// all — every tool call is a self-contained round trip.
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<RMC.TotalRisk.Api.Mcp.RiskAnalysisTools>();

// Add health checks
builder.Services.AddHealthChecks();

// =============================================================================
// Application Configuration
// =============================================================================

var app = builder.Build();

// Behind a path-routing load balancer the service is mounted under a prefix (CWBI serves it at
// /total-risk). PathBase is read from configuration so the same image serves at the root when
// the setting is absent.
var pathBase = app.Configuration["PathBase"];
if (!string.IsNullOrEmpty(pathBase))
{
    app.UsePathBase(pathBase);
}

// Configure error handling
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}

// Configure OpenAPI endpoint
if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("EnableSwagger"))
{
    app.MapOpenApi();
}

// No HTTPS redirection outside Development: the deployed container serves plain HTTP behind a
// TLS-terminating load balancer, so a redirect would turn every health probe into a 307 and the
// service would never report healthy. In Development it is skipped for the same reason as the
// MCP transport: local clients connect over plain HTTP.

app.UseCors(app.Environment.IsDevelopment() ? "AllowAll" : "Production");
app.UseAuthorization();

// Map controllers
app.MapControllers();

// MCP endpoint (streamable HTTP transport)
app.MapMcp("/mcp");

// =============================================================================
// Health and Info Endpoints
// =============================================================================

// Health check endpoint
app.MapHealthChecks("/health");

// Detailed health check with version info
app.MapGet("/health/detailed", () => Results.Ok(new HealthCheckDto
{
    Status = "healthy",
    Timestamp = DateTime.UtcNow,
    Version = typeof(Program).Assembly.GetName().Version?.ToString()
}))
.WithName("DetailedHealthCheck")
.WithTags("Health")
.Produces<HealthCheckDto>(StatusCodes.Status200OK);

// Service info endpoint describing the exposed feature areas
app.MapGet("/api/info", () => Results.Ok(new ApiInfoDto
{
    Name = "RMC-TotalRisk API",
    Version = typeof(Program).Assembly.GetName().Version?.ToString(),
    ApiContractVersion = ApiContractInfo.Version,
    Description = "REST API and MCP server for the RMC-TotalRisk quantitative risk analysis engine (stateless round-trip compute).",
    Features = new List<string> { "compute", "validate", "metadata", "mcp" }
}))
.WithName("ApiInfo")
.WithTags("Info")
.Produces<ApiInfoDto>(StatusCodes.Status200OK);

// Error handler endpoint
app.MapGet("/error", () => Results.Problem(
    title: "An error occurred",
    statusCode: StatusCodes.Status500InternalServerError))
.ExcludeFromDescription();

// =============================================================================
// Run Application
// =============================================================================

app.Run();

/// <summary>
/// Marker partial class making the top-level-statement entry point visible to the integration
/// test host (<c>WebApplicationFactory&lt;Program&gt;</c>).
/// </summary>
public partial class Program { }
