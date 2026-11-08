using Yf.Api.Modules.SystemManagement;

namespace Yf.Api.Tests;

public sealed class MailHtmlTests
{
    private static readonly (string Name, string Subject, string Body)[] Samples =
    [
        ("message", "[协作平台] 项目「2107796-1 CSUR-625F」有新留言",
            "项目：2107796-1 CSUR-625F\n以下留言请登录平台查看（本邮件由系统自动发送）：\n\n"
            + "- 留言人：账号 123\n  内容：请确认第二版资料\n  https://yf.example.test/projects/5?tab=messages&target=12\n"),
        ("file", "[协作平台] 项目「2107796-1 CSUR-625F」有新文件",
            "项目：2107796-1 CSUR-625F\n方向：供应商 → 公司\n以下文件请登录平台查看并下载（邮件不含附件）：\n\n"
            + "- 模板.pptx\n  https://yf.example.test/projects/5?tab=files&target=31\n  上传人：123（张三）\n"
            + "- 482a103b2cff877bdceb27ef575510cc.mp4\n  https://yf.example.test/projects/5?tab=files&target=32\n  上传人：123（张三）\n"),
        ("status", "[协作平台] 子项目「1」已终止",
            "项目：test1111 / 1\n状态：子项目已终止\n原因：客户取消\n\n请登录平台查看：https://yf.example.test/projects/5?tab=activity\n\n（本邮件由系统自动发送）"),
        ("oem", "[ODM/OEMA 文件传递] 新文件已送达 · 传递单 #5",
            "王五，您好：\n\n有新的文件传递给您，请登录平台查看和下载。\n\n传递单编号：#5\n方向：公司 → ODM/OEMA（培训ODM-OEM厂商）\n"
            + "发送方：内部账号 #1029\n附件：1 个，共 2.5 KB\n\n请登录 ODM/OEMA 文件传递平台查看：https://yf.example.test/oem-portal/transfers/5\n\n此邮件由系统自动发送，请勿直接回复。"),
        ("test", "[供应商协作平台] SMTP 测试邮件",
            "这是一封 SMTP 配置测试邮件。收到此邮件说明当前配置已成功完成邮件发送。\n此邮件由管理员手动触发，请勿回复。"),
    ];

    [Fact]
    public void NotificationLayoutBecomesStructuredHtml()
    {
        var status = MailHtml.Render(Samples[2].Subject, Samples[2].Body);
        Assert.Contains("SAA · 供应商协作平台", status);
        Assert.Contains(">子项目「1」已终止</h1>", status);
        Assert.Contains(">原因</td>", status);
        Assert.Contains("href=\"https://yf.example.test/projects/5?tab=activity\"", status);
        Assert.Contains(">登录平台查看</a>", status);
        Assert.Contains("本邮件由系统自动发送", status);

        var file = MailHtml.Render(Samples[1].Subject, Samples[1].Body);
        Assert.Contains(">模板.pptx</div>", file);
        Assert.Contains("上传人：123（张三）", file);
        Assert.Equal(2, file.Split("查看 →").Length - 1);
        Assert.Contains("此邮件由系统自动发送，请勿直接回复。", file);

        var oem = MailHtml.Render(Samples[3].Subject, Samples[3].Body);
        Assert.Contains("SAA · ODM/OEMA 文件传递", oem);
        Assert.Contains(">登录 ODM/OEMA 文件传递平台查看</a>", oem);
        Assert.Contains("公司 → ODM/OEMA（培训ODM-OEM厂商）", oem);

        var directory = Environment.GetEnvironmentVariable("YF_MAIL_PREVIEW_DIR");
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
            foreach (var (name, subject, body) in Samples)
            {
                File.WriteAllText(Path.Combine(directory, name + ".html"), MailHtml.Render(subject, body));
                File.WriteAllText(Path.Combine(directory, name + "-internal.html"),
                    MailHtml.Render(subject, MailLinks.ForRecipient(body, Deployment(), internalStaff: true)));
            }
        }
    }

    private static Yf.Api.Infrastructure.AppOptions Deployment() => new()
    {
        WebBaseUrl = "https://yf.example.test",
        AdditionalWebOrigins = ["http://192.168.7.102:3340"],
    };

    [Fact]
    public void InternalStaffGetIntranetAndInternetLinksWhileVendorsKeepTheExternalOne()
    {
        var options = Deployment();
        var status = Samples[2].Body;
        var internalBody = MailLinks.ForRecipient(status, options, internalStaff: true);
        Assert.Contains("外网访问：https://yf.example.test/projects/5?tab=activity", internalBody);
        Assert.Contains("内网访问：http://192.168.7.102:3340/projects/5?tab=activity", internalBody);
        Assert.DoesNotContain("请登录平台查看", internalBody);
        Assert.Equal(status, MailLinks.ForRecipient(status, options, internalStaff: false));

        var file = MailLinks.ForRecipient(Samples[1].Body, options, internalStaff: true);
        Assert.Contains("  外网访问：https://yf.example.test/projects/5?tab=files&target=31", file);
        Assert.Contains("  内网访问：http://192.168.7.102:3340/projects/5?tab=files&target=31", file);
        var html = MailHtml.Render(Samples[1].Subject, file);
        Assert.Contains(">外网访问 →</a>", html);
        Assert.Contains(">内网访问 →</a>", html);
        var buttons = MailHtml.Render(Samples[2].Subject, internalBody);
        Assert.Contains(">外网访问</a>", buttons);
        Assert.Contains(">内网访问</a>", buttons);

        // No private origin, or a site that is itself on the private network: one link, unchanged.
        Assert.Equal(status, MailLinks.ForRecipient(status, new() { WebBaseUrl = "https://yf.example.test", AdditionalWebOrigins = ["https://other.example.test"] }, true));
        Assert.Equal(status, MailLinks.ForRecipient(status, new() { WebBaseUrl = "http://192.168.7.102:3340", AdditionalWebOrigins = ["http://10.0.0.5"] }, true));
    }

    [Theory]
    [InlineData("http://192.168.7.102:3340", true)]
    [InlineData("http://10.1.2.3", true)]
    [InlineData("http://172.20.0.1", true)]
    [InlineData("http://127.0.0.1:5180", true)]
    [InlineData("http://intranet-host", true)]
    [InlineData("http://183.238.37.108:8090", false)]
    [InlineData("https://yf.example.test", false)]
    [InlineData("http://172.32.0.1", false)]
    public void PrivateOriginsAreRecognised(string origin, bool expected) => Assert.Equal(expected, MailLinks.IsPrivate(origin));

    [Fact]
    public void OnlyInternalStaffRecipientsAreTreatedAsInternal()
    {
        Assert.True(MailLinks.IsInternalStaff("INTERNAL", "internal", null));
        Assert.False(MailLinks.IsInternalStaff("SUPPLIER", "internal", null));
        Assert.True(MailLinks.IsInternalStaff(null, "internal", 7));
        Assert.False(MailLinks.IsInternalStaff(null, "oem", 7));
    }

    [Fact]
    public void UserTextIsEncodedAndOnlyHttpLinksAreClickable()
    {
        var html = MailHtml.Render("[协作平台] <b>x</b>",
            "项目：<script>alert(1)</script>\n请登录平台查看：javascript:alert(1)\n\n- 文件\"名\n  ftp://example.test/a\n");
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<b>x</b>", html);
        Assert.DoesNotContain("href=\"javascript:", html);
        Assert.DoesNotContain("href=\"ftp:", html);
        Assert.Contains("文件&quot;名", html);
    }
}
