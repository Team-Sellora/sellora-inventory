# US-E3-4 — Inventory event processing

Implementation scope: T1–T5. T6 integration/acceptance testing remains with QA.

## Subtasks and files

| Subtask | Implementation |
| --- | --- |
| T1 | `OrderEventHandler` confirms the tenant-scoped reservation matching the order reference. `StockReservationService` locks the reservation before checking status and writes `Sold` while reducing on-hand and reserved. |
| T2 | `OrderCancelledEvent` releases an active reservation and writes `Released`; on-hand is unchanged. **Changed by US-E4-5:** a *confirmed* reservation is now returned to on-hand (`Returned` movement, reservation → `Released`) instead of being dead-lettered — see below. |
| T3 | `ReturnAcceptedEvent` restores the named owner's product/batch quantities and writes `Returned` with the return reference. Missing stock rows are created; unknown owners are rejected. |
| T4 | `processed_inventory_event` uses `(company_id, event_id)` as a unique message identity. The insert, stock changes, ledger and reservation transition share one transaction. Duplicate payloads are no-ops; reusing an event ID with different contents is rejected. |
| T5 | `OrderEventConsumerService` routes permanent errors to the configured dead-letter topic. It commits the input offset only after processing or acknowledged dead-letter publication. Temporary infrastructure failures rejoin from committed offsets. |

## Producer contract

These are local contract stubs for E4/E6, which are not present in this workspace.
Agree on these payloads with the Order and Delivery producers before end-to-end rollout.
Use `schemaVersion: "1.0"`, matching the existing hierarchy event convention.

`eventId` must remain stable across redeliveries. The Kafka key may be an aggregate
key; deduplication uses the envelope event ID, not the aggregate key. A different
event ID represents a different event. Producers must not issue new IDs for retries.
Company and reservation/owner identifiers must be valid UUIDs from the same tenant.

Order topic (default `sellora.order.v1`):

```json
{
  "eventId": "11111111-1111-1111-1111-111111111111",
  "eventType": "OrderConfirmed",
  "schemaVersion": "1.0",
  "companyId": "22222222-2222-2222-2222-222222222222",
  "entityId": "33333333-3333-3333-3333-333333333333",
  "reservationId": "44444444-4444-4444-4444-444444444444",
  "orderReference": "ORDER-001",
  "confirmedAt": "2026-09-14T10:00:00Z",
  "correlationId": "order-001"
}
```

For cancellation use `eventType: "OrderCancelled"`, a new stable event ID and
`cancelledAt` instead of `confirmedAt`, with the same reservation/order reference.

Delivery topic (default `sellora.delivery.v1`):

```json
{
  "eventId": "55555555-5555-5555-5555-555555555555",
  "eventType": "ReturnAccepted",
  "schemaVersion": "1.0",
  "companyId": "22222222-2222-2222-2222-222222222222",
  "entityId": "66666666-6666-6666-6666-666666666666",
  "inventoryOwnerId": "77777777-7777-7777-7777-777777777777",
  "returnReference": "RETURN-001",
  "lines": [
    {
      "productId": "88888888-8888-8888-8888-888888888888",
      "batchId": null,
      "quantity": 5
    }
  ],
  "acceptedAt": "2026-09-14T11:00:00Z",
  "correlationId": "return-001"
}
```

Movement timestamps record processing time. Producer timestamps are preserved in
the event contract. Returns accept positive integer quantities only; duplicate
product/batch lines are combined. A return changes on-hand only. Return eligibility
and physical acceptance belong to the upstream Delivery service.

## Configuration and operation

The API registers one hosted consumer for both input topics outside the Testing
environment. Configure `Kafka:BootstrapServers`, `OrderTopic`, `DeliveryTopic`,
`OrderConsumerGroupId` and `DeadLetterTopic`. Provision the topics and grant the
service read permissions on inputs and write permission on the dead-letter topic.
Do not share its consumer group with the hierarchy consumer.

`AddProcessedInventoryEvents` adds the receipt table. The existing API startup
migration step applies it. Deploy the migration before starting event consumption.
Do not prune receipts while messages can still be replayed.

Dead-letter records contain the original payload/key/headers, source topic,
partition and offset, failure reason and time. Their key is topic:partition:offset.
Publication is at least once: a crash after publication but before input commit can
duplicate a dead-letter record. Deduplicate operational alerts using that key.

An error-level `InventoryEventDeadLettered` log is emitted on every acknowledged
dead-letter publication. Configure the deployment's log alerting to notify on this
signal (and on repeated processing interruptions). This repository does not contain
an external notification destination or deployed alert rule.

Unknown reservations/owners, mismatched references, conflicting transitions,
malformed payloads and unsupported schema versions are dead-lettered. Other event
types on the shared streams are ignored. A temporarily missing dependency can be
replayed from the dead-letter record after it is available, preserving the event ID.
Never advance the source offset when dead-letter publication fails.

## Verification boundary

Build and existing unit tests are developer checks, not T6 acceptance evidence.
QA still owns duplicate/concurrent delivery, confirm/cancel/expiry races, rollback,
five-unit return restoration, full-cycle reconciliation and real Kafka dead-letter
verification against PostgreSQL. No full-story acceptance sign-off is implied.

## US-E4-5 — cancelling an order whose stock is already confirmed

Order now holds scheduled deliveries for agency approval, and commits their
stock at placement (Inventory's 15-minute hold is far shorter than an agency
takes to decide). A rejection, or a shop cancellation inside the one-hour
window, therefore arrives as `OrderCancelled` for a **confirmed** reservation.
Before this change that event was dead-lettered and the stock stayed sold.

`StockReservationService.CancelForOrderAsync` (used only by the
`OrderCancelled` handler):

| Reservation status | Result |
| --- | --- |
| `Active` | Ordinary release — `Released` movement, reserved goes down. |
| `Confirmed` | On-hand goes back up by each line's quantity, one `Returned` movement per line (`ReferenceType = Order`), reservation → `Released`, low-stock alert re-armed if the item is back above its threshold. |
| `Released` / `Expired` | `ReservationAlreadyReleased` — the handler treats it as already applied, so a replayed event changes nothing. |

Why returning confirmed stock is safe now: Order only publishes
`OrderCancelled` for an order whose goods never left the owner — a scheduled
delivery before delivery (cash sales that were paid and handed over cannot be
cancelled), or a cash sale whose payment was refused because the shop
cancelled at the same moment. Order is the source of truth for whether an
order stands. The HTTP `release` endpoint is unchanged and still only
releases held reservations.
