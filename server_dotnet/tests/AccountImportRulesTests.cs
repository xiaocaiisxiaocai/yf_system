using Dapper;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Admin;
using Yf.Api.Modules.Identity;

namespace Yf.Api.Tests;

public sealed class AccountImportRulesTests
{
    [Fact]
    public void ImportedInitialPasswordsAreOnlyLengthChecked()
    {
        PasswordService.ValidateInitial("123456");
        Assert.Throws<ApiException>(() => PasswordService.Validate("123456"));
        Assert.Equal(PasswordService.InitialPolicyMessage,
            Assert.Throws<ApiException>(() => PasswordService.ValidateInitial("1234")).Message);
        Assert.Throws<ApiException>(() => PasswordService.ValidateInitial(new string('1', 21)));
    }

    [Fact(Timeout = 90_000)]
    public async Task ImportAcceptsDepartmentLevelUsersWithoutEmailAndSimpleInitialPasswords()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("account_import_rules", ct);
        await database.InitializeBusinessFixtureAsync(ct);
        await SchemaMigrations.ApplyAsync(database.Database, ct);
        await database.ExecuteAsync("""
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,status,must_change_password,failed_login_attempts,created_at,updated_at)
            VALUES(9700,'import-rules-admin','unused','导入管理员','admin@example.invalid','INTERNAL','ACTIVE',0,0,UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            INSERT INTO user_roles(user_id,role_id) VALUES(9700,1);
            INSERT INTO departments(id,parent_id,name,kind,created_at,updated_at)
            VALUES(9701,NULL,'导入验收事业部','DIVISION',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3)),
                  (9702,9701,'导入验收部门','DEPARTMENT',UTC_TIMESTAMP(3),UTC_TIMESTAMP(3));
            """, ct);
        var factory = EfTestSupport.DbContextFactory(database.Options);
        var audit = new AuditService([]);
        var users = new UserService(factory, new PermissionService(), audit);
        var imports = new AdminImportService(factory, users, new SupplierService(factory, new PermissionService(), audit));
        var admin = new CurrentUser(9700, "import-rules-admin", "INTERNAL", null);

        var result = await imports.ImportInternalUsersAsync(admin, new InternalUserImportRequest([
            new(2, "division.lead", "事业部主管", "", 9701, 3, "123456"),
            new(3, "dept.lead", "部门主管", "dept.lead@example.invalid", 9702, 3, "123456"),
            new(4, "too.short", "密码太短", "", 9702, 3, "1234"),
        ]), ct);
        Assert.Equal(2, result.Created);
        Assert.Equal(1, result.Failed);
        Assert.Equal(PasswordService.InitialPolicyMessage, result.Rows.Single(row => row.Key == "too.short").Message);

        await using (var conn = await database.Database.OpenAsync(ct))
        {
            var created = (await conn.QueryAsync<(string EmployeeNo, string Email, ulong DepartmentId, bool MustChange)>(new CommandDefinition(
                "SELECT employee_no,email,department_id,must_change_password FROM users WHERE id<>9700 ORDER BY employee_no",
                cancellationToken: ct))).ToArray();
            Assert.Equal([("dept.lead", "dept.lead@example.invalid", 9702UL, true), ("division.lead", "", 9701UL, true)], created);
        }

        // Accounts created by hand keep the full password policy; email stays optional there too.
        var weak = await Assert.ThrowsAsync<ApiException>(() => users.CreateAsync(admin,
            new UserCreate("manual.weak", "123456", "手工新增", "", 9702, 3, null), ct));
        Assert.Equal(PasswordService.PolicyMessage, weak.Message);
        var manual = await users.CreateAsync(admin, new UserCreate("manual.ok", "Strong#2026", "手工新增", "", 9702, 3, null), ct);
        Assert.Equal("", manual.Email);
    }
}
