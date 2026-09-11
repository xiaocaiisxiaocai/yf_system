using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Admin;

public static class AdminModule
{
    public static IServiceCollection AddAdminModule(this IServiceCollection services) => services
        .AddSingleton<DepartmentService>()
        .AddSingleton<UserService>()
        .AddSingleton<RoleService>()
        .AddSingleton<SupplierService>();

    public static IEndpointRouteBuilder MapAdminModule(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/departments", (DepartmentService s, CancellationToken ct) => s.ListAsync(ct));
        endpoints.MapGet("/api/v1/permissions", (RoleService s, CancellationToken ct) => s.PermissionsAsync(ct));
        var admin = endpoints.MapGroup("/api/v1/admin");

        admin.MapGet("/users", (HttpContext c, UserService s, CancellationToken ct) => { var p = QueryValues.Page(c.Request); ulong? dept = ulong.TryParse(c.Request.Query["departmentId"], out var d) ? d : null; return s.ListAsync(AccessService.GetCurrent(c), p.Page, p.Size, p.Offset, c.Request.Query["keyword"], dept, c.Request.Query["status"], ct); });
        admin.MapGet("/user-role-options", (HttpContext c, UserService s, CancellationToken ct) => s.RoleOptionsAsync(AccessService.GetCurrent(c), c.Request.Query["keyword"], ct));
        admin.MapPost("/users", (UserCreate r, HttpContext c, UserService s, CancellationToken ct) => s.CreateAsync(AccessService.GetCurrent(c), r, ct));
        admin.MapPut("/users/{id:long}", (ulong id, UserUpdate r, HttpContext c, UserService s, CancellationToken ct) => s.UpdateAsync(AccessService.GetCurrent(c), id, r, ct));
        admin.MapPut("/users/{id:long}/status", (ulong id, StatusRequest r, HttpContext c, UserService s, CancellationToken ct) => s.SetStatusAsync(AccessService.GetCurrent(c), id, r.Status, ct));
        admin.MapPut("/users/{id:long}/password", async (ulong id, PasswordRequest r, HttpContext c, UserService s, CancellationToken ct) => { await s.ResetPasswordAsync(AccessService.GetCurrent(c), id, r.NewPassword, ct); return Results.Json(new { }); });
        admin.MapPut("/users/{id:long}/roles", async (ulong id, RoleAssign r, HttpContext c, UserService s, CancellationToken ct) => { await s.AssignRoleAsync(AccessService.GetCurrent(c), id, r.RoleIds, ct); return Results.Json(new { }); });
        admin.MapDelete("/users/{id:long}", async (ulong id, HttpContext c, UserService s, CancellationToken ct) => { await s.DeleteAsync(AccessService.GetCurrent(c), id, ct); return Results.Json(new { }); });

        admin.MapPost("/departments", (DepartmentUpsert r, HttpContext c, DepartmentService s, CancellationToken ct) => s.CreateAsync(AccessService.GetCurrent(c), r, ct));
        admin.MapPut("/departments/{id:long}", (ulong id, DepartmentUpsert r, HttpContext c, DepartmentService s, CancellationToken ct) => s.UpdateAsync(AccessService.GetCurrent(c), id, r, ct));
        admin.MapPut("/departments/{id:long}/status", (ulong id, StatusRequest r, HttpContext c, DepartmentService s, CancellationToken ct) => s.SetStatusAsync(AccessService.GetCurrent(c), id, r.Status, ct));
        admin.MapDelete("/departments/{id:long}", async (ulong id, HttpContext c, DepartmentService s, CancellationToken ct) => { await s.DeleteAsync(AccessService.GetCurrent(c), id, ct); return Results.Json(new { }); });

        admin.MapGet("/roles", (HttpContext c, RoleService s, CancellationToken ct) => { var p = QueryValues.Page(c.Request); return s.ListAsync(AccessService.GetCurrent(c), p.Page, p.Size, p.Offset, ct); });
        admin.MapPost("/roles", (RoleUpsert r, HttpContext c, RoleService s, CancellationToken ct) => s.CreateAsync(AccessService.GetCurrent(c), r, ct));
        admin.MapPut("/roles/{id:long}", (ulong id, RoleUpsert r, HttpContext c, RoleService s, CancellationToken ct) => s.UpdateAsync(AccessService.GetCurrent(c), id, r, ct));
        admin.MapPut("/roles/{id:long}/status", (ulong id, StatusRequest r, HttpContext c, RoleService s, CancellationToken ct) => s.SetStatusAsync(AccessService.GetCurrent(c), id, r.Status, ct));
        admin.MapPut("/roles/{id:long}/permissions", async (ulong id, PermissionAssign r, HttpContext c, RoleService s, CancellationToken ct) => { await s.AssignPermissionsAsync(AccessService.GetCurrent(c), id, r.PermissionIds, ct); return Results.Json(new { }); });
        admin.MapDelete("/roles/{id:long}", async (ulong id, HttpContext c, RoleService s, CancellationToken ct) => { await s.DeleteAsync(AccessService.GetCurrent(c), id, ct); return Results.Json(new { }); });

        admin.MapGet("/suppliers", (HttpContext c, SupplierService s, CancellationToken ct) => { var p = QueryValues.Page(c.Request); return s.ListAsync(AccessService.GetCurrent(c), p.Page, p.Size, p.Offset, c.Request.Query["keyword"], c.Request.Query["status"], ct); });
        admin.MapPost("/suppliers", (SupplierUpsert r, HttpContext c, SupplierService s, CancellationToken ct) => s.CreateAsync(AccessService.GetCurrent(c), r, ct));
        admin.MapGet("/suppliers/{id:long}", (ulong id, HttpContext c, SupplierService s, CancellationToken ct) => s.DetailAsync(AccessService.GetCurrent(c), id, ct));
        admin.MapPut("/suppliers/{id:long}", (ulong id, SupplierUpsert r, HttpContext c, SupplierService s, CancellationToken ct) => s.UpdateAsync(AccessService.GetCurrent(c), id, r, ct));
        admin.MapPut("/suppliers/{id:long}/status", (ulong id, StatusRequest r, HttpContext c, SupplierService s, CancellationToken ct) => s.SetStatusAsync(AccessService.GetCurrent(c), id, r.Status, ct));
        admin.MapDelete("/suppliers/{id:long}", async (ulong id, HttpContext c, SupplierService s, CancellationToken ct) => { await s.DeleteAsync(AccessService.GetCurrent(c), id, ct); return Results.Json(new { }); });
        admin.MapGet("/suppliers/{id:long}/accounts", (ulong id, HttpContext c, SupplierService s, CancellationToken ct) => s.AccountsAsync(AccessService.GetCurrent(c), id, ct));
        admin.MapPost("/suppliers/{id:long}/accounts", (ulong id, SupplierAccountCreate r, HttpContext c, SupplierService s, CancellationToken ct) => s.CreateAccountAsync(AccessService.GetCurrent(c), id, r, ct));
        admin.MapPut("/supplier-accounts/{id:long}", (ulong id, SupplierAccountUpdate r, HttpContext c, SupplierService s, CancellationToken ct) => s.UpdateAccountAsync(AccessService.GetCurrent(c), id, r, ct));
        admin.MapPut("/supplier-accounts/{id:long}/status", (ulong id, StatusRequest r, HttpContext c, SupplierService s, CancellationToken ct) => s.SetAccountStatusAsync(AccessService.GetCurrent(c), id, r.Status, ct));
        admin.MapPut("/supplier-accounts/{id:long}/password", async (ulong id, PasswordRequest r, HttpContext c, SupplierService s, CancellationToken ct) => { await s.ResetAccountPasswordAsync(AccessService.GetCurrent(c), id, r.NewPassword, ct); return Results.Json(new { }); });
        admin.MapDelete("/supplier-accounts/{id:long}", async (ulong id, HttpContext c, SupplierService s, CancellationToken ct) => { await s.DeleteAccountAsync(AccessService.GetCurrent(c), id, ct); return Results.Json(new { }); });
        return endpoints;
    }
}
