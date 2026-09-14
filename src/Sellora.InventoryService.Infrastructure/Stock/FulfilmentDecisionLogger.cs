using Microsoft.Extensions.Logging;
using Sellora.InventoryService.Application.Stock;
using Sellora.InventoryService.Domain.Tenancy;

namespace Sellora.InventoryService.Infrastructure.Stock;

public sealed class FulfilmentDecisionLogger : IFulfilmentDecisionLogger
{
    private readonly ILogger<FulfilmentDecisionLogger> _logger;
    private readonly ITenantContext _tenantContext;

    public FulfilmentDecisionLogger(
        ILogger<FulfilmentDecisionLogger> logger,
        ITenantContext tenantContext)
    {
        _logger = logger;
        _tenantContext = tenantContext;
    }

    public void LogDecision(
        string orderReference,
        Guid agencyId,
        Guid? inventoryOwnerId,
        string decision,
        string reason)
    {
        _logger.LogInformation(
            "Fulfilment decision {Decision} for order {OrderReference}. " +
            "CompanyId={CompanyId}, AgencyId={AgencyId}, " +
            "InventoryOwnerId={InventoryOwnerId}. Reason={Reason}",
            decision,
            orderReference,
            _tenantContext.CompanyId,
            agencyId,
            inventoryOwnerId,
            reason);
    }
}