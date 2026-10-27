using Microsoft.EntityFrameworkCore;
using Sellora.InventoryService.Domain.Tenancy;
using Sellora.InventoryService.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Sellora.InventoryService.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgreSqlCollection
    : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "PostgreSQL integration tests";
}

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("inventory_tests")
            .WithUsername("sellora")
            .WithPassword("sellora_test_pw")
            .Build();

    private InventoryApiFactory? _api;

    public string ConnectionString => _container.GetConnectionString();

    /// <summary>One in-process API host shared by the HTTP tests.</summary>
    public InventoryApiFactory Api => _api ??= new InventoryApiFactory(ConnectionString);

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        await using var db = CreateContext(null);
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_api is not null)
        {
            await _api.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    public InventoryDbContext CreateContext(Guid? companyId)
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql(ConnectionString)
            .EnableSensitiveDataLogging()
            .Options;

        return new InventoryDbContext(
            options,
            new TestTenantContext(companyId));
    }

    private sealed class TestTenantContext : ITenantContext
    {
        public TestTenantContext(Guid? companyId)
        {
            CompanyId = companyId;
        }

        public Guid? CompanyId { get; }
    }
}