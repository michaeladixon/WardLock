using System.Runtime.InteropServices;
using Windows.Security.Credentials.UI;

namespace WardLock.Services;

/// <summary>
/// Uses Windows Hello (fingerprint, face, PIN) to gate access to the vault.
/// Falls back gracefully if Windows Hello is not available.
/// </summary>
public static class WindowsHelloService
{
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
    /// Returns true if verified, false if denied/cancelled/unavailable.
    /// </summary>
    public static async Task<bool> VerifyAsync(string message = "Verify your identity to access WardLock")
    {
        try
        {
            var result = await UserConsentVerifier.RequestVerificationAsync(message);
            return result == UserConsentVerificationResult.Verified;
        }
        catch (COMException)
        {
            // WinRT interop can throw if the API is unavailable
            return false;
        }
        catch
        {
            return false;
        }
    }
}
