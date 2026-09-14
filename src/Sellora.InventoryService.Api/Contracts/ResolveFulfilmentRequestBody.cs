using System.ComponentModel.DataAnnotations;

namespace Sellora.InventoryService.Api.Contracts;

public sealed class ResolveFulfilmentRequestBody
{
    [Required]
    public string OrderReference { get; init; } = string.Empty;

    public Guid AgencyId { get; init; }

    [Required]
    [MinLength(1)]
    public IReadOnlyCollection<ReservationLineRequestBody> Lines { get; init; } =
        Array.Empty<ReservationLineRequestBody>();
}