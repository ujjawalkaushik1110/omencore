using System;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using OmenCore.Hardware;
using OmenCore.Services;
using Xunit;

namespace OmenCoreApp.Tests.Hardware
{
    public class CapabilityDetectionServiceTests
    {
        private static CapabilityDetectionService CreateService()
        {
            var logging = new LoggingService();
            logging.Initialize();
            return new CapabilityDetectionService(logging);
        }

        private static void SetWmiBios(CapabilityDetectionService service, HpWmiBios wmiBios)
        {
            var field = typeof(CapabilityDetectionService).GetField("_wmiBios", BindingFlags.Instance | BindingFlags.NonPublic);
            field.Should().NotBeNull();
            field!.SetValue(service, wmiBios);
        }

        private static void SetSystemDesign(HpWmiBios wmiBios, HpWmiBios.SystemDesignData? design)
        {
            var property = typeof(HpWmiBios).GetProperty(nameof(HpWmiBios.SystemDesign));
            property.Should().NotBeNull();
            property!.SetValue(wmiBios, design);
        }

        private static void InvokeRefineCapabilitiesFromModel(CapabilityDetectionService service)
        {
            var method = typeof(CapabilityDetectionService).GetMethod("RefineCapabilitiesFromModel", BindingFlags.Instance | BindingFlags.NonPublic);
            method.Should().NotBeNull();
            method!.Invoke(service, null);
        }

        private static ModelCapabilities UnverifiedTemplateModel() => new()
        {
            ProductId = "TESTFAMILYFALLBACK",
            ModelName = "Test Family Fallback Board",
            Family = OmenModelFamily.Victus,
            SupportsFanControlWmi = true,
            SupportsFanControlEc = false,
            FanZoneCount = 2,
            UserVerified = false
        };

        [Fact]
        public void RefineCapabilitiesFromModel_KeepsFanControl_WhenFirmwareBitReadsFalse_OnUnverifiedBoard()
        {
            // GitHub #203 (board 8C2F, Victus 15-fb2xxx) and #202 (8BB1, Victus 15-fa1xxx): v4.3.1
            // forced monitoring-only whenever IsSwFanControlSupport read false on an unverified
            // board, which switched off Custom Fan Curve that worked in 4.3.0 on hardware where
            // WMI 0x2E writes are accepted, read back, and audibly move the fans. The bit reads
            // false on V0-thermal-policy firmware because the byte isn't populated there, so it
            // can never be used to narrow a capability - only a real write outcome can.
            var service = CreateService();
            service.Capabilities.ModelConfig = UnverifiedTemplateModel();
            service.Capabilities.CanSetFanSpeed = true;
            service.Capabilities.FanControl = FanControlMethod.WmiBios;

            using var wmiBios = new HpWmiBios(null);
            SetSystemDesign(wmiBios, new HpWmiBios.SystemDesignData { IsSwFanControlSupport = false });
            SetWmiBios(service, wmiBios);

            InvokeRefineCapabilitiesFromModel(service);

            service.Capabilities.CanSetFanSpeed.Should().BeTrue("the firmware bit is diagnostic only and must never disable working fan control");
            service.Capabilities.FanControl.Should().Be(FanControlMethod.WmiBios);
        }

        [Theory]
        [InlineData("C8-00-00-00-00-00-00-02-00-00-00-00")] // 8C2F, #203: policy V0, flag 0
        [InlineData("C8-00-00-00-00-D2-00-02-00-00-00-00")] // 8BB1, #202: policy V0, flag 0
        public void RealV0FirmwareBlocks_DecodeAsNoSwFanFlag_ButPolicyZero_SoTheBitIsUnpopulatedNotADenial(string hex)
        {
            // The captured replies that exposed the v4.3.1 regression. Both decode to
            // IsSwFanControlSupport=false AND ThermalPolicyVersion=V0 - i.e. an unpopulated legacy
            // block, on machines whose fans respond to software commands.
            var bytes = hex.Split('-').Select(h => Convert.ToByte(h, 16)).Concat(new byte[116]).ToArray();

            var design = HpWmiBios.DecodeSystemDesignData(bytes);

            design.Should().NotBeNull();
            design!.Value.IsSwFanControlSupport.Should().BeFalse();
            design.Value.ThermalPolicyVersion.Should().Be(HpWmiBios.ThermalPolicyVersion.V0);
        }

