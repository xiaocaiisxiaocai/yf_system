using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Tests;

/// <summary>
/// Opt-in benchmark of the hot read endpoints over a large history (100k messages and 100k project
/// activities, half of them read). Skipped unless YF_PERF_BENCHMARK=1; writes timings and the slowest
/// SQL statements to .artifacts/tests/perf/. Not an assertion of speed: it produces comparable numbers.
/// </summary>
[Collection(ConnectionLifecycleCollection.Name)]
public sealed class PerformanceBenchmarks
{
    private const int Rounds = 5;

    [Fact(Timeout = 900_000)]
    public async Task HotReadEndpointsOverLargeHistory()
    {
        if (Environment.GetEnvironmentVariable("YF_PERF_BENCHMARK") != "1") Assert.Skip("Set YF_PERF_BENCHMARK=1 to run");
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        await SeedAsync(database, ct);

        var admin = new CurrentUser(1, "admin", "INTERNAL", null);
        var collaboration = new CollaborationService();
        var dashboard = new DashboardService();
        var groups = new ProjectGroupService(new AuditService([]), null!);
        var report = new StringBuilder();
        report.AppendLine($"History: 100000 messages, 100000 activities over ~2 years, 50% read; 20 groups x 5 projects; {Rounds} rounds, first is warm-up");

        var benchmarks = new (string Name, Func<MySqlConnector.MySqlConnection, Task> Run)[]
        {
            ("collaboration summary", conn => collaboration.SummaryAsync(conn, admin, ct)),
            ("dashboard summary", conn => dashboard.SummaryAsync(conn, admin, ct)),
            ("dashboard unread messages", conn => dashboard.MessagesAsync(conn, admin, 1, 10, true, ct)),
            ("dashboard messages", conn => dashboard.MessagesAsync(conn, admin, 1, 10, false, ct)),
            ("project group list", conn => groups.ListAsync(conn, admin, 1, 20, null, null, null, ct)),
            ("notifications (all)", conn => collaboration.NotificationsAsync(conn, admin, 1, 20, false, ct)),
            ("notifications (unread)", conn => collaboration.NotificationsAsync(conn, admin, 1, 20, true, ct)),
        };
        foreach (var (name, run) in benchmarks)
        {
            var times = new List<double>();
            using var capture = new SqlCapture();
            for (var round = 0; round < Rounds; round++)
            {
                await using var conn = await database.Database.OpenAsync(ct);
                if (round == Rounds - 1) capture.Start();
                var watch = Stopwatch.StartNew();
                await run(conn);
                times.Add(watch.Elapsed.TotalMilliseconds);
                capture.Stop();
            }
            var measured = times.Skip(1).ToArray();
            report.AppendLine($"{name,-28} median {Median(measured),7:F0} ms   rounds {string.Join(", ", measured.Select(t => t.ToString("F0")))}");
            foreach (var (sql, ms) in capture.Slowest(2))
                report.AppendLine($"    {ms,6:F0} ms  {sql}");
        }

        var directory = Path.Combine(RepositoryRoot(), ".artifacts", "tests", "perf");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"benchmark-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        await File.WriteAllTextAsync(path, report.ToString(), ct);
        TestContext.Current.SendDiagnosticMessage(report.ToString());
    }

    private static async Task SeedAsync(MigratedTestDatabase database, CancellationToken ct)
    {
        await database.ExecuteAsync("""
            UPDATE users SET must_change_password=0 WHERE id=1;
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,status,must_change_password)
              VALUES(2,'author','x','author','','INTERNAL','ACTIVE',0);
            INSERT INTO suppliers(id,name,status,created_by) VALUES(100,'S','ACTIVE',1);
            CREATE TABLE digits(i INT);
            INSERT INTO digits VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9);
            INSERT INTO project_groups(id,name,supplier_id,status,created_by,responsible_user_id)
              SELECT 1+a.i*10+b.i,CONCAT('G',a.i*10+b.i),100,'IN_PROGRESS',1,1 FROM digits a,digits b WHERE a.i*10+b.i<20;
            INSERT INTO projects(id,project_group_id,name,supplier_id,status,created_by,responsible_user_id)
              SELECT 1+a.i*10+b.i,1+(a.i*10+b.i)%20,CONCAT('P',a.i*10+b.i),100,'IN_PROGRESS',1,1 FROM digits a,digits b;
            INSERT INTO project_activities(project_id,activity_type,action,actor_id,actor_name,occurred_at,title,source_key)
              SELECT 1+(x.i*10000+a.i*1000+b.i*100+c.i*10+d.i)%100,'MESSAGE','CREATE',2,'author',
                     DATE_SUB(UTC_TIMESTAMP(3),INTERVAL (x.i*10000+a.i*1000+b.i*100+c.i*10+d.i)*10 MINUTE),'t',
                     CONCAT('k',x.i*10000+a.i*1000+b.i*100+c.i*10+d.i)
              FROM digits x,digits a,digits b,digits c,digits d;
            INSERT INTO collaboration_reads(activity_id,user_id) SELECT id,1 FROM project_activities WHERE id%2=0;
            INSERT INTO messages(project_id,sender_id,content,status,created_at)
              SELECT 1+(x.i*10000+a.i*1000+b.i*100+c.i*10+d.i)%100,2,'hello','NORMAL',
                     DATE_SUB(UTC_TIMESTAMP(),INTERVAL (x.i*10000+a.i*1000+b.i*100+c.i*10+d.i)*10 MINUTE)
              FROM digits x,digits a,digits b,digits c,digits d;
            INSERT INTO message_reads(message_id,user_id) SELECT id,1 FROM messages WHERE id%2=0;
            DROP TABLE digits;
            ANALYZE TABLE messages, message_reads, project_activities, collaboration_reads, projects, project_groups;
            """, null, ct);
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    private static string RepositoryRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));

    /// <summary>Records MySqlConnector command activities (statement text and duration).</summary>
    private sealed class SqlCapture : IDisposable
    {
        private readonly ActivityListener listener;
        private readonly ConcurrentBag<(string Sql, double Ms)> statements = [];
        private volatile bool active;

        public SqlCapture()
        {
            listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "MySqlConnector",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    if (!active) return;
                    var sql = activity.GetTagItem("db.statement") as string ?? activity.GetTagItem("db.query.text") as string;
                    if (!string.IsNullOrWhiteSpace(sql))
                        statements.Add((string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), activity.Duration.TotalMilliseconds));
                },
            };
            ActivitySource.AddActivityListener(listener);
        }

        public void Start() => active = true;
        public void Stop() => active = false;

        public IEnumerable<(string Sql, double Ms)> Slowest(int count) =>
            statements.OrderByDescending(item => item.Ms).Take(count)
                .Select(item => (item.Sql.Length > 400 ? item.Sql[..400] + " ..." : item.Sql, item.Ms));

        public void Dispose() => listener.Dispose();
    }
}
