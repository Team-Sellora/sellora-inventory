using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sellora.InventoryService.Application.Identity;
using Sellora.InventoryService.Application.Stock;
using Sellora.InventoryService.Domain.Entities;
using Sellora.InventoryService.Domain.Inventory;

namespace Sellora.InventoryService.Tests;

/// <summary>
/// End-to-end HTTP tests through the real pipeline: authorization policies,
/// tenant and caller scope, controllers and the PostgreSQL-backed services.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
public sealed class StockApiTests
{
    private readonly PostgreSqlFixture _fixture;
    private readonly InventoryApiFactory _api;
    private readonly Guid _companyId = Guid.NewGuid();
    private readonly InventoryOwner _company;
    private readonly InventoryOwner _agency;
    private readonly InventoryOwner _otherAgency;
    private readonly InventoryOwner _van;
    private readonly StockItem _companyStock;
    private readonly StockItem _agencyStock;
    private readonly StockItem _otherAgencyStock;
    private readonly StockItem _vanStock;

    public StockApiTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
        _api = fixture.Api;

        _company = TestData.Owner(_companyId, InventoryOwnerType.Company, _companyId, "Company Stock");
        _agency = TestData.Owner(_companyId, InventoryOwnerType.Agency, displayName: "Colombo Agency");
        _otherAgency = TestData.Owner(_companyId, InventoryOwnerType.Agency, displayName: "Kandy Agency");
        _van = TestData.Owner(_companyId, InventoryOwnerType.SalesRep, displayName: "Ruwan's van");

