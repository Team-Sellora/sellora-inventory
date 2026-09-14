using Sellora.InventoryService.Domain.Tenancy;

namespace Sellora.InventoryService.Application.Stock;

public sealed class FulfilmentResolver : IFulfilmentResolver
{
    private readonly IFulfilmentOwnerLookup _ownerLookup;
    private readonly IStockReservationService _reservationService;
    private readonly ITenantContext _tenantContext;

    public FulfilmentResolver(
        IFulfilmentOwnerLookup ownerLookup,
        IStockReservationService reservationService,
        ITenantContext tenantContext)
    {
        _ownerLookup = ownerLookup;
        _reservationService = reservationService;
        _tenantContext = tenantContext;
    }

    public async Task<ReserveStockResult> ResolveFulfilmentSourceAsync(
        ResolveFulfilmentRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_tenantContext.CompanyId is not Guid companyId ||
            companyId == Guid.Empty)
        {
            return ReserveStockResult.Failure(
                ReservationOutcome.TenantNotAvailable,
                "A company identifier is required.");
        }

        if (request is null ||
            string.IsNullOrWhiteSpace(request.OrderReference) ||
            request.AgencyId == Guid.Empty ||
            request.Lines is null ||
            request.Lines.Count == 0)
        {
            return ReserveStockResult.Failure(
                ReservationOutcome.InvalidRequest,
                "Order reference, agency and stock lines are required.");
        }

        var lines = request.Lines.ToArray();

        if (lines.Any(line =>
            line is null ||
            line.ProductId == Guid.Empty ||
            line.Quantity <= 0))
        {
            return ReserveStockResult.Failure(
                ReservationOutcome.InvalidRequest,
                "Every stock line requires a product and positive quantity.");
        }

        if (lines
            .GroupBy(line => new { line.ProductId, line.BatchId })
            .Any(group =>
                group.Sum(line => (long)line.Quantity) > int.MaxValue))
        {
            return ReserveStockResult.Failure(
                ReservationOutcome.InvalidRequest,
                "The combined quantity for a stock line is too large.");
        }

        var agencyOwnerId = await _ownerLookup.FindAgencyOwnerAsync(
            request.AgencyId,
            cancellationToken);

        if (agencyOwnerId is null)
        {
            return ReserveStockResult.Failure(
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
            return agencyResult;
        }

        var companyOwnerId = await _ownerLookup.FindCompanyOwnerAsync(
            cancellationToken);

        if (companyOwnerId is null)
        {
            return ReserveStockResult.Failure(
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
            return ReserveStockResult.Failure(
                ReservationOutcome.InsufficientStock,
                "Neither the agency nor the company can fulfil the entire " +
                "order. Reported shortages are for company stock.",
                companyResult.Shortages);
        }

        return companyResult;
    }
}