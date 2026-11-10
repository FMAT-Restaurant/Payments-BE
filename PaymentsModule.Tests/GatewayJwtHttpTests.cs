using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace PaymentsModule.Tests;

// The real JwtBearer configured in Program.cs (not TestAuthHandler) validating tokens signed like Auth-Backend / Kong:
// HS256 with the literal JWT_SECRET, issuer urn:fmat:restaurant:auth, no `aud`. Also boots with MigrateOnStartup.
public class GatewayJwtHttpTests(PaymentsApiFactory api) : IClassFixture<PaymentsApiFactory>, IAsyncLifetime
{
    private const string Secret = "gateway-jwt-tests-secret-0123456789abcdef";
    private const string Issuer = "urn:fmat:restaurant:auth";

    public Task InitializeAsync() => PaymentsApiFactory.EnsureDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Gateway_token_authenticates_and_maps_sub_and_roles()
    {
        using var client = CreateJwtClient(CreateToken(Secret, Issuer, "Cajero"));

        // Cashier role reaches its own endpoint (no open shift -> 404), but not a supervisor one (403).
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/payments/cash-shifts/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/payments/cash-shifts/open")).StatusCode);
    }

    [Theory]
    [InlineData("wrong-secret-0123456789abcdef0123456789", Issuer)]
    [InlineData(Secret, "urn:another:issuer")]
    public async Task Token_with_wrong_secret_or_issuer_returns_401(string secret, string issuer)
    {
        using var client = CreateJwtClient(CreateToken(secret, issuer, "Cajero"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/payments/cash-shifts/me")).StatusCode);
    }

    private HttpClient CreateJwtClient(string token)
    {
        var client = api.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Authentication:Schemes:Bearer:Secret", Secret);
            builder.UseSetting("Authentication:Schemes:Bearer:ValidIssuer", Issuer);
            builder.UseSetting("Database:MigrateOnStartup", "true");
            builder.ConfigureTestServices(services => services.Configure<AuthenticationOptions>(o =>
            {
                o.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
                o.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            }));
        }).CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static string CreateToken(string secret, string issuer, params string[] roles) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Subject = new ClaimsIdentity([new Claim("sub", $"cashier-{Guid.NewGuid()}")]),
            Claims = new Dictionary<string, object> { ["roles"] = roles },
            Expires = DateTime.UtcNow.AddMinutes(15),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)), SecurityAlgorithms.HmacSha256),
        });
}
