# US-E4-6 — Van stock transfer on `VanStockReturned`

sellora-order publishes `VanStockReturned` on the **order topic** when an agency operator accepts a rep's end-of-route van return (keyed by the return reference, `VR-…`). `OrderEventHandler` moves the stock in one transaction, deduplicated on `eventId` like every other consumed event.

## What the handler does
1. **Checks the owners.** `vanInventoryOwnerId` must be a `SalesRep` owner whose `externalOwnerId` is the event's `salesRepId`; the agency's owner is the `Agency` owner with `externalOwnerId = agencyId`. Both rows are locked (`FOR UPDATE`) so concurrent transfers serialise and missing stock rows are created once.
2. **Debits the van, oldest batch first** (the order reservations use), with a conditional `UPDATE … WHERE quantity_on_hand - quantity_reserved >= n`. Stock held for an unfinished cash sale is never returned.
3. **Credits the agency in the same batch**, creating the stock row if the agency has none, and re-arms the low-stock alert if it is back above threshold.
4. Writes a `Transferred` movement on each side: `referenceType = VanReturn`, `referenceId = VR-…`, `actorId = sales-rep:{id}`; the two deltas sum to zero.

Only `lines[].acceptedQuantity` moves; declared quantity and variance ride along for other consumers and are ignored here. A line with 0 moves nothing.

## Failures
| Case | Result |
|---|---|
| Van holds less than accepted | `InvalidInventoryEventException` → dead-letter; nothing moves. Order re-checks the van at acceptance, so this means stock changed in between. |
| Van owner is not the rep's / agency has no owner | Dead-letter; nothing moves. |
| A reservation took van stock between the read and the update | Transient: the transaction rolls back and the consumer retries from the committed offset. |
| Same event again | No-op (processed-event receipt). |

No migration: `Transferred` is already an allowed `movement_type`.
