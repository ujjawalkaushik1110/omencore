using FluentAssertions;
using OmenCore.Services;
using Xunit;

namespace OmenCoreApp.Tests.Services
{
    /// <summary>
    /// The tray's refresh-rate shortcuts used to pass a null device, which Windows reads as "primary
    /// display" - the external monitor on a docked laptop. They now resolve the built-in panel
    /// through the CCD API's output technology. The live query needs real display hardware, so
    /// these pin the classification and the marshalled layout it depends on.
    /// </summary>
    public class DisplayServiceInternalPanelTests
    {
        [Theory]
        [InlineData(0x80000000u)] // INTERNAL
        [InlineData(11u)]         // DISPLAYPORT_EMBEDDED - typical laptop eDP panel
        [InlineData(13u)]         // UDI_EMBEDDED
        [InlineData(6u)]          // LVDS
        public void BuiltInOutputTechnologies_AreTheLaptopPanel(uint technology)
        {
            DisplayService.IsInternalOutputTechnology(technology).Should().BeTrue();
        }

        [Theory]
        [InlineData(5u)]  // HDMI
        [InlineData(10u)] // DISPLAYPORT_EXTERNAL
        [InlineData(4u)]  // DVI
        [InlineData(0u)]  // HD15 / VGA
        public void ExternalOutputTechnologies_AreNotTheLaptopPanel(uint technology)
        {
            DisplayService.IsInternalOutputTechnology(technology).Should().BeFalse();
        }

        [Fact]
        public void CcdStructs_MarshalToTheWindowsSdkSizes()
        {
            DisplayService.PathInfoMarshalSize.Should().Be(72, "sizeof(DISPLAYCONFIG_PATH_INFO)");
            DisplayService.SourceDeviceNameMarshalSize.Should().Be(84, "sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME)");
        }
    }
}
