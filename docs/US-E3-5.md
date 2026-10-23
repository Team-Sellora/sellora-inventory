# US-E3-5 — Low-stock threshold decision

## T1 decision

The reorder threshold is defined **per stock item**, which makes it per
company, inventory owner, product, and batch where batches are tracked.

This matches the inventory model: each owner (company, agency, or sales rep)
can hold a different quantity of the same product and has an independent
replenishment need. A company-wide or product-only threshold would not express
that distinction.

`ReorderThreshold` is nullable. A null value disables low-stock detection for
that stock item. A configured threshold must be zero or greater.

Low stock means `available quantity < reorder threshold`. The event is raised
only on a downward crossing and is re-armed after available quantity rises
strictly above the threshold.
