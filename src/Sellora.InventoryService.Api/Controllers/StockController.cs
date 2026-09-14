using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sellora.InventoryService.Api.Authorization;
using Sellora.InventoryService.Api.Contracts;
using Sellora.InventoryService.Application.Identity;
using Sellora.InventoryService.Application.Stock;
using Sellora.InventoryService.Domain.Tenancy;

namespace Sellora.InventoryService.Api.Controllers;

[ApiController]
[Route("api/stock")]
public sealed class StockController : ControllerBase
{
    private readonly IStockAdjustmentService _stockAdjustmentService;
    private readonly IStockReadService _stockReadService;
    private readonly ITenantContext _tenantContext;
    private readonly IStockReservationService _stockReservationService;

    public StockController(
        IStockAdjustmentService stockAdjustmentService,
        IStockReadService stockReadService,
        ITenantContext tenantContext,
        IStockReservationService stockReservationService)
    {
        _stockAdjustmentService = stockAdjustmentService;
        _stockReadService = stockReadService;
        _tenantContext = tenantContext;
        _stockReservationService = stockReservationService;
    }

    [HttpGet]
    [Authorize(Policy = RolePolicies.RequireStockRead)]
    public async Task<ActionResult<IReadOnlyCollection<StockItemResponse>>> GetStock(
        [FromQuery] Guid? productId,
        [FromQuery] Guid? inventoryOwnerId,
        CancellationToken cancellationToken)
    {
        if (_tenantContext.CompanyId is null)
        {
            return Unauthorized(new
            {
                Message = "A valid company identifier was not found in the access token."
            });
        }

        var stock = await _stockReadService.GetStockAsync(
            new StockListQuery(productId, inventoryOwnerId),
            cancellationToken);

        return Ok(stock);
    }

