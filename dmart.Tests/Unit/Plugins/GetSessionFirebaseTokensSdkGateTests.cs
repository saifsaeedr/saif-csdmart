using Dmart.Sdk;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Plugins;

// Pure unit-level pin for the SDK-side version gate on
// DmartSdk.GetSessionFirebaseTokens (V5 callback). The wrapper must:
//   - return an empty list when cb.Version < 5, EVEN IF the function pointer
//     happens to be non-null (covers the in-place struct upgrade case)
//   - return an empty list when the function pointer is null, regardless
//     of the Version field (covers a partially-populated struct)
//   - never throw on the gate path
//
// Doesn't invoke the function pointer — that requires a real host. The
// gate's whole job is to *avoid* invoking the pointer on too-old hosts, so
// validating the no-call path is what matters.
public sealed class GetSessionFirebaseTokensSdkGateTests
{
    [Fact]
    public unsafe void GetSessionFirebaseTokens_OldHost_Version4_Returns_Empty()
    {
        // Even with a non-null fptr (the host could be ABI-mid-upgrade with
        // memory still live but Version not yet bumped), the SDK must trust
        // Version as the capability source of truth.
        var cb = new DmartCallbacks
        {
            Version = 4,
            GetSessionFirebaseTokens =
                (delegate* unmanaged[Cdecl]<byte*, int, byte*>)unchecked((IntPtr)0xDEADBEEF),
        };
        var result = DmartSdk.GetSessionFirebaseTokens(in cb, "anyone");
        result.ShouldBeEmpty();
    }

    [Fact]
    public unsafe void GetSessionFirebaseTokens_Version5_But_NullFunctionPointer_Returns_Empty()
    {
        // Defensive: a freshly-zeroed struct with Version manually set to 5
        // (no fptrs) — the SDK should NOT dereference a null pointer.
        var cb = new DmartCallbacks
        {
            Version = 5,
            GetSessionFirebaseTokens = null,
        };
        var result = DmartSdk.GetSessionFirebaseTokens(in cb, "anyone");
        result.ShouldBeEmpty();
    }

    [Fact]
    public unsafe void GetSessionFirebaseTokens_Version1_Returns_Empty_Regardless_Of_Args()
    {
        // V1-era struct — every field after dmart_free is undefined. The gate
        // must short-circuit before reading Version-appended fields, including
        // when the caller passes a TTL.
        var cb = new DmartCallbacks { Version = 1 };
        DmartSdk.GetSessionFirebaseTokens(in cb, "x").ShouldBeEmpty();
        DmartSdk.GetSessionFirebaseTokens(in cb, "x", inactivityTtlSeconds: 30).ShouldBeEmpty();
    }
}
