namespace Sellora.InventoryService.Application.Events;

// Permanent event errors go to the dead-letter topic instead of being retried.
public sealed class InvalidInventoryEventException(string message) : Exception(message);
