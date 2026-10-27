using System.Security.Claims;
using System.Text.Encodings.Web;
using MassTransit;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaymentsModule.Data;

namespace PaymentsModule.Tests;

/// <summary>
/// Boots the real Program.cs pipeline in memory (routing, authentication, authorization, UseExceptionHandler and
/// MVC serialization) so tests assert real HTTP status codes and bodies. Shared through <c>IClassFixture</c>.
/// Only what does not exist in a test run is swapped:
/// <list type="bullet">
/// <item>Authentication: the API Gateway JWT becomes <see cref="TestAuthHandler"/> (header based).</item>
/// <item>MassTransit: RabbitMQ becomes MassTransit's in-memory test harness.</item>
/// <item>Database: the SQL Server test database, with <see cref="Failures"/> to simulate timeouts.</item>
/// </list>
/// Runs against a real SQL Server: `docker compose up -d sqlserver` locally, a service container in CI.
/// </summary>
public sealed class PaymentsApiFactory : WebApplicationFactory<Program>
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer")
        ?? "Server=localhost,14330;Database=payments_tests;User Id=sa;Password=Payments_Dev_2026!;TrustServerCertificate=True";

    /// <summary>Makes queries fail like a SQL Server timeout for chosen parameter values (e.g. a folio).</summary>
    public FailingQueries Failures { get; } = new();

    /// <summary>A caller without credentials, as if the request arrived with no JWT.</summary>
    public HttpClient CreateAnonymousClient() => CreateClient(HttpsClientOptions());

    /// <summary>An authenticated caller; <paramref name="roles"/> become role claims for <c>[Authorize(Roles = ...)]</c>.</summary>
    public HttpClient CreateAuthenticatedClient(string user = "cashier-http-tests", params string[] roles)
    {
        var client = CreateClient(HttpsClientOptions());
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        if (roles.Length > 0)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(',', roles));
        }

        return client;
    }

    // HTTPS so UseHttpsRedirection never answers a redirect instead of the real status code.
    private static WebApplicationFactoryClientOptions HttpsClientOptions() => new()
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not Development: no developer exception page or Swagger, the same pipeline production runs.
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // Replaces the RabbitMQ bus configured in Program.cs with the in-memory one; consumers stay registered.
            services.AddMassTransitTestHarness();

            services.RemoveAll<IDbContextOptionsConfiguration<PaymentsDbContext>>();
            services.AddDbContext<PaymentsDbContext>(o => o.UseSqlServer(ConnectionString).AddInterceptors(Failures));
        });
    }
}

/// <summary>
/// Stand-in for the API Gateway JWT. A request is authenticated only when it carries <see cref="UserHeader"/>;
/// otherwise authorization challenges with 401, exactly like a request without a token.
/// </summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string UserHeader = "X-Test-User";
    public const string RolesHeader = "X-Test-Roles";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers[UserHeader].ToString();
        if (string.IsNullOrWhiteSpace(user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user) };
        claims.AddRange(Request.Headers[RolesHeader].ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(role => new Claim(ClaimTypes.Role, role)));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
