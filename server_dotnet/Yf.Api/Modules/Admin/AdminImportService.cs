using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;
using Yf.Api.Infrastructure.Entities;
using Yf.Api.Modules.Identity;

namespace Yf.Api.Modules.Admin;

/// <summary>Row-isolated, create-only imports for internal and Robot-supplier accounts.</summary>
public sealed class AdminImportService(
    IDbContextFactory<YfDbContext> dbFactory,
    UserService users,
    SupplierService suppliers)
{
    internal const int MaximumAccountRows = 100;

    public async Task<AdminImportResponse> ImportInternalUsersAsync(
        CurrentUser actor, InternalUserImportRequest request, CancellationToken ct)
    {
        var rows = RequireRows(request.Rows);
        // This read checks permission before the duplicate lookup can reveal whether an account exists.
        await users.RoleOptionsAsync(actor, null, ct);
        var existing = await ExistingEmployeeNumbersAsync(rows.Select(row => row.EmployeeNo), ct);
        var duplicateRows = DuplicateRowIndexes(rows.Select(row => row.EmployeeNo));
        var results = new List<AdminImportRowResult>(rows.Length);
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            var rowNumber = RowNumber(row.RowNumber, index);
            var employeeNo = row.EmployeeNo?.Trim() ?? "";
            if (duplicateRows.Contains(index))
            {
                results.Add(new(rowNumber, employeeNo, "SKIPPED", "文件内账号重复，本重复组未导入"));
                continue;
            }
            if (existing.Contains(NormalizeKey(employeeNo)))
            {
                results.Add(new(rowNumber, employeeNo, "SKIPPED", "账号已存在，未覆盖现有资料"));
                continue;
            }

            try
            {
                await users.CreateAsync(actor, new UserCreate(
                    employeeNo, row.InitialPassword ?? "", row.RealName ?? "", row.Email ?? "",
                    row.DepartmentId, row.RoleId, null), ct);
                results.Add(new(rowNumber, employeeNo, "CREATED", null));
            }
            catch (ApiException error) when (error.Status is 400 or 409)
            {
                var status = error.Status == 409 && error.Message == "账号已存在" ? "SKIPPED" : "FAILED";
                results.Add(new(rowNumber, employeeNo, status, error.Message));
            }
            catch (ApiException error) when (error.Status == 403)
            {
                results.Add(new(rowNumber, employeeNo, "FAILED", "权限已变化，批次已停止"));
                AddNotRun(results, rows.Select((row, rowIndex) =>
                    (RowNumber(row.RowNumber, rowIndex), row.EmployeeNo)).ToArray(), index + 1);
                break;
            }
        }
        return Summarize(results);
    }

    public async Task<AdminImportResponse> ImportSupplierAccountsAsync(
        CurrentUser actor, ulong supplierId, SupplierAccountImportRequest request, CancellationToken ct)
    {
        var rows = RequireRows(request.Rows);
        // Role options applies the supplier-account delegation ceiling and permission gate before lookup.
        await suppliers.RoleOptionsAsync(actor, null, ct);
        var existing = await ExistingEmployeeNumbersAsync(rows.Select(row => row.EmployeeNo), ct);
        var duplicateRows = DuplicateRowIndexes(rows.Select(row => row.EmployeeNo));
        var results = new List<AdminImportRowResult>(rows.Length);
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            var rowNumber = RowNumber(row.RowNumber, index);
            var employeeNo = row.EmployeeNo?.Trim() ?? "";
            if (duplicateRows.Contains(index))
            {
                results.Add(new(rowNumber, employeeNo, "SKIPPED", "文件内账号重复，本重复组未导入"));
                continue;
            }
            if (existing.Contains(NormalizeKey(employeeNo)))
            {
                results.Add(new(rowNumber, employeeNo, "SKIPPED", "账号已存在，未覆盖现有资料"));
                continue;
            }

            try
            {
                await suppliers.CreateAccountAsync(actor, supplierId, new SupplierAccountCreate(
                    employeeNo, row.InitialPassword ?? "", row.RoleId), ct);
                results.Add(new(rowNumber, employeeNo, "CREATED", null));
            }
            catch (ApiException error) when (error.Status is 400 or 409)
            {
                var status = error.Status == 409 && error.Message == "账号已存在" ? "SKIPPED" : "FAILED";
                results.Add(new(rowNumber, employeeNo, status, error.Message));
            }
            catch (ApiException error) when (error.Status == 403)
            {
                results.Add(new(rowNumber, employeeNo, "FAILED", "权限已变化，批次已停止"));
                AddNotRun(results, rows.Select((row, rowIndex) =>
                    (RowNumber(row.RowNumber, rowIndex), row.EmployeeNo)).ToArray(), index + 1);
                break;
            }
        }
        return Summarize(results);
    }

    private async Task<HashSet<string>> ExistingEmployeeNumbersAsync(IEnumerable<string> values, CancellationToken ct)
    {
        var keys = values.Select(NormalizeKey).Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (keys.Length == 0) return new(StringComparer.OrdinalIgnoreCase);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        var existing = await context.Users.AsNoTracking().Where(user => Enumerable.Contains(keys, user.EmployeeNo))
            .Select(user => user.EmployeeNo).ToArrayAsync(ct);
        return existing.Select(NormalizeKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static T[] RequireRows<T>(T[]? rows)
    {
        if (rows is null || rows.Length is < 1 or > MaximumAccountRows || rows.Any(row => row is null))
            throw ApiException.BadRequest($"导入行数需为 1–{MaximumAccountRows} 行");
        return rows;
    }

    private static HashSet<int> DuplicateRowIndexes(IEnumerable<string> values)
    {
        var indexed = values.Select(NormalizeKey).Select((key, index) => (key, index)).Where(item => item.key.Length > 0);
        return indexed.GroupBy(item => item.key, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).SelectMany(group => group.Select(item => item.index)).ToHashSet();
    }

    private static string NormalizeKey(string? value) => (value ?? "").Trim().ToUpperInvariant();

    private static int RowNumber(int supplied, int index) => supplied is >= 2 and <= 101 ? supplied : index + 2;

    private static void AddNotRun(List<AdminImportRowResult> results, IReadOnlyList<(int RowNumber, string Key)> rows, int start)
    {
        for (var index = start; index < rows.Count; index++)
            results.Add(new(rows[index].RowNumber, rows[index].Key?.Trim() ?? "", "FAILED", "未执行"));
    }

    private static AdminImportResponse Summarize(IReadOnlyList<AdminImportRowResult> results) => new(
        results.Count(row => row.Status == "CREATED"),
        results.Count(row => row.Status == "SKIPPED"),
        results.Count(row => row.Status == "FAILED"), results);
}