    [HttpPost("adjustments")]
    [Authorize(Policy = RolePolicies.RequireStockAdjustment)]
    public async Task<ActionResult<StockAdjustmentResponse>> Adjust(
        AdjustStockRequestBody body,
        CancellationToken cancellationToken)
    {
        var request = new AdjustStockRequest(
            body.InventoryOwnerId,
            body.ProductId,
            body.BatchId,
            body.QuantityDelta,
            body.Reason ?? string.Empty);

        var result = await _stockAdjustmentService.AdjustAsync(
            request,
            cancellationToken);

        return result.Outcome switch
        {
            AdjustStockOutcome.Success =>
                Ok(result.Adjustment),

            AdjustStockOutcome.InvalidRequest =>
                BadRequest(new { result.Message }),

            AdjustStockOutcome.TenantNotAvailable =>
                Unauthorized(new { result.Message }),

            AdjustStockOutcome.CallerNotAuthorized =>
                Forbid(),

            AdjustStockOutcome.InventoryOwnerNotFound =>
                NotFound(new { result.Message }),

            AdjustStockOutcome.InsufficientStock =>
                Conflict(new { result.Message }),

            AdjustStockOutcome.ConcurrencyConflict =>
                Conflict(new { result.Message }),

            _ => Problem(
                title: "Stock adjustment failed.",
                statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    [HttpPost("availability")]
    [Authorize(Policy = RolePolicies.RequireStockReservation)]
    public async Task<ActionResult<IReadOnlyCollection<StockAvailability>>>
    CheckAvailability(
        ReserveStockRequestBody body,
        CancellationToken cancellationToken)
    {
        var availability = await _stockReservationService
            .CheckAvailabilityAsync(
                new CheckAvailabilityRequest(
                    body.InventoryOwnerId,
                    body.Lines
                        .Select(line => new ReservationLineRequest(
                            line.ProductId,
                            line.BatchId,
                            line.Quantity))
                        .ToList()),
                cancellationToken);

        return Ok(availability);
    }

    [HttpPost("reservations")]
    [Authorize(Policy = RolePolicies.RequireStockReservation)]
    public async Task<ActionResult<StockReservationResponse>> Reserve(
        ReserveStockRequestBody body,
        CancellationToken cancellationToken)
    {
        var result = await _stockReservationService.ReserveAsync(
            new ReserveStockRequest(
                body.OrderReference ?? string.Empty,
                body.InventoryOwnerId,
                body.Lines
                    .Select(line => new ReservationLineRequest(
                        line.ProductId,
                        line.BatchId,
                        line.Quantity))
                    .ToList()),
            cancellationToken);

        return ReservationResult(result);
    }

    [HttpPost("reservations/{reservationId:guid}/release")]
    [Authorize(Policy = RolePolicies.RequireStockReservation)]
    public async Task<ActionResult<StockReservationResponse>> Release(
        Guid reservationId,
        CancellationToken cancellationToken)
    {
        var result = await _stockReservationService.ReleaseAsync(
            reservationId,
            cancellationToken);

        return ReservationResult(result);
    }

    [HttpPost("reservations/{reservationId:guid}/confirm")]
    [Authorize(Policy = RolePolicies.RequireStockReservation)]
    public async Task<ActionResult<StockReservationResponse>> Confirm(
        Guid reservationId,
        CancellationToken cancellationToken)
    {
        var result = await _stockReservationService.ConfirmAsync(
            reservationId,
            cancellationToken);

        return ReservationResult(result);
    }

    [HttpPost("fulfilment/resolve")]
    [Authorize(Policy = RolePolicies.RequireStockReservation)]
    public async Task<ActionResult<StockReservationResponse>> ResolveFulfilment(
    [FromBody] ResolveFulfilmentRequestBody body,
    [FromServices] IFulfilmentResolver resolver,
    [FromServices] ICurrentUserContext currentUser,
    CancellationToken cancellationToken)
    {
        if (_tenantContext.CompanyId is not Guid companyId ||
            companyId == Guid.Empty)
        {
            return Unauthorized(new
            {
                Message = "A valid company identifier was not found in the access token."
            });
        }

        if (body.AgencyId == Guid.Empty ||
            body.Lines is null ||
            body.Lines.Count == 0 ||
            body.Lines.Any(line => line is null))
        {
            return BadRequest(new
            {
                Message = "A valid agency and stock lines are required."
            });
        }

        var isCompanyAdmin =
            User.IsInRole("CompanyAdmin") ||
            User.HasClaim("roles", "CompanyAdmin");

        if (!isCompanyAdmin && currentUser.AgencyId != body.AgencyId)
        {
            return Forbid();
        }

        var result = await resolver.ResolveFulfilmentSourceAsync(
            new ResolveFulfilmentRequest(
                body.OrderReference,
                body.AgencyId,
                body.Lines
                    .Select(line => new ReservationLineRequest(
                        line.ProductId,
                        line.BatchId,
                        line.Quantity))
                    .ToArray()),
            cancellationToken);

        return ReservationResult(result);
    }

    private ActionResult<StockReservationResponse> ReservationResult(
        ReserveStockResult result) =>
        result.Outcome switch
        {
            ReservationOutcome.Success =>
                Ok(result.Reservation),

            ReservationOutcome.InvalidRequest =>
                BadRequest(new { result.Message }),

            ReservationOutcome.TenantNotAvailable =>
                Unauthorized(new { result.Message }),

            ReservationOutcome.InventoryOwnerNotFound or
            ReservationOutcome.ReservationNotFound =>
                NotFound(new { result.Message }),

            ReservationOutcome.InsufficientStock =>
                Conflict(new
                {
                    result.Message,
                    result.Shortages
                }),

            ReservationOutcome.ReservationAlreadyReleased or
            ReservationOutcome.ReservationAlreadyConfirmed =>
                Conflict(new { result.Message }),

            _ => Problem(
                title: "Stock reservation failed.",
                statusCode: StatusCodes.Status500InternalServerError)
        };
}