        _companyStock = TestData.Stock(_company, 50);
        _agencyStock = TestData.Stock(_agency, 3, reorderThreshold: 5);
        _otherAgencyStock = TestData.Stock(_otherAgency, 7);
        _vanStock = TestData.Stock(_van, 9);
    }

    private async Task SeedAsync()
    {
        await using var db = _fixture.CreateContext(_companyId);
        db.InventoryOwners.AddRange(_company, _agency, _otherAgency, _van);
        db.StockItems.AddRange(_companyStock, _agencyStock, _otherAgencyStock, _vanStock);
        await db.SaveChangesAsync();
    }

    private HttpClient Client(string role, string subject = "test-user", bool withCompany = true) =>
        _api.CreateClientFor(role, withCompany ? _companyId : null, subject);

    private HttpClient AgencyOperator()
    {
        var subject = $"operator-{Guid.NewGuid():N}";
        _api.Organization.SetScope(subject, new CallerScope(_agency.ExternalOwnerId, null));
        return Client("AgencyOperator", subject);
    }

    private HttpClient SalesRep()
    {
        var subject = $"rep-{Guid.NewGuid():N}";
        _api.Organization.SetScope(subject, new CallerScope(_agency.ExternalOwnerId, _van.ExternalOwnerId));
        return Client("SalesRep", subject);
    }

    private static async Task<List<StockItemResponse>> StockAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync($"/api/stock{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<StockItemResponse>>())!;
    }

    [Fact]
    public async Task Health_is_anonymous()
    {
        var response = await _api.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoints_require_authentication()
    {
        var client = _api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/stock")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/whoami")).StatusCode);
    }

    [Fact]
    public async Task Whoami_returns_the_callers_claims()
    {
        var response = await Client("CompanyAdmin").GetAsync("/whoami");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("CompanyAdmin", body);
        Assert.Contains(_companyId.ToString(), body);
    }

    [Fact]
    public async Task A_role_outside_the_policy_is_forbidden()
    {
        var response = await Client("ShopOwner").GetAsync("/api/stock");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Reads_without_a_company_claim_are_unauthorized()
    {
        var client = Client("CompanyAdmin", withCompany: false);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/stock")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/inventory-owners")).StatusCode);
    }

    [Fact]
    public async Task Company_admin_sees_all_company_stock_and_can_filter_it()
    {
        await SeedAsync();
        var client = Client("CompanyAdmin");

        var all = await StockAsync(client);
        Assert.Equal(4, all.Count);
        Assert.Contains(all, item => item.OwnerType == "Company" && item.OwnerDisplayName == "Company Stock");

        var byProduct = await StockAsync(client, $"?productId={_vanStock.ProductId}");
        Assert.Equal(_vanStock.StockItemId, Assert.Single(byProduct).StockItemId);

        var byOwner = await StockAsync(client, $"?inventoryOwnerId={_otherAgency.InventoryOwnerId}");
        Assert.Equal(_otherAgencyStock.StockItemId, Assert.Single(byOwner).StockItemId);

        var low = await StockAsync(client, "?lowStock=true");
        Assert.Equal(_agencyStock.StockItemId, Assert.Single(low).StockItemId);
    }

    [Fact]
    public async Task Agency_operator_sees_company_stock_and_only_their_own_agency()
    {
        await SeedAsync();

        var stock = await StockAsync(AgencyOperator());

        Assert.Equal(
            new[] { _companyStock.StockItemId, _agencyStock.StockItemId }.Order(),
            stock.Select(item => item.StockItemId).Order());
    }

    [Fact]
    public async Task Sales_rep_sees_only_their_van()
    {
        await SeedAsync();

        var stock = await StockAsync(SalesRep());

        Assert.Equal(_vanStock.StockItemId, Assert.Single(stock).StockItemId);
    }

    [Fact]
    public async Task A_scoped_role_without_an_organization_profile_sees_nothing()
    {
        await SeedAsync();

        var stock = await StockAsync(Client("SalesRep", $"no-profile-{Guid.NewGuid():N}"));

        Assert.Empty(stock);
    }

    [Fact]
    public async Task Inventory_owners_lists_active_owners_of_the_company()
    {
        await SeedAsync();

        var response = await Client("CompanyAdmin").GetAsync("/api/inventory-owners");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var owners = await response.Content.ReadFromJsonAsync<List<InventoryOwnerResponse>>();
        Assert.Equal(4, owners!.Count);
        Assert.Equal(
            new[] { "Agency", "Agency", "Company", "SalesRep" },
            owners.Select(owner => owner.OwnerType));
    }

    [Fact]
    public async Task Company_admin_adjusts_stock_and_the_ledger_balance_follows()
    {
        await SeedAsync();
        var client = Client("CompanyAdmin");
        var newProduct = Guid.NewGuid();

        var created = await client.PostAsJsonAsync("/api/stock/adjustments", new
        {
            InventoryOwnerId = _company.InventoryOwnerId,
            ProductId = newProduct,
            QuantityDelta = 12,
            Reason = "  Goods received  "
        });

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var adjustment = await created.Content.ReadFromJsonAsync<StockAdjustmentResponse>();
        Assert.Equal(12, adjustment!.QuantityOnHand);
        Assert.Equal("Goods received", adjustment.Reason);

        var reduced = await client.PostAsJsonAsync("/api/stock/adjustments", new
        {
            InventoryOwnerId = _company.InventoryOwnerId,
            ProductId = newProduct,
            QuantityDelta = -2,
            Reason = "Damaged"
        });

        Assert.Equal(HttpStatusCode.OK, reduced.StatusCode);
        Assert.Equal(10, (await reduced.Content.ReadFromJsonAsync<StockAdjustmentResponse>())!.QuantityOnHand);
    }

    [Fact]
    public async Task Restocking_above_the_threshold_rearms_the_low_stock_alert()
    {
        _agencyStock.LowStockNotified = true;
        await SeedAsync();

        var response = await AgencyOperator().PostAsJsonAsync("/api/stock/adjustments", new
        {
            InventoryOwnerId = _agency.InventoryOwnerId,
            ProductId = _agencyStock.ProductId,
            QuantityDelta = 10,
            Reason = "Restock"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = _fixture.CreateContext(_companyId);
        Assert.False(db.StockItems.Single(item => item.StockItemId == _agencyStock.StockItemId).LowStockNotified);
    }

    public static TheoryData<object, HttpStatusCode> InvalidAdjustments() => new()
    {
        { new { InventoryOwnerId = Guid.Empty, ProductId = Guid.NewGuid(), QuantityDelta = 1, Reason = "x" }, HttpStatusCode.BadRequest },
        { new { InventoryOwnerId = Guid.NewGuid(), ProductId = Guid.Empty, QuantityDelta = 1, Reason = "x" }, HttpStatusCode.BadRequest },
        { new { InventoryOwnerId = Guid.NewGuid(), ProductId = Guid.NewGuid(), QuantityDelta = 0, Reason = "x" }, HttpStatusCode.BadRequest },
        { new { InventoryOwnerId = Guid.NewGuid(), ProductId = Guid.NewGuid(), QuantityDelta = 1 }, HttpStatusCode.BadRequest },
        { new { InventoryOwnerId = Guid.NewGuid(), ProductId = Guid.NewGuid(), QuantityDelta = 1, Reason = "x" }, HttpStatusCode.NotFound },
    };

    [Theory]
    [MemberData(nameof(InvalidAdjustments))]
    public async Task Invalid_adjustments_are_rejected(object body, HttpStatusCode expected)
    {
        var response = await Client("CompanyAdmin").PostAsJsonAsync("/api/stock/adjustments", body);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Adjustments_that_would_go_negative_conflict()
    {
        await SeedAsync();

        var response = await Client("CompanyAdmin").PostAsJsonAsync("/api/stock/adjustments", new
        {
            InventoryOwnerId = _company.InventoryOwnerId,
            ProductId = _companyStock.ProductId,
            QuantityDelta = -51,
            Reason = "Too many"
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Adjustments_need_a_tenant_a_subject_and_an_owned_agency()
    {
        await SeedAsync();
        var body = new
        {
            InventoryOwnerId = _otherAgency.InventoryOwnerId,
            ProductId = _otherAgencyStock.ProductId,
            QuantityDelta = 1,
            Reason = "Count"
        };

        var noTenant = await Client("CompanyAdmin", withCompany: false).PostAsJsonAsync("/api/stock/adjustments", body);
        var noSubject = await Client("CompanyAdmin", subject: "").PostAsJsonAsync("/api/stock/adjustments", body);
        var otherAgency = await AgencyOperator().PostAsJsonAsync("/api/stock/adjustments", body);
        var salesRep = await SalesRep().PostAsJsonAsync("/api/stock/adjustments", body);

        Assert.Equal(HttpStatusCode.Unauthorized, noTenant.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, noSubject.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, otherAgency.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, salesRep.StatusCode);
    }

    [Fact]
    public async Task Reorder_threshold_updates_are_validated_and_scoped()
    {
        await SeedAsync();
        var admin = Client("CompanyAdmin");

        var updated = await admin.PutAsJsonAsync(
            $"/api/stock/{_companyStock.StockItemId}/reorder-threshold", new { ReorderThreshold = 20 });
        var negative = await admin.PutAsJsonAsync(
            $"/api/stock/{_companyStock.StockItemId}/reorder-threshold", new { ReorderThreshold = -1 });
        var missing = await admin.PutAsJsonAsync(
            $"/api/stock/{Guid.NewGuid()}/reorder-threshold", new { ReorderThreshold = 1 });
        var ownAgency = await AgencyOperator().PutAsJsonAsync(
            $"/api/stock/{_agencyStock.StockItemId}/reorder-threshold", new { ReorderThreshold = 1 });
        var otherAgency = await AgencyOperator().PutAsJsonAsync(
            $"/api/stock/{_otherAgencyStock.StockItemId}/reorder-threshold", new { ReorderThreshold = 1 });

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(20, (await updated.Content.ReadFromJsonAsync<StockItemResponse>())!.ReorderThreshold);
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ownAgency.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, otherAgency.StatusCode);
    }

    [Fact]
    public async Task Availability_reports_each_requested_line()
    {
        await SeedAsync();

        var response = await Client("SalesRep").PostAsJsonAsync("/api/stock/availability", new
        {
            InventoryOwnerId = _company.InventoryOwnerId,
            Lines = new[]
            {
                new { ProductId = _companyStock.ProductId, Quantity = 10 },
                new { ProductId = Guid.NewGuid(), Quantity = 1 }
            }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lines = await response.Content.ReadFromJsonAsync<List<StockAvailability>>();
        Assert.True(lines![0].IsAvailable);
        Assert.False(lines[1].IsAvailable);
    }

    [Fact]
    public async Task Reservations_can_be_made_released_and_confirmed()
    {
        await SeedAsync();
        var client = Client("CompanyAdmin");

        async Task<StockReservationResponse> ReserveAsync(string reference)
        {
            var response = await client.PostAsJsonAsync("/api/stock/reservations", new
            {
                OrderReference = reference,
                InventoryOwnerId = _company.InventoryOwnerId,
                Lines = new[] { new { ProductId = _companyStock.ProductId, Quantity = 4 } }
            });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<StockReservationResponse>())!;
        }

        var released = await ReserveAsync($"ORD-{Guid.NewGuid():N}");
        var release = await client.PostAsync($"/api/stock/reservations/{released.ReservationId}/release", null);
        var releaseAgain = await client.PostAsync($"/api/stock/reservations/{released.ReservationId}/release", null);

        var confirmed = await ReserveAsync($"ORD-{Guid.NewGuid():N}");
        var confirm = await client.PostAsync($"/api/stock/reservations/{confirmed.ReservationId}/confirm", null);
        var confirmAgain = await client.PostAsync($"/api/stock/reservations/{confirmed.ReservationId}/confirm", null);

        Assert.Equal(HttpStatusCode.OK, release.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, releaseAgain.StatusCode);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        Assert.Equal("Confirmed", (await confirm.Content.ReadFromJsonAsync<StockReservationResponse>())!.Status);
        Assert.Equal(HttpStatusCode.Conflict, confirmAgain.StatusCode);
    }

    [Fact]
    public async Task Reservation_failures_map_to_http_status_codes()
    {
        await SeedAsync();
        var client = Client("CompanyAdmin");

        var shortage = await client.PostAsJsonAsync("/api/stock/reservations", new
        {
            OrderReference = $"ORD-{Guid.NewGuid():N}",
            InventoryOwnerId = _company.InventoryOwnerId,
            Lines = new[] { new { ProductId = _companyStock.ProductId, Quantity = 500 } }
        });
        var invalid = await client.PostAsJsonAsync("/api/stock/reservations", new
        {
            InventoryOwnerId = _company.InventoryOwnerId
        });
        var unknownOwner = await client.PostAsJsonAsync("/api/stock/reservations", new
        {
            OrderReference = $"ORD-{Guid.NewGuid():N}",
            InventoryOwnerId = Guid.NewGuid(),
            Lines = new[] { new { ProductId = _companyStock.ProductId, Quantity = 1 } }
        });
        var unknownReservation = await client.PostAsync($"/api/stock/reservations/{Guid.NewGuid()}/confirm", null);
        var noTenant = await Client("CompanyAdmin", withCompany: false)
            .PostAsync($"/api/stock/reservations/{Guid.NewGuid()}/release", null);

        Assert.Equal(HttpStatusCode.Conflict, shortage.StatusCode);
        using (var body = JsonDocument.Parse(await shortage.Content.ReadAsStringAsync()))
        {
            Assert.Equal(1, body.RootElement.GetProperty("shortages").GetArrayLength());
        }

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownOwner.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownReservation.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, noTenant.StatusCode);
    }

    [Fact]
    public async Task Fulfilment_resolves_against_the_agency_owner()
    {
        await SeedAsync();

        var response = await AgencyOperator().PostAsJsonAsync("/api/stock/fulfilment/resolve", new
        {
            OrderReference = $"ORD-{Guid.NewGuid():N}",
            AgencyId = _agency.ExternalOwnerId,
            Lines = new[] { new { ProductId = _agencyStock.ProductId, Quantity = 2 } }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reservation = await response.Content.ReadFromJsonAsync<StockReservationResponse>();
        Assert.Equal(_agency.InventoryOwnerId, reservation!.InventoryOwnerId);
    }

    [Fact]
    public async Task Fulfilment_requires_an_agency_and_lines()
    {
        var response = await Client("CompanyAdmin").PostAsJsonAsync("/api/stock/fulfilment/resolve", new
        {
            OrderReference = "ORD-EMPTY-AGENCY",
            AgencyId = Guid.Empty,
            Lines = new[] { new { ProductId = Guid.NewGuid(), Quantity = 1 } }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
