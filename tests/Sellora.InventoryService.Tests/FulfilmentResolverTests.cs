using Sellora.InventoryService.Application.Stock;
using Sellora.InventoryService.Domain.Tenancy;

namespace Sellora.InventoryService.Tests;

public sealed class FulfilmentResolverTests
{
    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(true, false, 2)]
    [InlineData(true, true, 2)]
    public async Task Resolve_uses_whole_basket_and_preserves_shortages(
        bool agencyShort,
        bool companyShort,
        int expectedAttempts)
    {
        var owners = new OwnerLookupStub();
        var lines = Enumerable.Range(1, 5)
            .Select(quantity => new ReservationLineRequest(
                Guid.NewGuid(), null, quantity))
            .ToArray();

        var agencyFailure = ReserveStockResult.Failure(
            ReservationOutcome.InsufficientStock,
            "Agency shortage.",
            new[]
            {
                new ReservationShortage(
                    lines[4].ProductId, null, 5, 4)
            });

        var companyFailure = ReserveStockResult.Failure(
            ReservationOutcome.InsufficientStock,
            "Company shortage.",
            new[]
            {
                new ReservationShortage(
                    lines[4].ProductId, null, 5, 2)
            });

        var reservations = new ReservationServiceStub(request =>
        {
            if (request.InventoryOwnerId == owners.AgencyOwnerId)
            {
                return agencyShort
                    ? agencyFailure
                    : SuccessfulReservation(request);
            }

            Assert.Equal(owners.CompanyOwnerId, request.InventoryOwnerId);

            return companyShort
                ? companyFailure
                : SuccessfulReservation(request);
        });

        var resolver = new FulfilmentResolver(
            owners, reservations, new TenantStub());

        var result = await resolver.ResolveFulfilmentSourceAsync(
            new ResolveFulfilmentRequest(
                "ORDER-FULFILMENT-001", owners.AgencyId, lines));

        Assert.Equal(expectedAttempts, reservations.Attempts.Count);
        Assert.Equal(
            owners.AgencyOwnerId,
            reservations.Attempts[0].InventoryOwnerId);
        Assert.Equal(agencyShort ? 1 : 0, owners.CompanyLookups);

        Assert.All(reservations.Attempts, attempt =>
        {
            Assert.Equal("ORDER-FULFILMENT-001", attempt.OrderReference);
            Assert.Equal(lines, attempt.Lines.ToArray());
        });

        if (agencyShort && companyShort)
        {
            Assert.Equal(
                ReservationOutcome.InsufficientStock, result.Outcome);
            Assert.Null(result.Reservation);

            var shortage = Assert.Single(result.Shortages);
            Assert.Equal(lines[4].ProductId, shortage.ProductId);
            Assert.Equal(5, shortage.RequestedQuantity);
            Assert.Equal(2, shortage.AvailableQuantity);
        }
        else
        {
            Assert.Equal(ReservationOutcome.Success, result.Outcome);
            Assert.NotNull(result.Reservation);
            Assert.Equal(
                agencyShort ? owners.CompanyOwnerId : owners.AgencyOwnerId,
                result.Reservation.InventoryOwnerId);
            Assert.Equal(5, result.Reservation.Lines.Count);
            Assert.Empty(result.Shortages);
        }
    }

    private static ReserveStockResult SuccessfulReservation(
        ReserveStockRequest request) =>
        ReserveStockResult.Success(new StockReservationResponse(
            Guid.NewGuid(),
            request.OrderReference,
            request.InventoryOwnerId,
            "Active",
            DateTimeOffset.UtcNow.AddMinutes(15),
            request.Lines
                .Select(line => new ReservationLineResponse(
                    Guid.NewGuid(),
                    line.ProductId,
                    line.BatchId,
                    line.Quantity))
                .ToArray()));

    private sealed class TenantStub : ITenantContext
    {
        public Guid? CompanyId { get; } = Guid.NewGuid();
    }

    private sealed class OwnerLookupStub : IFulfilmentOwnerLookup
    {
        public Guid AgencyId { get; } = Guid.NewGuid();
        public Guid AgencyOwnerId { get; } = Guid.NewGuid();
        public Guid CompanyOwnerId { get; } = Guid.NewGuid();
        public int CompanyLookups { get; private set; }

        public Task<Guid?> FindAgencyOwnerAsync(
            Guid agencyId,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(AgencyId, agencyId);
            return Task.FromResult<Guid?>(AgencyOwnerId);
        }

        public Task<Guid?> FindCompanyOwnerAsync(
            CancellationToken cancellationToken = default)
        {
            CompanyLookups++;
            return Task.FromResult<Guid?>(CompanyOwnerId);
        }
    }

    private sealed class ReservationServiceStub(
        Func<ReserveStockRequest, ReserveStockResult> reserve)
        : IStockReservationService
    {
        public List<ReserveStockRequest> Attempts { get; } = new();

        public Task<ReserveStockResult> ReserveAsync(
            ReserveStockRequest request,
            CancellationToken cancellationToken = default)
        {
            Attempts.Add(request);
            return Task.FromResult(reserve(request));
        }

        public Task<IReadOnlyCollection<StockAvailability>>
            CheckAvailabilityAsync(
                CheckAvailabilityRequest request,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ReserveStockResult> ReleaseAsync(
            Guid reservationId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ReserveStockResult> ConfirmAsync(
            Guid reservationId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}