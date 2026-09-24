using System;
using FluentAssertions;
using OmenCore.Hardware;
using Xunit;

namespace OmenCoreApp.Tests.Hardware
{
    /// <summary>
    /// <see cref="PawnIOEcAccess.HoldByteAndFire{T}"/> is the timing-critical primitive behind the
    /// 8D87 GPU TGP unlock (<see cref="OmenCore.Services.GpuTgpUnlockService"/>): a single EC-mutex
    /// acquisition held across a spin-write loop and a synchronous WMI call, replacing the normal
    /// per-call-acquire-and-release-with-a-trailing-sleep path that made a ~2 ms hold impossible.
    /// The actual port I/O needs real PawnIO and real EC hardware, which this suite does not have -
    /// this only pins the one thing that is checkable without either: the address allowlist, which
    /// is deliberately checked before <c>EnsureAvailable()</c> so it refuses on a board this was
    /// never measured on without needing hardware to be present at all.
    /// </summary>
    public class PawnIOEcAccessHoldAndFireTests
    {
        [Theory]
        [InlineData(0x59)] // OGHP
        [InlineData(0x90)] // PROH
        public void AllowedAddresses_ReachPastTheAllowlistCheck(ushort address)
        {
            using var ec = new PawnIOEcAccess();

            // No PawnIO/EC hardware in a test host, so this cannot reach a real port write - but an
            // allowed address must get far enough to hit EnsureAvailable()'s "not initialized"
            // failure, not the allowlist's UnauthorizedAccessException. That is the boundary this
            // test actually checks.
            Action act = () => ec.HoldByteAndFire<bool>(address, 0xFF, 0x00, TimeSpan.FromMilliseconds(2), () => true);

            act.Should().NotThrow<UnauthorizedAccessException>();
        }

        [Theory]
        [InlineData((ushort)0x2C)] // a real fan-control address, allowed for ordinary WriteByte - not for this
        [InlineData((ushort)0x00)]
        [InlineData((ushort)0xFF)]
        public void UnlistedAddresses_AreRefused_BeforeAnyHardwareIsTouched(ushort address)
        {
            using var ec = new PawnIOEcAccess();

            Action act = () => ec.HoldByteAndFire<bool>(address, 0xFF, 0x00, TimeSpan.FromMilliseconds(2), () => true);

            act.Should().Throw<UnauthorizedAccessException>()
                .WithMessage("*hold-and-fire allowlist*");
        }

        [Fact]
        public void NullCallback_IsRejected_BeforeTheAllowlistCheck()
        {
            using var ec = new PawnIOEcAccess();

            Action act = () => ec.HoldByteAndFire<bool>(0x59, 0xFF, 0x00, TimeSpan.FromMilliseconds(2), null!);

            act.Should().Throw<ArgumentNullException>();
        }
    }
}
