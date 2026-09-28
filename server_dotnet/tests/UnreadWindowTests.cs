using Yf.Api.Infrastructure;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Tests;

/// <summary>Only the last UnreadWindow.Days days count as unread; older items are treated as read.</summary>
[Collection(ConnectionLifecycleCollectionDefinition.Name)]
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
        Assert.Equal([2UL], summary.RecentMessages.Select(message => message.Id));
        Assert.Equal([true], summary.RecentMessages.Select(message => message.Unread));

        var unreadOnly = await dashboard.MessagesAsync(conn, admin, 1, 10, true, ct);
        Assert.Equal(1UL, unreadOnly.Total);
        Assert.Equal(2UL, Assert.Single(unreadOnly.List).Id);
        var all = await dashboard.MessagesAsync(conn, admin, 1, 10, false, ct);
        Assert.Equal(1UL, all.Total);
        Assert.Equal([2UL], all.List.Select(message => message.Id));

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
        Assert.Equal(1UL, notifications.Total);
        Assert.Equal(2UL, Assert.Single(notifications.List).Id);
        Assert.False(notifications.List[0].Read);
        var unreadNotifications = await collaboration.NotificationsAsync(conn, admin, 1, 20, true, ct);
        Assert.Equal(2UL, Assert.Single(unreadNotifications.List).Id);
    }

    /// <summary>
    /// The collaboration summary computes its unread count in hand-written SQL while the notification list
    /// uses LINQ. Both must apply the same visibility, meaningful-action, actor and window rules.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task CollaborationSummaryAndNotificationUnreadCountsAgreeForEveryScope()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigratedTestDatabase.CreateOrSkipAsync(ct);
        var activities = new List<string>();
        foreach (var projectId in new[] { 1, 2, 3 })
        {
            var b = projectId * 10;
            activities.Add($"({b + 1},{projectId},'FILE','UPLOAD',2,'作者',DATE_SUB(UTC_TIMESTAMP(3),INTERVAL 1 DAY),'文件','s{b + 1}')");
            activities.Add($"({b + 2},{projectId},'MESSAGE','CREATE',30,'供应商',DATE_SUB(UTC_TIMESTAMP(3),INTERVAL 1 DAY),'留言','s{b + 2}')");
            activities.Add($"({b + 3},{projectId},'PROJECT','SUBMIT',30,'供应商',DATE_SUB(UTC_TIMESTAMP(3),INTERVAL 1 DAY),'提交','s{b + 3}')");
            activities.Add($"({b + 4},{projectId},'PROJECT','CONFIRM',2,'作者',DATE_SUB(UTC_TIMESTAMP(3),INTERVAL 1 DAY),'确认','s{b + 4}')");
            activities.Add($"({b + 5},{projectId},'PROJECT','UPDATE',2,'作者',DATE_SUB(UTC_TIMESTAMP(3),INTERVAL 1 DAY),'编辑','s{b + 5}')");
            activities.Add($"({b + 6},{projectId},'FILE','UPLOAD',NULL,'系统',DATE_SUB(UTC_TIMESTAMP(3),INTERVAL 1 DAY),'系统','s{b + 6}')");
            activities.Add($"({b + 7},{projectId},'MESSAGE','CREATE',2,'作者',DATE_SUB(UTC_TIMESTAMP(3),INTERVAL 40 DAY),'旧留言','s{b + 7}')");
        }
        activities.Add("(18,1,'FILE','UPLOAD',20,'负责人',DATE_SUB(UTC_TIMESTAMP(3),INTERVAL 1 DAY),'自己的文件','s18')");
        await database.ExecuteAsync($"""
            UPDATE users SET must_change_password=0 WHERE id=1;
            INSERT INTO suppliers(id,name,status,created_by) VALUES(100,'S1','ACTIVE',1),(200,'S2','ACTIVE',1);
            INSERT INTO roles(id,name,status,is_built_in) VALUES(9101,'confirm owner','ACTIVE',0),(9102,'list only','ACTIVE',0);
            INSERT INTO role_permissions(role_id,permission_id)
                SELECT 9101,id FROM permissions WHERE code IN ('project:list','project:confirm');
            INSERT INTO role_permissions(role_id,permission_id)
                SELECT 9102,id FROM permissions WHERE code='project:list';
            INSERT INTO users(id,employee_no,password_hash,real_name,email,user_type,supplier_id,status,must_change_password) VALUES
                (2,'author','x','作者','','INTERNAL',NULL,'ACTIVE',0),
                (20,'owner-confirm','x','负责人','','INTERNAL',NULL,'ACTIVE',0),
                (21,'owner-list','x','只读负责人','','INTERNAL',NULL,'ACTIVE',0),
                (30,'supplier-user','x','供应商','','SUPPLIER',100,'ACTIVE',0);
            INSERT INTO user_roles(user_id,role_id) VALUES(20,9101),(21,9102),(30,9102);
            INSERT INTO project_groups(id,name,supplier_id,status,created_by,responsible_user_id) VALUES
                (1,'G1',100,'IN_PROGRESS',1,20),(2,'G2',100,'IN_PROGRESS',1,21),(3,'G3',200,'IN_PROGRESS',1,1);
            INSERT INTO projects(id,project_group_id,name,supplier_id,status,created_by,responsible_user_id) VALUES
                (1,1,'P1',100,'IN_PROGRESS',1,20),(2,2,'P2',100,'IN_PROGRESS',1,21),(3,3,'P3',200,'IN_PROGRESS',1,1);
            INSERT INTO project_activities(id,project_id,activity_type,action,actor_id,actor_name,occurred_at,title,source_key) VALUES
                {string.Join(",", activities)};
            INSERT INTO collaboration_reads(activity_id,user_id,read_at) VALUES(11,20,UTC_TIMESTAMP(3)),(12,30,UTC_TIMESTAMP(3));
            """, null, ct);

        await using var conn = await database.Database.OpenAsync(ct);
        var collaboration = new CollaborationService();
        foreach (var (actor, expected) in new[]
        {
            (new CurrentUser(1, "admin", "INTERNAL", null), 13UL),
            (new CurrentUser(20, "owner-confirm", "INTERNAL", null), 3UL),
            (new CurrentUser(21, "owner-list", "INTERNAL", null), 3UL),
            (new CurrentUser(30, "supplier-user", "SUPPLIER", 100), 5UL),
        })
        {
            var summary = await collaboration.SummaryAsync(conn, actor, ct);
            var all = await collaboration.NotificationsAsync(conn, actor, 1, 100, false, ct);
            var unread = await collaboration.NotificationsAsync(conn, actor, 1, 100, true, ct);
            Assert.Equal(expected, summary.UnreadCount);
            Assert.Equal(expected, all.UnreadCount);
            Assert.Equal(expected, unread.Total);
            Assert.Equal((int)expected, unread.List.Count);
            Assert.Equal((ulong)all.List.Count(item => !item.Read), expected);
        }
    }
}
