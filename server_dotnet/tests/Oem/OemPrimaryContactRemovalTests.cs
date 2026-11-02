using Dapper;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Yf.Api.Tests.Oem;

public sealed class OemPrimaryContactRemovalTests
{
    [Fact(Timeout = 180_000)]
    public async Task MigrationDiscardsPrimaryContactWithoutCreatingContactRows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("remove_primary", ct);
        await using var context = await EfTestSupport.DbContextFactory(scope.Options).CreateDbContextAsync(ct);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20261101120851_AddOemCompanyContacts", ct);
        await using (var seedConnection = await scope.Database.OpenAsync(ct))
            await seedConnection.ExecuteAsync("INSERT INTO oem_companies(name,contact_name,contact_phone,contact_email,status,created_at,updated_at) VALUES('旧资料','旧主联系人','12345','old@example.invalid','ACTIVE',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3))");
        await migrator.MigrateAsync(cancellationToken: ct);
        await using var connection = await scope.Database.OpenAsync(ct);
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM oem_company_contacts"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='oem_companies' AND column_name IN ('contact_name','contact_phone','contact_email')"));
    }
}
