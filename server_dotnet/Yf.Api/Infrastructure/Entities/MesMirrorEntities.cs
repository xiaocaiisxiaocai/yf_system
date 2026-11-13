using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Yf.Api.Infrastructure.Entities;

/// <summary>
/// One MES monitoring row mirrored into this system. The whole table is replaced after every complete fetch;
/// <see cref="RawJson"/> keeps the row exactly as MES returned it so field mappings can change without refetching.
/// </summary>
public sealed class MesWorkOrder
{
    public string WorkOrderNo { get; set; } = null!;
    public string BatchId { get; set; } = null!;
    public DateTime FetchedAt { get; set; }
    public string? CustomerName { get; set; }
    public string? DeviceModels { get; set; }
    public string RawJson { get; set; } = null!;
}

/// <summary>Same shape as <see cref="MesWorkOrder"/>: pages are collected here and swapped in only after the whole fetch is validated.</summary>
public sealed class MesWorkOrderStaging
{
    public string WorkOrderNo { get; set; } = null!;
    public string BatchId { get; set; } = null!;
    public DateTime FetchedAt { get; set; }
    public string? CustomerName { get; set; }
    public string? DeviceModels { get; set; }
    public string RawJson { get; set; } = null!;
}

/// <summary>Single-row (<c>id=1</c>) state of the MES mirror: a database-clock lease and the outcome of the last run.</summary>
public sealed class MesMirrorState
{
    public byte Id { get; set; }
    /// <summary>Business date the scheduled run last started for; a scheduled run happens at most once a day.</summary>
    public DateOnly? ScheduledFor { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public DateTime? LastStartedAt { get; set; }
    public DateTime? LastFinishedAt { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public string? LastError { get; set; }
    public string? LastTrigger { get; set; }
    /// <summary>Rows in the mirror after the last successful run; the next run must not fall far below it.</summary>
    public int LastCount { get; set; }
    /// <summary>MES address set on the settings page; empty means the configured <c>App:Ipms:MesBaseUrl</c> applies.</summary>
    public string? BaseUrl { get; set; }
}

public sealed class MesWorkOrderConfig : IEntityTypeConfiguration<MesWorkOrder>
{
    public void Configure(EntityTypeBuilder<MesWorkOrder> b)
    {
        b.ToTable("mes_work_orders");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.WorkOrderNo);
        b.Property(x => x.WorkOrderNo).HasColumnName("work_order_no").HasMaxLength(64);
        b.Property(x => x.BatchId).HasColumnName("batch_id").HasMaxLength(36).IsRequired();
        b.Property(x => x.FetchedAt).HasColumnName("fetched_at").HasColumnType("datetime(3)");
        b.Property(x => x.CustomerName).HasColumnName("customer_name").HasMaxLength(256);
        b.Property(x => x.DeviceModels).HasColumnName("device_models").HasMaxLength(256);
        b.Property(x => x.RawJson).HasColumnName("raw_json").HasColumnType("longtext").IsRequired();
    }
}

public sealed class MesWorkOrderStagingConfig : IEntityTypeConfiguration<MesWorkOrderStaging>
{
    public void Configure(EntityTypeBuilder<MesWorkOrderStaging> b)
    {
        b.ToTable("mes_work_orders_staging");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.WorkOrderNo);
        b.Property(x => x.WorkOrderNo).HasColumnName("work_order_no").HasMaxLength(64);
        b.Property(x => x.BatchId).HasColumnName("batch_id").HasMaxLength(36).IsRequired();
        b.Property(x => x.FetchedAt).HasColumnName("fetched_at").HasColumnType("datetime(3)");
        b.Property(x => x.CustomerName).HasColumnName("customer_name").HasMaxLength(256);
        b.Property(x => x.DeviceModels).HasColumnName("device_models").HasMaxLength(256);
        b.Property(x => x.RawJson).HasColumnName("raw_json").HasColumnType("longtext").IsRequired();
    }
}

public sealed class MesMirrorStateConfig : IEntityTypeConfiguration<MesMirrorState>
{
    public void Configure(EntityTypeBuilder<MesMirrorState> b)
    {
        b.ToTable("mes_mirror_state");
        b.UseCollation("utf8mb4_unicode_ci");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.ScheduledFor).HasColumnName("scheduled_for").HasColumnType("date");
        b.Property(x => x.LeaseUntil).HasColumnName("lease_until").HasColumnType("datetime(3)");
        b.Property(x => x.LastStartedAt).HasColumnName("last_started_at").HasColumnType("datetime(3)");
        b.Property(x => x.LastFinishedAt).HasColumnName("last_finished_at").HasColumnType("datetime(3)");
        b.Property(x => x.LastSuccessAt).HasColumnName("last_success_at").HasColumnType("datetime(3)");
        b.Property(x => x.LastError).HasColumnName("last_error").HasMaxLength(512);
        b.Property(x => x.LastTrigger).HasColumnName("last_trigger").HasMaxLength(16);
        b.Property(x => x.LastCount).HasColumnName("last_count").HasDefaultValue(0);
        b.Property(x => x.BaseUrl).HasColumnName("base_url").HasMaxLength(256);
    }
}
