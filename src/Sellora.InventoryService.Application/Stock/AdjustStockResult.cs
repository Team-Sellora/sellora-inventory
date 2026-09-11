namespace Sellora.InventoryService.Application.Stock;

public enum AdjustStockOutcome
{
    Success,
    InvalidRequest,
    TenantNotAvailable,
    CallerNotAuthorized,
    InventoryOwnerNotFound,
    ProductNotFound,
    InsufficientStock
}

public sealed class AdjustStockResult
{
    public AdjustStockOutcome Outcome { get; }

    public string Message { get; }

    public StockAdjustmentResponse? Adjustment { get; }

    private AdjustStockResult(
        AdjustStockOutcome outcome,
        string message,
        StockAdjustmentResponse? adjustment = null)
    {
        Outcome = outcome;
        Message = message;
        Adjustment = adjustment;
    }

    public static AdjustStockResult Success(
        StockAdjustmentResponse adjustment) =>
        new(
            AdjustStockOutcome.Success,
            "Stock adjusted successfully.",
            adjustment);

    public static AdjustStockResult InvalidRequest(string message) =>
        new(AdjustStockOutcome.InvalidRequest, message);

    public static AdjustStockResult TenantNotAvailable() =>
        new(
            AdjustStockOutcome.TenantNotAvailable,
            "A valid company identifier was not found in the access token.");

    public static AdjustStockResult CallerNotAuthorized() =>
        new(
            AdjustStockOutcome.CallerNotAuthorized,
            "You are not authorised to adjust stock for this owner.");

    public static AdjustStockResult InventoryOwnerNotFound(
        Guid inventoryOwnerId) =>
        new(
            AdjustStockOutcome.InventoryOwnerNotFound,
            $"Inventory owner '{inventoryOwnerId}' was not found.");

    public static AdjustStockResult ProductNotFound(Guid productId) =>
        new(
            AdjustStockOutcome.ProductNotFound,
            $"Product '{productId}' was not found.");

    public static AdjustStockResult InsufficientStock() =>
        new(
            AdjustStockOutcome.InsufficientStock,
            "The adjustment would make stock on-hand negative.");
}