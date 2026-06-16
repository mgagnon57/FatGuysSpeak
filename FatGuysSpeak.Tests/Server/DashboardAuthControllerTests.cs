using FatGuysSpeak.Server.Controllers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Moq;

namespace FatGuysSpeak.Tests.Server;

public class DashboardAuthControllerTests
{
    private static DashboardAuthController MakeController(
        string configUser = "Admin",
        string configPass = "Admin")
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Dashboard:Username"] = configUser,
                ["Dashboard:Password"] = configPass,
            })
            .Build();

        var authServiceMock = new Mock<IAuthenticationService>();
        authServiceMock
            .Setup(x => x.SignInAsync(
                It.IsAny<HttpContext>(),
                "Dashboard",
                It.IsAny<System.Security.Claims.ClaimsPrincipal>(),
                It.IsAny<AuthenticationProperties?>()))
            .Returns(Task.CompletedTask);
        authServiceMock
            .Setup(x => x.SignOutAsync(
                It.IsAny<HttpContext>(),
                "Dashboard",
                It.IsAny<AuthenticationProperties?>()))
            .Returns(Task.CompletedTask);

        var servicesMock = new Mock<IServiceProvider>();
        servicesMock
            .Setup(s => s.GetService(typeof(IAuthenticationService)))
            .Returns(authServiceMock.Object);

        var controller = new DashboardAuthController(config);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { RequestServices = servicesMock.Object }
        };
        return controller;
    }

    [Fact]
    public void LoginPage_NoError_ReturnsHtml()
    {
        var ctrl = MakeController();
        var result = ctrl.LoginPage(error: false);
        Assert.Equal("text/html", result.ContentType);
        Assert.Contains("Server Dashboard", result.Content);
        Assert.DoesNotContain("Invalid username", result.Content!);
    }

    [Fact]
    public void LoginPage_WithError_ShowsErrorMessage()
    {
        var ctrl = MakeController();
        var result = ctrl.LoginPage(error: true);
        Assert.Contains("Invalid username or password", result.Content);
    }

    [Fact]
    public async Task Login_CorrectCredentials_RedirectsToDashboard()
    {
        var ctrl = MakeController("Admin", "Admin");
        var result = await ctrl.Login("Admin", "Admin");
        var redirect = Assert.IsType<LocalRedirectResult>(result);
        Assert.Equal("/dashboard", redirect.Url);
    }

    [Fact]
    public async Task Login_WrongPassword_RedirectsToLoginWithError()
    {
        var ctrl = MakeController("Admin", "Admin");
        var result = await ctrl.Login("Admin", "wrongpass");
        var redirect = Assert.IsType<LocalRedirectResult>(result);
        Assert.Contains("/dashboard/login", redirect.Url);
        Assert.Contains("error", redirect.Url);
    }

    [Fact]
    public async Task Login_WrongUsername_RedirectsToLoginWithError()
    {
        var ctrl = MakeController("Admin", "Admin");
        var result = await ctrl.Login("notadmin", "Admin");
        var redirect = Assert.IsType<LocalRedirectResult>(result);
        Assert.Contains("/dashboard/login", redirect.Url);
        Assert.Contains("error", redirect.Url);
    }

    [Fact]
    public async Task Logout_RedirectsToLoginPage()
    {
        var ctrl = MakeController();
        var result = await ctrl.Logout();
        var redirect = Assert.IsType<LocalRedirectResult>(result);
        Assert.Equal("/dashboard/login", redirect.Url);
    }
}
