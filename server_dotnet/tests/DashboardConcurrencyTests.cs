using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Tests;

[Collection(ConnectionLifecycleCollectionDefinition.Name)]
public sealed class DashboardConcurrencyTests
{
    [Theory(Timeout = 120_000)]
    [InlineData("list", "delete")]
    [InlineData("summary", "delete")]
    [InlineData("unread", "delete")]
    [InlineData("list", "reassign")]
    [InlineData("summary", "reassign")]
    [InlineData("unread", "reassign")]
    [InlineData("unread", "read")]
    public async Task DetailQueryRechecksMessagesAfterConcurrentChange(string endpoint, string mutation)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        await database.ExecuteAsync("""
            INSERT INTO roles(id,name,status,is_built_in) VALUES(9001,'dashboard reader','ACTIVE',0);
            INSERT INTO role_permissions(role_id,permission_id)
                SELECT 9001,id FROM permissions WHERE code IN ('dashboard','project:list');
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,status,must_change_password)
                VALUES(2,'reader','unused','Reader','','INTERNAL','ACTIVE',0),
                      (3,'replacement','unused','Replacement','','INTERNAL','ACTIVE',0);
            INSERT INTO user_roles(user_id,role_id) VALUES(2,9001),(3,9001);
            INSERT INTO suppliers(id,name,status,created_by) VALUES(100,'test','ACTIVE',1);
            INSERT INTO project_groups(id,name,supplier_id,status,created_by,responsible_user_id)
                VALUES(5001,'group',100,'IN_PROGRESS',1,2);
            INSERT INTO projects(id,project_group_id,name,supplier_id,status,created_by,responsible_user_id)
                VALUES(1001,5001,'project',100,'IN_PROGRESS',1,2);
            INSERT INTO messages(id,project_id,sender_id,content,status)
                VALUES(10001,1001,1,'private message','NORMAL');
            """, null, ct);

        await using var connection = await database.Database.OpenAsync(ct);
        // Commit on a second real connection immediately before the unified detail/count
        // statement executes. No sleeps or production test hooks are required.
        using var change = new BeforeDetailQuery(connection.Database, () =>
        {
            using var concurrent = new MySqlConnection(AppDb.BuildConnectionString(database.Options));
            concurrent.Open();
            using var tx = concurrent.BeginTransaction(System.Data.IsolationLevel.ReadCommitted);
            using var command = concurrent.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "SELECT cfg_key FROM system_configs WHERE cfg_key='security.management_lock' LOCK IN SHARE MODE";
            command.ExecuteScalar();
            command.CommandText = mutation switch
            {
                "delete" => "UPDATE messages SET status='DELETED',deleted_at=UTC_TIMESTAMP() WHERE id=10001",
                "reassign" => "UPDATE project_groups SET responsible_user_id=3 WHERE id=5001; UPDATE projects SET responsible_user_id=3 WHERE id=1001",
                "read" => "INSERT INTO message_reads(message_id,user_id,read_at) VALUES(10001,2,UTC_TIMESTAMP())",
                _ => throw new InvalidOperationException("Unknown mutation")
            };
            command.ExecuteNonQuery();
            tx.Commit();
        });
        var service = new DashboardService();
        var actor = new CurrentUser(2, "reader", "INTERNAL", null);
        if (endpoint == "summary")
            Assert.Empty((await service.SummaryAsync(connection, actor, ct)).RecentMessages);
        else
        {
            var page = await service.MessagesAsync(connection, actor, 1, 20, endpoint == "unread", ct);
            Assert.Equal(0UL, page.Total);
            Assert.Empty(page.List);
        }
        Assert.True(change.Fired, "The concurrent mutation must occur before detail/count materialization.");
        Assert.Equal(1, CountOccurrences(change.CommandText!, "COUNT(*)"));
    }

    private static int CountOccurrences(string value, string pattern)
    {
        var count = 0;
        for (var index = 0; (index = value.IndexOf(pattern, index, StringComparison.OrdinalIgnoreCase)) >= 0;
             index += pattern.Length)
            count++;
        return count;
    }

    private sealed class BeforeDetailQuery : IDisposable
    {
        private readonly List<IDisposable> subscriptions = [];
        private readonly IDisposable listeners;
        private int fired;
        public bool Fired => Volatile.Read(ref fired) != 0;
        public string? CommandText { get; private set; }

        public BeforeDetailQuery(string database, Action change)
        {
            listeners = DiagnosticListener.AllListeners.Subscribe(new Observer<DiagnosticListener>(listener =>
            {
                if (listener.Name != "Microsoft.EntityFrameworkCore") return;
                subscriptions.Add(listener.Subscribe(new Observer<KeyValuePair<string, object?>>(item =>
                {
                    if (!item.Key.EndsWith("CommandExecuting", StringComparison.Ordinal)
                        || item.Value is not CommandEventData data || data.Command.Connection?.Database != database)
                        return;
                    var sql = data.Command.CommandText;
                    if (!sql.Contains("`content`", StringComparison.Ordinal) || !sql.Contains("FROM `messages`", StringComparison.Ordinal))
                        return;
                    if (Interlocked.Exchange(ref fired, 1) == 0)
                    {
                        CommandText = sql;
                        change();
                    }
                })));
            }));
        }

        public void Dispose()
        {
            listeners.Dispose();
            foreach (var subscription in subscriptions) subscription.Dispose();
        }
    }

    private sealed class Observer<T>(Action<T> action) : IObserver<T>
    {
        public void OnNext(T value) => action(value);
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
}