        [Fact]
        public void RealV1FirmwareBlock_DecodesTheFlagAsSet()
        {
            // 8BD4 (Victus 16-s0xxx) and 8CC0 (OMEN 16-ae0xxx) report policy V1 with the bit set.
            var bytes = "18-01-00-01-01-BE-00-06-23-00-00-00".Split('-').Select(h => Convert.ToByte(h, 16)).Concat(new byte[116]).ToArray();

            var design = HpWmiBios.DecodeSystemDesignData(bytes);

            design!.Value.IsSwFanControlSupport.Should().BeTrue();
            design.Value.ThermalPolicyVersion.Should().Be(HpWmiBios.ThermalPolicyVersion.V1);
        }

        [Fact]
        public void RefineCapabilitiesFromModel_LeavesFanControlAlone_WhenBoardIsUserVerified()
        {
            // A hand-verified board's flags come from a real person's hardware and must stay
            // authoritative over a generic firmware-byte heuristic, even if that heuristic
            // disagrees.
            var service = CreateService();
            var verifiedModel = UnverifiedTemplateModel();
            verifiedModel.UserVerified = true;
            service.Capabilities.ModelConfig = verifiedModel;
            service.Capabilities.CanSetFanSpeed = true;
            service.Capabilities.FanControl = FanControlMethod.WmiBios;

            using var wmiBios = new HpWmiBios(null);
            SetSystemDesign(wmiBios, new HpWmiBios.SystemDesignData { IsSwFanControlSupport = false });
            SetWmiBios(service, wmiBios);

            InvokeRefineCapabilitiesFromModel(service);

            service.Capabilities.CanSetFanSpeed.Should().BeTrue(
                "a UserVerified board's own confirmed flags must not be second-guessed by SystemDesignData");
            service.Capabilities.FanControl.Should().Be(FanControlMethod.WmiBios);
        }

        [Fact]
        public void RefineCapabilitiesFromModel_LeavesFanControlAlone_WhenFirmwareConfirmsSwFanControl()
        {
            var service = CreateService();
            service.Capabilities.ModelConfig = UnverifiedTemplateModel();
            service.Capabilities.CanSetFanSpeed = true;
            service.Capabilities.FanControl = FanControlMethod.WmiBios;

            using var wmiBios = new HpWmiBios(null);
            SetSystemDesign(wmiBios, new HpWmiBios.SystemDesignData { IsSwFanControlSupport = true });
            SetWmiBios(service, wmiBios);

            InvokeRefineCapabilitiesFromModel(service);

            service.Capabilities.CanSetFanSpeed.Should().BeTrue();
            service.Capabilities.FanControl.Should().Be(FanControlMethod.WmiBios);
        }

        [Fact]
        public void RefineCapabilitiesFromModel_LeavesFanControlAlone_WhenSystemDesignDataUnavailable()
        {
            // No WMI BIOS / no SystemDesignData reading (e.g. WMI unavailable on this machine) must
            // fall back to the existing model-database-only behavior, not silently disable fan
            // control for lack of evidence.
            var service = CreateService();
            service.Capabilities.ModelConfig = UnverifiedTemplateModel();
            service.Capabilities.CanSetFanSpeed = true;
            service.Capabilities.FanControl = FanControlMethod.WmiBios;

            InvokeRefineCapabilitiesFromModel(service);

            service.Capabilities.CanSetFanSpeed.Should().BeTrue();
            service.Capabilities.FanControl.Should().Be(FanControlMethod.WmiBios);
        }
    }
}
