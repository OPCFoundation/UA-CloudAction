namespace UACloudAction.Services
{
    using Microsoft.AspNetCore.Authentication;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using System.Security.Claims;
    using System.Text;
    using System.Text.Encodings.Web;

    /// <summary>
    /// HTTP Basic authentication handler used to protect the OPC UA Web API. Credentials are
    /// validated against the same <c>ADMIN_USERNAME</c>/<c>ADMIN_PASSWORD</c> environment
    /// variables used by the interactive (cookie) login.
    /// </summary>
    public sealed class BasicAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "Basic";

        public BasicAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("Authorization", out var authorizationHeader))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            string header = authorizationHeader.ToString();
            if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            string? expectedUser = Environment.GetEnvironmentVariable("ADMIN_USERNAME");
            string? expectedPassword = Environment.GetEnvironmentVariable("ADMIN_PASSWORD");
            if (string.IsNullOrEmpty(expectedUser) || string.IsNullOrEmpty(expectedPassword))
            {
                return Task.FromResult(AuthenticateResult.Fail("Basic authentication is not configured."));
            }

            string username;
            string password;
            try
            {
                string encoded = header["Basic ".Length..].Trim();
                string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                int separator = decoded.IndexOf(':');
                if (separator < 0)
                {
                    return Task.FromResult(AuthenticateResult.Fail("Invalid Basic authentication header."));
                }

                username = decoded[..separator];
                password = decoded[(separator + 1)..];
            }
            catch (FormatException)
            {
                return Task.FromResult(AuthenticateResult.Fail("Invalid Basic authentication header."));
            }

            if (!string.Equals(username, expectedUser, StringComparison.Ordinal)
                || !string.Equals(password, expectedPassword, StringComparison.Ordinal))
            {
                return Task.FromResult(AuthenticateResult.Fail("Invalid username or password."));
            }

            Claim[] claims =
            {
                new Claim(ClaimTypes.Name, username),
                new Claim(ClaimTypes.Role, "Administrator")
            };

            ClaimsIdentity identity = new(claims, SchemeName);
            ClaimsPrincipal principal = new(identity);
            AuthenticationTicket ticket = new(principal, SchemeName);

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.Headers.WWWAuthenticate = "Basic realm=\"UA-CloudAction OPC UA Web API\", charset=\"UTF-8\"";
            return base.HandleChallengeAsync(properties);
        }
    }
}
