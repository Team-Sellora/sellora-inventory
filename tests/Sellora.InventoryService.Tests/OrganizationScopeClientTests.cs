using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Sellora.InventoryService.Api.Identity;
using Sellora.InventoryService.Application.Identity;

namespace Sellora.InventoryService.Tests;

public sealed class OrganizationScopeClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(respond(request));
        }
    }

    private static (OrganizationScopeClient Client, StubHandler Handler) Create(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        string? authorization = "Bearer caller-token",
        bool withBaseAddress = true)
    {
        var handler = new StubHandler(respond);
        var http = new HttpClient(handler);
        if (withBaseAddress)
        {
            http.BaseAddress = new Uri("https://organization.test/");
        }

        var context = new DefaultHttpContext();
        if (authorization is not null)
        {
            context.Request.Headers.Authorization = authorization;
        }

        return (new OrganizationScopeClient(http, new HttpContextAccessor { HttpContext = context }), handler);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Returns_the_scope_and_forwards_the_callers_token()
    {
        var agencyId = Guid.NewGuid();
        var (client, handler) = Create(_ => Json($$"""{"agencyId":"{{agencyId}}","salesRepId":null}"""));

        var scope = await client.GetCallerScopeAsync(CancellationToken.None);

        Assert.Equal(new CallerScope(agencyId, null), scope);
        Assert.Equal("https://organization.test/api/me/scope", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal("caller-token", handler.Request.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task Sends_no_authorization_when_the_caller_has_none()
    {
        var (client, handler) = Create(_ => Json("""{"agencyId":null,"salesRepId":null}"""), authorization: null);

        await client.GetCallerScopeAsync(CancellationToken.None);

        Assert.Null(handler.Request!.Headers.Authorization);
    }

    [Fact]
    public async Task Not_found_means_no_profile()
    {
        var (client, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        Assert.Null(await client.GetCallerScopeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Server_errors_are_unavailable()
    {
        var (client, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        var exception = await Assert.ThrowsAsync<OrganizationUnavailableException>(
            () => client.GetCallerScopeAsync(CancellationToken.None));

        Assert.Contains("502", exception.Message);
    }

    [Fact]
    public async Task An_empty_body_is_unavailable()
    {
        var (client, _) = Create(_ => Json("null"));

        await Assert.ThrowsAsync<OrganizationUnavailableException>(
            () => client.GetCallerScopeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Network_failures_are_unavailable()
    {
        var (client, _) = Create(_ => throw new HttpRequestException("connection refused"));

        var exception = await Assert.ThrowsAsync<OrganizationUnavailableException>(
            () => client.GetCallerScopeAsync(CancellationToken.None));

        Assert.IsType<HttpRequestException>(exception.InnerException);
    }

    [Fact]
    public async Task A_missing_base_url_is_unavailable()
    {
        var (client, _) = Create(_ => Json("{}"), withBaseAddress: false);

        await Assert.ThrowsAsync<OrganizationUnavailableException>(
            () => client.GetCallerScopeAsync(CancellationToken.None));
    }
}
