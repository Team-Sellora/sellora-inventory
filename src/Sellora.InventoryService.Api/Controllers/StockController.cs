using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sellora.InventoryService.Api.Authorization;
using Sellora.InventoryService.Api.Contracts;
using Sellora.InventoryService.Application.Stock;

namespace Sellora.InventoryService.Api.Controllers;

[ApiController]
[Route("api/stock")]
public sealed class StockController : ControllerBase
{
    private readonly IStockAdjustmentService _stockAdjustmentService;

    public StockController(
        IStockAdjustmentService stockAdjustmentService)
    {
        _stockAdjustmentService = stockAdjustmentService;
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

            _ => Problem(
                title: "Stock adjustment failed.",
                statusCode: StatusCodes.Status500InternalServerError)
        };
    }
}