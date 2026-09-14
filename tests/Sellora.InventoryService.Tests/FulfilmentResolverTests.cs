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

        var decisionLogger = new RecordingFulfilmentDecisionLogger();
        var resolver = new FulfilmentResolver(
            owners, reservations, new TenantStub(), decisionLogger);

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

        Assert.Equal(agencyShort ? 2 : 1, decisionLogger.Entries.Count);

        Assert.All(decisionLogger.Entries, entry =>
        {
            Assert.Equal("ORDER-FULFILMENT-001", entry.OrderReference);
            Assert.Equal(owners.AgencyId, entry.AgencyId);
            Assert.False(string.IsNullOrWhiteSpace(entry.Reason));
        });

        if (agencyShort)
        {
            var fallback = decisionLogger.Entries[0];
            Assert.Equal("CompanyFallback", fallback.Decision);
            Assert.Equal(owners.AgencyOwnerId, fallback.InventoryOwnerId);
            Assert.Contains("Agency stock cannot fulfil", fallback.Reason);
        }

        var finalDecision = decisionLogger.Entries[^1];

        if (agencyShort && companyShort)
        {
            Assert.Equal("Rejected", finalDecision.Decision);
            Assert.Null(finalDecision.InventoryOwnerId);
            Assert.Contains("InsufficientStock", finalDecision.Reason);
            Assert.Contains(
                "Neither the agency nor the company",
                finalDecision.Reason);
        }
        else
        {
            Assert.Equal("ReservationResolved", finalDecision.Decision);
            Assert.Equal(
                agencyShort ? owners.CompanyOwnerId : owners.AgencyOwnerId,
                finalDecision.InventoryOwnerId);
        }

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

    [Theory]
    [InlineData(ReservationOutcome.InvalidRequest)]
    [InlineData(ReservationOutcome.TenantNotAvailable)]
    [InlineData(ReservationOutcome.InventoryOwnerNotFound)]
    [InlineData(ReservationOutcome.ReservationAlreadyReleased)]
    [InlineData(ReservationOutcome.ReservationAlreadyConfirmed)]
    public async Task Resolve_does_not_fallback_for_non_stock_errors(
    ReservationOutcome outcome)
    {
        var owners = new OwnerLookupStub();
        var failure = ReserveStockResult.Failure(
            outcome, "Reservation could not proceed.");

        var reservations = new ReservationServiceStub(_ => failure);
        var decisionLogger = new RecordingFulfilmentDecisionLogger();
        var resolver = new FulfilmentResolver(
            owners, reservations, new TenantStub(), decisionLogger);

        var result = await resolver.ResolveFulfilmentSourceAsync(
            new ResolveFulfilmentRequest(
                "ORDER-ERROR-001",
                owners.AgencyId,
                new[]
                {
                new ReservationLineRequest(Guid.NewGuid(), null, 1)
                }));

        Assert.Same(failure, result);
        Assert.Single(reservations.Attempts);
        Assert.Equal(0, owners.CompanyLookups);

        var decision = Assert.Single(decisionLogger.Entries);
        Assert.Equal("Rejected", decision.Decision);
        Assert.Equal("ORDER-ERROR-001", decision.OrderReference);
        Assert.Equal(owners.AgencyId, decision.AgencyId);
        Assert.Null(decision.InventoryOwnerId);
        Assert.Contains(outcome.ToString(), decision.Reason);
        Assert.Contains("Reservation could not proceed.", decision.Reason);
    }

    [Fact]
    public async Task Resolve_preserves_existing_company_reservation_on_retry()
    {
        var owners = new OwnerLookupStub();
        var lines = new[]
        {
        new ReservationLineRequest(Guid.NewGuid(), null, 3)
    };

        var existing = SuccessfulReservation(new ReserveStockRequest(
            "ORDER-RETRY-001",
            owners.CompanyOwnerId,
            lines));

        // ReserveAsync may return an existing company reservation when
        // the resolver retries the same order reference against the agency.
        var reservations = new ReservationServiceStub(_ => existing);
        var decisionLogger = new RecordingFulfilmentDecisionLogger();
        var resolver = new FulfilmentResolver(
            owners, reservations, new TenantStub(), decisionLogger);

        var result = await resolver.ResolveFulfilmentSourceAsync(
            new ResolveFulfilmentRequest(
                "ORDER-RETRY-001", owners.AgencyId, lines));

        Assert.Same(existing, result);
        Assert.NotNull(result.Reservation);
        Assert.Equal(
            owners.CompanyOwnerId,
            result.Reservation.InventoryOwnerId);
        Assert.Single(reservations.Attempts);
        Assert.Equal(0, owners.CompanyLookups);

        var decision = Assert.Single(decisionLogger.Entries);
        Assert.Equal("ReservationResolved", decision.Decision);
        Assert.Equal("ORDER-RETRY-001", decision.OrderReference);
        Assert.Equal(owners.AgencyId, decision.AgencyId);
        Assert.Equal(owners.CompanyOwnerId, decision.InventoryOwnerId);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    [Theory]
    [InlineData("missing-order")]
    [InlineData("missing-agency")]
    [InlineData("empty-lines")]
    [InlineData("null-lines")]
    [InlineData("null-line")]
    [InlineData("missing-product")]
    [InlineData("zero-quantity")]
    [InlineData("negative-quantity")]
    [InlineData("quantity-overflow")]
    public async Task Resolve_rejects_invalid_input_without_reserving(
    string scenario)
    {
        var owners = new OwnerLookupStub();
        var productId = Guid.NewGuid();

        var request = new ResolveFulfilmentRequest(
            "ORDER-VALIDATION-001",
            owners.AgencyId,
            new[]
            {
            new ReservationLineRequest(productId, null, 1)
            });

        request = scenario switch
        {
            "missing-order" => request with
            {
                OrderReference = " "
            },
            "missing-agency" => request with
            {
                AgencyId = Guid.Empty
            },
            "empty-lines" => request with
            {
                Lines = Array.Empty<ReservationLineRequest>()
            },
            "null-lines" => request with
            {
                Lines = null!
            },
            "null-line" => request with
            {
                Lines = new ReservationLineRequest[] { null! }
            },
            "missing-product" => request with
            {
                Lines = new[]
                {
                new ReservationLineRequest(Guid.Empty, null, 1)
            }
            },
            "zero-quantity" => request with
            {
                Lines = new[]
                {
                new ReservationLineRequest(productId, null, 0)
            }
            },
            "negative-quantity" => request with
            {
                Lines = new[]
                {
                new ReservationLineRequest(productId, null, -1)
            }
            },
            "quantity-overflow" => request with
            {
                Lines = new[]
                {
                new ReservationLineRequest(productId, null, int.MaxValue),
                new ReservationLineRequest(productId, null, 1)
            }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        var reservations = new ReservationServiceStub(_ =>
            throw new InvalidOperationException(
                "Invalid input must not reach the reservation service."));

        var decisionLogger = new RecordingFulfilmentDecisionLogger();
        var resolver = new FulfilmentResolver(
            owners, reservations, new TenantStub(), decisionLogger);

        var result = await resolver.ResolveFulfilmentSourceAsync(request);

        Assert.Equal(ReservationOutcome.InvalidRequest, result.Outcome);
        Assert.Null(result.Reservation);
        Assert.Empty(reservations.Attempts);
        Assert.Equal(0, owners.CompanyLookups);

        var decision = Assert.Single(decisionLogger.Entries);
        Assert.Equal("Rejected", decision.Decision);
        Assert.Contains("InvalidRequest", decision.Reason);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
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