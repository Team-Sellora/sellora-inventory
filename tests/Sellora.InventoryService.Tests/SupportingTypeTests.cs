using System.Security.Claims;
using Confluent.Kafka;
using Microsoft.AspNetCore.Http;
using Sellora.InventoryService.Api.Identity;
using Sellora.InventoryService.Api.Tenancy;
using Sellora.InventoryService.Application.Stock;
using Sellora.InventoryService.Domain.Entities;
using Sellora.InventoryService.Domain.Inventory;
using Sellora.InventoryService.Infrastructure.Kafka;

namespace Sellora.InventoryService.Tests;

public sealed class SupportingTypeTests
{
    private static HttpContextAccessor Accessor(params Claim[] claims) => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
        }
    };

    [Fact]
    public void Tenant_comes_from_the_company_claim()
    {
        var companyId = Guid.NewGuid();

        Assert.Equal(companyId, new HttpTenantContext(Accessor(new Claim("companyId", companyId.ToString()))).CompanyId);
        Assert.Null(new HttpTenantContext(Accessor(new Claim("companyId", "not-a-guid"))).CompanyId);
        Assert.Null(new HttpTenantContext(new HttpContextAccessor { HttpContext = null }).CompanyId);
    }

    [Fact]
    public void A_system_tenant_scope_overrides_the_claim_and_restores_on_dispose()
    {
        var claimCompany = Guid.NewGuid();
        var outerCompany = Guid.NewGuid();
        var innerCompany = Guid.NewGuid();
        var tenant = new HttpTenantContext(Accessor(new Claim("companyId", claimCompany.ToString())));

        using (tenant.BeginSystemTenantScope(outerCompany))
        {
            Assert.Equal(outerCompany, tenant.CompanyId);

            using (tenant.BeginSystemTenantScope(innerCompany))
            {
                Assert.Equal(innerCompany, tenant.CompanyId);
            }

            Assert.Equal(outerCompany, tenant.CompanyId);
        }

        Assert.Equal(claimCompany, tenant.CompanyId);
        Assert.Throws<ArgumentOutOfRangeException>(() => tenant.BeginSystemTenantScope(Guid.Empty));
    }

    [Fact]
    public void Current_user_reads_subject_and_role_from_any_supported_claim()
    {
        // HttpContextAccessor shares one ambient context, so read each user
        // before the next accessor replaces it.
        var standard = new HttpCurrentUserContext(Accessor(
            new Claim(ClaimTypes.NameIdentifier, "user-1"),
            new Claim(ClaimTypes.Role, "CompanyAdmin")));
        Assert.Equal(("user-1", "CompanyAdmin"), (standard.Subject, standard.Role));

        var wso2 = new HttpCurrentUserContext(Accessor(new Claim("sub", "user-2"), new Claim("roles", "SalesRep")));
        Assert.Equal(("user-2", "SalesRep"), (wso2.Subject, wso2.Role));

        var legacy = new HttpCurrentUserContext(Accessor(new Claim("role", "AgencyOperator")));
        Assert.Equal("AgencyOperator", legacy.Role);

        var anonymous = new HttpCurrentUserContext(new HttpContextAccessor { HttpContext = null });
        Assert.Null(anonymous.Subject);
        Assert.Null(anonymous.Role);
        Assert.Null(anonymous.AgencyId);
    }

    [Fact]
    public void Sasl_is_applied_only_when_a_username_is_configured()
    {
        var local = new ProducerConfig();
        KafkaSaslConfigurator.Apply(local, "  ", "ignored");

        var managed = new ProducerConfig();
        KafkaSaslConfigurator.Apply(managed, "api-key", "api-secret");

        Assert.Null(local.SecurityProtocol);
        Assert.Null(local.SaslUsername);
        Assert.Equal(SecurityProtocol.SaslSsl, managed.SecurityProtocol);
        Assert.Equal(SaslMechanism.Plain, managed.SaslMechanism);
        Assert.Equal("api-key", managed.SaslUsername);
        Assert.Equal("api-secret", managed.SaslPassword);
    }

    [Fact]
    public void Adjust_stock_results_carry_their_outcome_and_message()
    {
        var productId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();

        var results = new[]
        {
            (AdjustStockResult.InvalidRequest("bad"), AdjustStockOutcome.InvalidRequest),
            (AdjustStockResult.TenantNotAvailable(), AdjustStockOutcome.TenantNotAvailable),
            (AdjustStockResult.CallerNotAuthorized(), AdjustStockOutcome.CallerNotAuthorized),
            (AdjustStockResult.InventoryOwnerNotFound(ownerId), AdjustStockOutcome.InventoryOwnerNotFound),
            (AdjustStockResult.ProductNotFound(productId), AdjustStockOutcome.ProductNotFound),
            (AdjustStockResult.InsufficientStock(), AdjustStockOutcome.InsufficientStock),
            (AdjustStockResult.ConcurrencyConflict(), AdjustStockOutcome.ConcurrencyConflict),
        };

        Assert.All(results, pair =>
        {
            Assert.Equal(pair.Item2, pair.Item1.Outcome);
            Assert.False(string.IsNullOrWhiteSpace(pair.Item1.Message));
            Assert.Null(pair.Item1.Adjustment);
        });
        Assert.Contains(productId.ToString(), results[4].Item1.Message);
        Assert.Contains(ownerId.ToString(), results[3].Item1.Message);
    }

    [Fact]
    public void Stock_item_rejects_invalid_movements()
    {
        var item = new StockItem { StockItemId = Guid.NewGuid() };
        StockMovement Movement(int onHand, int reserved, Guid? id = null, Guid? stockItemId = null) => new()
        {
            StockMovementId = id ?? Guid.NewGuid(),
            StockItemId = stockItemId ?? item.StockItemId,
            MovementType = StockMovementType.Adjustment,
            OnHandDelta = onHand,
            ReservedDelta = reserved,
            OccurredAt = DateTimeOffset.UtcNow
        };

        Assert.Throws<ArgumentNullException>(() => item.ApplyMovement(null!));
        Assert.Throws<InvalidOperationException>(() => item.ApplyMovement(Movement(1, 0, Guid.Empty)));
        Assert.Throws<InvalidOperationException>(() => item.ApplyMovement(Movement(1, 0, stockItemId: Guid.NewGuid())));
        Assert.Throws<InvalidOperationException>(() => item.ApplyMovement(Movement(5, -1)));
        Assert.Equal(0, item.QuantityOnHand);
    }
}
