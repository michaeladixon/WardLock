using System.Runtime.InteropServices;
using System.Windows.Interop;
using Windows.Security.Credentials.UI;

namespace WardLock.Services;

/// <summary>
/// Uses Windows Hello (fingerprint, face, PIN) to gate access to the vault.
/// Falls back gracefully if Windows Hello is not available.
/// </summary>
public static class WindowsHelloService
{
    /// <summary>Outcome of a verification prompt; <see cref="Message"/> is user-facing.</summary>
    public readonly record struct HelloResult(bool Verified, string Message);

    /// <summary>
    /// Check if Windows Hello is configured and available on this device.
    /// </summary>
    public static async Task<bool> IsAvailableAsync()
    {
        try
        {
            var availability = await UserConsentVerifier.CheckAvailabilityAsync();
            return availability == UserConsentVerifierAvailability.Available;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Prompt the user to verify their identity via Windows Hello.
    /// </summary>
    public static async Task<HelloResult> VerifyAsync(string message = "Verify your identity to access WardLock")
    {
        try
        {
            // RequestVerificationAsync is the UWP API: from a desktop app the
            // prompt has no owner window and can open behind WardLock. On
            // Windows 11 (build 22000+) the interop overload parents it to our
            // window; Windows 10 keeps the original call.
            var owner = OwnerWindowHandle();
            var result = owner != IntPtr.Zero && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
                ? await UserConsentVerifierInterop.RequestVerificationForWindowAsync(owner, message)
                : await UserConsentVerifier.RequestVerificationAsync(message);

            return new HelloResult(result == UserConsentVerificationResult.Verified, Describe(result));
        }
        catch (COMException)
        {
            // WinRT interop can throw if the API is unavailable
            return new HelloResult(false, "Windows Hello is unavailable right now.");
        }
        catch
        {
            return new HelloResult(false, "Windows Hello is unavailable right now.");
        }
    }

    private static IntPtr OwnerWindowHandle()
    {
        var window = System.Windows.Application.Current?.MainWindow;
        return window == null ? IntPtr.Zero : new WindowInteropHelper(window).Handle;
    }

    private static string Describe(UserConsentVerificationResult result) => result switch
    {
        UserConsentVerificationResult.Verified => "Verified.",
        UserConsentVerificationResult.Canceled => "Verification canceled.",
        UserConsentVerificationResult.DeviceBusy => "The authentication device is busy. Try again.",
        UserConsentVerificationResult.DeviceNotPresent => "No Windows Hello device found.",
        UserConsentVerificationResult.DisabledByPolicy => "Windows Hello is disabled by policy on this device.",
        UserConsentVerificationResult.NotConfiguredForUser => "Windows Hello isn't set up. Configure a PIN or biometrics in Windows Settings.",
        UserConsentVerificationResult.RetriesExhausted => "Too many failed attempts. Try again later.",
        _ => "Verification failed. Try again.",
    };
}
