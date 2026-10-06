using Microsoft.AspNetCore.Http;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Identity;
using Yf.Api.Modules.Oem.Common;
using Yf.Api.Modules.Oem.Identity;

namespace Yf.Api.Tests;

public sealed class OemIdentityRouteSecurityTests
{
    private readonly OemIdentityExtension extension = new(null!);

    [Theory]
    [InlineData("POST", "/api/v1/oem/auth/login", true)]
    [InlineData("POST", "/api/v1/oem/auth/refresh", true)]
    [InlineData("POST", "/api/v1/oem/auth/logout", true)]
    [InlineData("GET", "/api/v1/oem/auth/login", false)]
    [InlineData("POST", "/api/v1/oem/auth/login/extra", false)]
    [InlineData("GET", "/api/v1/oem/files/42/download", true)]
    [InlineData("HEAD", "/api/v1/oem/files/42/download", true)]
    [InlineData("POST", "/api/v1/oem/files/42/download", false)]
    [InlineData("GET", "/api/v1/oem/files/42/download/extra", false)]
    [InlineData("GET", "/api/v1/oem/files/not-a-number/download", false)]
    public void PublicRealmRoutesRequireExactMethodAndPath(string method, string path, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;

        Assert.Equal(expected, IdentityMiddleware.IsAnonymousPath(extension, context.Request));
    }

    [Fact]
    public void RealmAnonymousHookCannotBypassAuthenticationOutsideItsPrefix()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/v1/oem-other/auth/login";

        Assert.False(IdentityMiddleware.IsAnonymousPath(extension, context.Request));
    }

    [Fact]
    public void SupplierIdentityCannotCrossIntoOemWhileInternalIdentityCan()
    {
        var supplier = ContextFor(new CurrentUser(5, "SUP005", UserTypes.Supplier, 8, "supplier-session"));
        var error = Assert.Throws<ApiException>(() => OemActorAccessor.Get(supplier));
        Assert.Equal(40304, error.Code);

        var employee = ContextFor(new CurrentUser(6, "EMP006", UserTypes.Internal, null, "employee-session"));
        var actor = Assert.IsType<InternalOemActor>(OemActorAccessor.Get(employee));
        Assert.Equal((ulong)6, actor.Id);
        Assert.Equal("employee-session", actor.LoginSessionId);
    }

    private static DefaultHttpContext ContextFor(CurrentUser user)
    {
        var context = new DefaultHttpContext();
        context.Items[typeof(CurrentUser)] = user;
        context.Items[typeof(AccessClaims)] = new AccessClaims(
            user.Id, user.EmployeeNo, user.SessionId ?? throw new InvalidOperationException("Test user requires a session."),
            DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds());
        return context;
    }
}
