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
        ("oem", "[ODM/OEM 文件传递] 新文件已送达：training-drawing.pdf",
            "王五，您好：\n\n有新的文件传递给您，请登录平台查看和下载。\n\n传递单：training-drawing.pdf\n方向：公司 → OEM（培训ODM-OEM厂商）\n"
            + "发送方：OEM培训发送人1029\n附件：1 个，共 2.5 KB\n\n请登录 ODM/OEM 文件传递平台查看：https://yf.example.test/oem-portal/transfers/5\n\n此邮件由系统自动发送，请勿直接回复。"),
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
        Assert.Contains("SAA · ODM/OEM 文件传递", oem);
        Assert.Contains(">登录 ODM/OEM 文件传递平台查看</a>", oem);
        Assert.Contains("公司 → OEM（培训ODM-OEM厂商）", oem);

        var directory = Environment.GetEnvironmentVariable("YF_MAIL_PREVIEW_DIR");
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
            foreach (var (name, subject, body) in Samples)
                File.WriteAllText(Path.Combine(directory, name + ".html"), MailHtml.Render(subject, body));
        }
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
