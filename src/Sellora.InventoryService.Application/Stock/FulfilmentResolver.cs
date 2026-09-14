using Sellora.InventoryService.Domain.Tenancy;

namespace Sellora.InventoryService.Application.Stock;

public sealed class FulfilmentResolver : IFulfilmentResolver
{
    private readonly IFulfilmentOwnerLookup _ownerLookup;
    private readonly IStockReservationService _reservationService;
    private readonly ITenantContext _tenantContext;
    private readonly IFulfilmentDecisionLogger _decisionLogger;

    public FulfilmentResolver(
        IFulfilmentOwnerLookup ownerLookup,
        IStockReservationService reservationService,
        ITenantContext tenantContext,
        IFulfilmentDecisionLogger decisionLogger)
    {
        _ownerLookup = ownerLookup;
        _reservationService = reservationService;
        _tenantContext = tenantContext;
        _decisionLogger = decisionLogger;
    }

    public async Task<ReserveStockResult> ResolveFulfilmentSourceAsync(
        ResolveFulfilmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ReserveStockResult Finish(ReserveStockResult result)
        {
            var succeeded = result.Outcome == ReservationOutcome.Success;

            _decisionLogger.LogDecision(
                request?.OrderReference?.Trim() ?? string.Empty,
                request?.AgencyId ?? Guid.Empty,
                result.Reservation?.InventoryOwnerId,
                succeeded ? "ReservationResolved" : "Rejected",
                succeeded
                    ? "Reservation service returned the recorded owner for this order."
                    : $"{result.Outcome}: {result.Message}");

            return result;
        }

        ReserveStockResult Reject(
            ReservationOutcome outcome,
            string message,
            IReadOnlyCollection<ReservationShortage>? shortages = null) =>
            Finish(ReserveStockResult.Failure(outcome, message, shortages));

        if (_tenantContext.CompanyId is not Guid companyId ||
            companyId == Guid.Empty)
        {
            return Reject(
                ReservationOutcome.TenantNotAvailable,
                "A company identifier is required.");
        }

        if (request is null ||
            string.IsNullOrWhiteSpace(request.OrderReference) ||
            request.AgencyId == Guid.Empty ||
            request.Lines is null ||
            request.Lines.Count == 0)
        {
            return Reject(
                ReservationOutcome.InvalidRequest,
                "Order reference, agency and stock lines are required.");
        }

        var lines = request.Lines.ToArray();

        if (lines.Any(line =>
            line is null ||
            line.ProductId == Guid.Empty ||
            line.Quantity <= 0))
        {
            return Reject(
                ReservationOutcome.InvalidRequest,
                "Every stock line requires a product and positive quantity.");
        }

        if (lines
            .GroupBy(line => new { line.ProductId, line.BatchId })
            .Any(group =>
                group.Sum(line => (long)line.Quantity) > int.MaxValue))
        {
            return Reject(
                ReservationOutcome.InvalidRequest,
                "The combined quantity for a stock line is too large.");
        }

        var agencyOwnerId = await _ownerLookup.FindAgencyOwnerAsync(
            request.AgencyId,
            cancellationToken);

        if (agencyOwnerId is null)
        {
            return Reject(
                ReservationOutcome.InventoryOwnerNotFound,
                "The agency inventory owner was not found.");
        }

        var orderReference = request.OrderReference.Trim();

        var agencyResult = await _reservationService.ReserveAsync(
            new ReserveStockRequest(
                orderReference,
                agencyOwnerId.Value,
                lines),
            cancellationToken);

        // Only a stock shortage permits fallback to company stock.
        if (agencyResult.Outcome != ReservationOutcome.InsufficientStock)
        {
            return Finish(agencyResult);
        }

        _decisionLogger.LogDecision(
            orderReference,
            request.AgencyId,
            agencyOwnerId.Value,
            "CompanyFallback",
            "Agency stock cannot fulfil the entire order; trying the complete " +
            "basket against company stock."
        );

        var companyOwnerId = await _ownerLookup.FindCompanyOwnerAsync(
            cancellationToken);

        if (companyOwnerId is null)
        {
            return Reject(
                ReservationOutcome.InventoryOwnerNotFound,
                "The company inventory owner was not found.");
        }

        // Reserve the same complete basket against the company.
        var companyResult = await _reservationService.ReserveAsync(
            new ReserveStockRequest(
                orderReference,
                companyOwnerId.Value,
                lines),
            cancellationToken);

        if (companyResult.Outcome == ReservationOutcome.InsufficientStock)
        {
            return Reject(
                ReservationOutcome.InsufficientStock,
                "Neither the agency nor the company can fulfil the entire " +
                "order. Reported shortages are for company stock.",
                companyResult.Shortages);
        }

        return Finish(companyResult);
    }
}