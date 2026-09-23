using Yf.Api.Infrastructure;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Tests;

/// <summary>Only the last UnreadWindow.Days days count as unread; older items are treated as read.</summary>
[Collection(ConnectionLifecycleCollection.Name)]
public sealed class UnreadWindowTests
{
    [Fact(Timeout = 120_000)]
    public async Task UnreadCountsAndFiltersIgnoreItemsOlderThanTheWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(30, UnreadWindow.Days);
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        await database.ExecuteAsync("""
            UPDATE users SET must_change_password=0 WHERE id=1;
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,status,must_change_password)
              VALUES(2,'author','x','作者','','INTERNAL','ACTIVE',0);
            INSERT INTO suppliers(id,name,status,created_by) VALUES(100,'S','ACTIVE',1);
            INSERT INTO project_groups(id,name,supplier_id,status,created_by,responsible_user_id) VALUES(1,'窗口主项目',100,'IN_PROGRESS',1,1);
            INSERT INTO projects(id,project_group_id,name,supplier_id,status,created_by,responsible_user_id)
              VALUES(1,1,'窗口子项目',100,'IN_PROGRESS',1,1);
            INSERT INTO messages(id,project_id,sender_id,content,status,created_at) VALUES
              (1,1,2,'四十天前未读','NORMAL',DATE_SUB(UTC_TIMESTAMP(),INTERVAL 40 DAY)),
              (2,1,2,'一天前未读','NORMAL',DATE_SUB(UTC_TIMESTAMP(),INTERVAL 1 DAY));
            INSERT INTO project_activities(id,project_id,activity_type,action,actor_id,actor_name,occurred_at,title,source_key) VALUES
              (1,1,'MESSAGE','CREATE',2,'作者',DATE_SUB(UTC_TIMESTAMP(3),INTERVAL 40 DAY),'旧留言','window-old'),
              (2,1,'MESSAGE','CREATE',2,'作者',DATE_SUB(UTC_TIMESTAMP(3),INTERVAL 1 DAY),'新留言','window-new');
            """, null, ct);
        var admin = new CurrentUser(1, "admin", "INTERNAL", null);

        await using var conn = await database.Database.OpenAsync(ct);
        var dashboard = new DashboardService();
        var summary = await dashboard.SummaryAsync(conn, admin, ct);
        Assert.Equal(1UL, summary.UnreadMessages);
        Assert.Equal([2UL, 1UL], summary.RecentMessages.Select(message => message.Id));
        Assert.Equal([true, false], summary.RecentMessages.Select(message => message.Unread));

        var unreadOnly = await dashboard.MessagesAsync(conn, admin, 1, 10, true, ct);
        Assert.Equal(1UL, unreadOnly.Total);
        Assert.Equal(2UL, Assert.Single(unreadOnly.List).Id);
        var all = await dashboard.MessagesAsync(conn, admin, 1, 10, false, ct);
        Assert.Equal([2UL, 1UL], all.List.Select(message => message.Id));

        Assert.Equal(1UL, await MessageService.UnreadCountAsync(conn, null, 1, 1, ct));
        var groups = new ProjectGroupService(new AuditService([]), null!);
        Assert.Equal(1UL, Assert.Single((await groups.ListAsync(conn, admin, 1, 20, null, null, null, ct)).List).UnreadMessages);
        var detail = await groups.DetailAsync(conn, admin, 1, ct);
        Assert.Equal(1UL, detail.Group.UnreadMessages);
        Assert.Equal(1UL, Assert.Single(detail.Projects).UnreadMessages);

        var collaboration = new CollaborationService();
        Assert.Equal(1UL, (await collaboration.SummaryAsync(conn, admin, ct)).UnreadCount);
        var notifications = await collaboration.NotificationsAsync(conn, admin, 1, 20, false, ct);
        Assert.Equal(1UL, notifications.UnreadCount);
        Assert.Equal([2UL, 1UL], notifications.List.Select(item => item.Id));
        Assert.Equal([false, true], notifications.List.Select(item => item.Read));
        var unreadNotifications = await collaboration.NotificationsAsync(conn, admin, 1, 20, true, ct);
        Assert.Equal(2UL, Assert.Single(unreadNotifications.List).Id);
    }
}
