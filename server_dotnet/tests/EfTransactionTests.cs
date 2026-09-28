using Dapper;
using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Tests;

[Collection(ConnectionLifecycleCollectionDefinition.Name)]
public sealed class EfTransactionTests
{
    private static readonly int[] PageSource = [1, 2, 3];
    private static readonly int[] SecondPage = [2, 3];

    [Fact(Timeout = 120_000)]
    public async Task BorrowedContextSharesWritesAndRollbackWithoutClosingTheOwnersConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("ef_transaction", ct);
        await database.InitializeBusinessFixtureAsync(ct);
        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await using var connection = await database.Database.OpenAsync(ct);
        await using var transaction = await AppDb.BeginTransactionAsync(connection, ct);
        await using (var context = EfDb.Use(connection, transaction))
        {
            context.Departments.Add(new Department { Name = "EF事务测试", Kind = "SECTION", Status = "ACTIVE" });
            await context.SaveChangesAsync(ct);
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM departments WHERE name='EF事务测试'", transaction: transaction));
        }
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
        await using (var context = EfDb.Use(connection, transaction))
        {
            Assert.True(await context.Departments.AnyAsync(d => d.Name == "EF事务测试", ct));
        }
        await transaction.RollbackAsync(ct);
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM departments WHERE name='EF事务测试'"));
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }

    [Fact]
    public void HugePageOffsetDoesNotOverflowIntoTheFirstPage()
    {
        var source = PageSource.AsQueryable();
        Assert.Empty(source.Page((ulong)int.MaxValue + 1, 20));
        Assert.Empty(source.Page(ulong.MaxValue, 20));
        Assert.Equal(SecondPage, source.Page(1, 2));
    }

    [Fact(Timeout = 120_000)]
    public async Task ProjectProjectionPreservesNullableMetadataAndPendingSubmissionIdentity()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("ef_projection", ct);
        await database.InitializeBusinessFixtureAsync(ct);
        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await using var connection = await database.Database.OpenAsync(ct);
        await using var transaction = await AppDb.BeginTransactionAsync(connection, ct);
        await using var context = EfDb.Use(connection, transaction);
        var supplier = new Supplier { Name = "EF投影供应商", Status = "ACTIVE" };
        var user = new User { EmployeeNo = "ef_projection", PasswordHash = "unused", RealName = "投影测试", Email = "ef@example.invalid", UserType = "INTERNAL", Status = "ACTIVE" };
        context.AddRange(supplier, user);
        await context.SaveChangesAsync(ct);
        var group = new ProjectGroup { Name = "EF投影主项目", SupplierId = supplier.Id, CreatedBy = user.Id, Status = "IN_PROGRESS" };
        context.Add(group);
        await context.SaveChangesAsync(ct);
        var project = new Project { Name = "EF投影子项目", ProjectGroupId = group.Id, SupplierId = supplier.Id, CreatedBy = user.Id,
            Status = "PENDING_CONFIRMATION", ConfirmSide = "COMPANY", ExpectedCompletionDate = new DateOnly(2030, 1, 2) };
        context.Add(project);
        await context.SaveChangesAsync(ct);
        var submit = new ProjectStatusLog { ProjectId = project.Id, OperatorId = user.Id, Action = "SUBMIT", ToStatus = "PENDING_CONFIRMATION" };
        context.Add(submit);
        await context.SaveChangesAsync(ct);
        var row = await ProjectQueries.Rows(context).SingleAsync(p => p.Id == project.Id, ct);
        Assert.Equal(group.Name, row.ProjectGroupName);
        Assert.Equal(supplier.Name, row.SupplierName);
        Assert.Equal(user.RealName, row.CreatedByName);
        Assert.Equal(new DateTime(2030, 1, 2), row.ExpectedCompletionDate);
        Assert.Equal(submit.Id, row.LatestSubmissionId);
        Assert.Null(row.ResponsibleUserName);
        Assert.False(row.HasCopyHistory);
        await context.Projects.Where(p => p.Id == project.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, "IN_PROGRESS").SetProperty(p => p.ExpectedCompletionDate, (DateOnly?)null), ct);
        row = await ProjectQueries.Rows(context).SingleAsync(p => p.Id == project.Id, ct);
        Assert.Null(row.LatestSubmissionId);
        Assert.Null(row.ExpectedCompletionDate);
        await transaction.RollbackAsync(ct);
    }
}
