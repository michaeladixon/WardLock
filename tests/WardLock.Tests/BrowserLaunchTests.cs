using WardLock.Services.BrowserBridge;

namespace WardLock.Tests;

public class BrowserLaunchTests
{
    [Fact]
    public void Chrome_and_Edge_launch_is_detected_by_extension_origin()
    {
        // Chrome/Edge on Windows: origin, then the calling window handle.
        string[] args = ["chrome-extension://hcbclfghekjpdgnbfnmfeaamigencjjf/", "--parent-window=1234"];
        Assert.Equal("chrome-extension://hcbclfghekjpdgnbfnmfeaamigencjjf/", NativeMessagingProxy.DetectBrowserOrigin(args));
    }

    [Fact]
    public void Firefox_launch_is_detected_by_manifest_path_and_addon_id()
    {
        // Firefox: full path of the host manifest, then the add-on ID.
        string[] args = [@"C:\Users\me\AppData\Local\WardLock\com.wardlock.wardlock.firefox.json", "wardlock@wardlock.app"];
        Assert.Equal(
            BrowserIntegrationInstaller.FirefoxOrigin(BrowserIntegrationInstaller.FirefoxExtensionId),
            NativeMessagingProxy.DetectBrowserOrigin(args));
    }

    [Theory]
    [InlineData]                                   // double-click / Start menu
    [InlineData("--minimized")]
    [InlineData(@"C:\backup.json")]               // a single path is not a host launch
    [InlineData(@"C:\settings.json", "--flag")]   // switches are not add-on IDs
    [InlineData(@"C:\notes.txt", "wardlock@wardlock.app")]
    public void Normal_launches_start_the_app(params string[] args)
        => Assert.Null(NativeMessagingProxy.DetectBrowserOrigin(args));

    [Fact]
    public void Firefox_identity_never_collides_with_a_chromium_origin()
    {
        var firefox = BrowserIntegrationInstaller.FirefoxOrigin(BrowserIntegrationInstaller.FirefoxExtensionId);
        Assert.StartsWith("firefox:", firefox);
        Assert.DoesNotContain("://", firefox);
    }
}
