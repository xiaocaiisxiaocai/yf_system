using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Outsource.Ipms.Mes;

/// <summary>Registered when the MES mirror is not selected; the sync service never starts then, so this only refuses stray calls.</summary>
public sealed class UnconfiguredMesSource : IMesSource
{
    public Task<MesTestResult> TestAsync(string? baseUrl, CancellationToken ct) =>
        Task.FromResult(new MesTestResult(false, "MES 数据镜像尚未启用", null, 0));

    public Task<MesPage> FetchPageAsync(int page, int pageSize, CancellationToken ct) =>
        throw new IpmsUnavailableException("MES 数据镜像尚未设定，请联系系统管理员");
}

internal static class MesSourceFactory
{
    internal static IMesSource Create(AppOptions options, IServiceProvider services)
    {
        if (options.Ipms.Mode != IpmsOptions.MesMirrorMode) return new UnconfiguredMesSource();
        // One long-lived client: the per-page timeout is enforced by the source, so the client itself never times out.
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var settings = new DbMesSettings(services.GetRequiredService<IDbContextFactory<YfDbContext>>(), options);
        return new MesHttpSource(http, options.Ipms, settings);
    }
}

/// <summary>The address saved on the page (<c>mes_mirror_state.base_url</c>) wins over the configured <c>App:Ipms:MesBaseUrl</c>.</summary>
public sealed class DbMesSettings(IDbContextFactory<YfDbContext> dbFactory, AppOptions options) : IMesSettings
{
    public async Task<string?> BaseUrlAsync(CancellationToken ct)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        var saved = await context.MesMirrorState.AsNoTracking().Where(x => x.Id == 1).Select(x => x.BaseUrl).SingleOrDefaultAsync(ct);
        return !string.IsNullOrWhiteSpace(saved) ? saved : string.IsNullOrWhiteSpace(options.Ipms.MesBaseUrl) ? null : options.Ipms.MesBaseUrl;
    }
}
