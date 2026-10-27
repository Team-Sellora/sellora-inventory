using Confluent.Kafka;

namespace Sellora.InventoryService.Infrastructure.Kafka;

/// <summary>
/// Managed brokers such as Confluent Cloud require SASL authentication.
/// A local broker does not, so the settings are applied only when a
/// username is configured.
/// </summary>
public static class KafkaSaslConfigurator
{
    public static void Apply(ClientConfig config, string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return;
        }

        config.SecurityProtocol = SecurityProtocol.SaslSsl;
        config.SaslMechanism = SaslMechanism.Plain;
        config.SaslUsername = username;
        config.SaslPassword = password;
    }
}
