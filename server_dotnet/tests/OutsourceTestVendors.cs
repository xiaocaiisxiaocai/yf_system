using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;

namespace Yf.Api.Tests;

/// <summary>Outsourcing vendors are ODM/OEMA companies; tests add them (and their contacts) to the ODM/OEMA directory.</summary>
internal static class OutsourceTestVendors
{
    internal sealed record Vendor(ulong Id, string Name);

    public static async Task<Vendor> CreateAsync(IDbContextFactory<YfDbContext> factory, string name, CancellationToken ct,
        string status = "ACTIVE", params (string Name, string? Email, string Status)[] contacts)
    {
        await using var context = await factory.CreateDbContextAsync(ct);
        await context.Database.OpenConnectionAsync(ct);
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO oem_companies(name,status,created_at,updated_at) VALUES({name},{status},UTC_TIMESTAMP(3),UTC_TIMESTAMP(3))", ct);
        var id = await context.OemCompanies.Where(x => x.Name == name).Select(x => x.Id).SingleAsync(ct);
        var sort = 0;
        foreach (var contact in contacts)
            await AddContactAsync(context, id, contact.Name, contact.Email, contact.Status, ++sort, ct);
        return new(id, name);
    }

    public static async Task AddContactAsync(IDbContextFactory<YfDbContext> factory, ulong vendorId, string name, string? email,
        CancellationToken ct, string status = "ACTIVE", int sortNo = 0)
    {
        await using var context = await factory.CreateDbContextAsync(ct);
        await AddContactAsync(context, vendorId, name, email, status, sortNo, ct);
    }

    /// <summary>An address on the vendor's outsourcing mail list (外协邮箱), apart from its ODM/OEMA contacts.</summary>
    public static async Task AddOutsourceEmailAsync(IDbContextFactory<YfDbContext> factory, ulong vendorId, string email, CancellationToken ct)
    {
        await using var context = await factory.CreateDbContextAsync(ct);
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO outsource_vendor_emails(vendor_id,email,created_at,updated_at) VALUES({vendorId},{email},UTC_TIMESTAMP(3),UTC_TIMESTAMP(3))", ct);
    }

    private static Task<int> AddContactAsync(YfDbContext context, ulong vendorId, string name, string? email, string status, int sortNo,
        CancellationToken ct) =>
        context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO oem_company_contacts(oem_company_id,name,email,sort_no,status,created_at,updated_at) VALUES({vendorId},{name},{email},{sortNo},{status},UTC_TIMESTAMP(3),UTC_TIMESTAMP(3))", ct);
}
