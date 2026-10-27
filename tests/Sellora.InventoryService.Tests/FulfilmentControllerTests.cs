using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Sellora.InventoryService.Api.Contracts;
using Sellora.InventoryService.Api.Controllers;
using Sellora.InventoryService.Application.Identity;
using Sellora.InventoryService.Application.Stock;
using Sellora.InventoryService.Domain.Tenancy;

namespace Sellora.InventoryService.Tests;

public sealed class FulfilmentControllerTests
{
    [Theory]
    [InlineData("AgencyOperator", true, true, 200)]
    [InlineData("AgencyOperator", false, true, 403)]
    [InlineData("SalesRep", true, true, 200)]
    [InlineData("SalesRep", false, true, 403)]
    [InlineData("CompanyAdmin", false, true, 200)]
    [InlineData("AgencyOperator", true, false, 401)]
    [InlineData("CompanyAdmin", false, false, 401)]
    public async Task Resolve_enforces_tenant_and_agency_scope(
        string role,
        bool matchingAgency,
        bool hasTenant,
        int expectedStatus)
    {
        var requestedAgencyId = Guid.NewGuid();
        var callerAgencyId = matchingAgency
            ? requestedAgencyId
            : Guid.NewGuid();

        var tenant = new TenantStub(
            hasTenant ? Guid.NewGuid() : null);

        // These dependencies are unused by ResolveFulfilment.
        var controller = new StockController(
            null!, null!, tenant, null!);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim("roles", role) },
                    "Test",
                    ClaimTypes.Name,
                    "roles"))
            }
        };

        var body = new ResolveFulfilmentRequestBody
        {
            OrderReference = "ORDER-SCOPE-001",
            AgencyId = requestedAgencyId,
            Lines = new[]
            {
                new ReservationLineRequestBody
                {
                    ProductId = Guid.NewGuid(),
                    Quantity = 2
                }
            }
        };

        var resolver = new ResolverStub();

        var response = await controller.ResolveFulfilment(
            body,
            resolver,
            new UserStub(role, callerAgencyId),
            CancellationToken.None);

        if (expectedStatus == 200)
        {
            Assert.IsType<OkObjectResult>(response.Result);

            var request = Assert.Single(resolver.Requests);
            Assert.Equal(body.OrderReference, request.OrderReference);
            Assert.Equal(requestedAgencyId, request.AgencyId);

            var line = Assert.Single(request.Lines);
            Assert.Equal(body.Lines.Single().ProductId, line.ProductId);
            Assert.Equal(2, line.Quantity);
        }
        else
        {
            Assert.Empty(resolver.Requests);

            if (expectedStatus == 403)
            {
                Assert.IsType<ForbidResult>(response.Result);
            }
            else
            {
                Assert.IsType<UnauthorizedObjectResult>(response.Result);
            }
        }
    }

    private sealed record TenantStub(Guid? CompanyId) : ITenantContext;

    private sealed record UserStub(
        string? Role,
        Guid? AgencyId) : ICurrentUserContext
    {
        public string? Subject => "test-user";
        public Guid? SalesRepId => null;
    }

    private sealed class ResolverStub : IFulfilmentResolver
    {
        public List<ResolveFulfilmentRequest> Requests { get; } = new();

        public Task<ReserveStockResult> ResolveFulfilmentSourceAsync(
            ResolveFulfilmentRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);

            return Task.FromResult(ReserveStockResult.Success(
                new StockReservationResponse(
                    Guid.NewGuid(),
                    request.OrderReference,
                    Guid.NewGuid(),
                    "Active",
                    DateTimeOffset.UtcNow.AddMinutes(15),
                    request.Lines
                        .Select(line => new ReservationLineResponse(
                            Guid.NewGuid(),
                            line.ProductId,
                            line.BatchId,
                            line.Quantity))
                        .ToArray())));
        }
    }
}