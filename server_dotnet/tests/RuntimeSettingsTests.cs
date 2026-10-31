using Dapper;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.SystemManagement;

namespace Yf.Api.Tests;

/// <summary>资料保留与上传限制改为页面配置：首次启动按配置补值，之后以页面为准，保存后立即生效。</summary>
public sealed class RuntimeSettingsTests
{
    [Theory]
    [InlineData("retention.audit_days", "0", "3651")]
    [InlineData("retention.mail_days", "0", "3651")]
    [InlineData("retention.mail_pending_days", "0", "366")]
    [InlineData("retention.upload_session_days", "29", "3651")]
    [InlineData("upload.max_active_sessions_per_user", "0", "1001")]
    [InlineData("upload.max_pending_bytes_per_user", "1073741823", "17592186044417")]
    public void ValuesMustStayInsideTheirRange(string key, string below, string above)
    {
        Assert.Throws<ApiException>(() => SystemService.NormalizeConfig(key, below));
        Assert.Throws<ApiException>(() => SystemService.NormalizeConfig(key, above));
        Assert.Throws<ApiException>(() => SystemService.NormalizeConfig(key, "abc"));
        var setting = RuntimeSettings.Find(key)!;
        Assert.Equal(setting.Maximum.ToString(), SystemService.NormalizeConfig(key, $" {setting.Maximum} "));
    }

    [Fact(Timeout = 90_000)]
    public async Task ConfiguredValuesSeedOnceAndPageChangesApplyImmediately()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SchemaShapeTests.SchemaDatabaseScope.CreateOrSkipAsync("runtime_settings", ct);
        await database.InitializeAsync(ct);
        var options = database.Options;
        options.AuditRetentionDays = 200;
        options.UploadMaxActiveSessionsPerUser = 7;
        await RuntimeSettings.ApplyAsync(options, seed: true, ct);

        // A later configuration change no longer wins over the stored value.
        options.AuditRetentionDays = 999;
        await RuntimeSettings.ApplyAsync(options, seed: true, ct);
        Assert.Equal(200, options.AuditRetentionDays);
        await using (var conn = await database.Database.OpenAsync(ct))
        {
            Assert.Equal("7", await conn.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT cfg_value FROM system_configs WHERE cfg_key='upload.max_active_sessions_per_user'", cancellationToken: ct)));
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE users SET must_change_password=0 WHERE employee_no='admin'", cancellationToken: ct));
        }

        await using var read = await database.Database.OpenAsync(ct);
        var adminId = await read.ExecuteScalarAsync<ulong>(new CommandDefinition(
            "SELECT id FROM users WHERE employee_no='admin'", cancellationToken: ct));
        await read.CloseAsync();
        var service = new SystemService(database.Database, new AuditService([]), options);
        await service.UpdateConfigsAsync(new ConfigBatch(
        [
            new ConfigItem("retention.audit_days", "90"),
            new ConfigItem("upload.max_pending_bytes_per_user", "2147483648"),
        ]), new CurrentUser(adminId, "admin", "INTERNAL", null), ct);
        Assert.Equal(90, options.AuditRetentionDays);
        Assert.Equal(2147483648L, options.UploadMaxPendingBytesPerUser);

        var listed = await service.ListConfigsAsync(ct);
        Assert.Equal("操作日志保留天数", listed.Single(item => item.Key == "retention.audit_days").Description);
    }
}
