using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Ombi.Api.External.ExternalApis.Radarr;
using Ombi.Api.External.ExternalApis.Sonarr;
using Ombi.Core.Services;
using Ombi.Core.Settings;
using Ombi.Settings.Settings.Models.External;

namespace Ombi.Core.Tests.Services
{
    [TestFixture]
    public class QualityProfileSelectionServiceTests
    {
        private Mock<ISettingsService<RadarrSettings>> _radarrSettings;
        private Mock<ISettingsService<Radarr4KSettings>> _radarr4KSettings;
        private Mock<ISettingsService<SonarrSettings>> _sonarrSettings;
        private Mock<IRadarrV3Api> _radarrApi;
        private Mock<ISonarrV3Api> _sonarrApi;
        private QualityProfileSelectionService _service;

        [SetUp]
        public void Setup()
        {
            _radarrSettings = new Mock<ISettingsService<RadarrSettings>>();
            _radarr4KSettings = new Mock<ISettingsService<Radarr4KSettings>>();
            _sonarrSettings = new Mock<ISettingsService<SonarrSettings>>();
            _radarrApi = new Mock<IRadarrV3Api>();
            _sonarrApi = new Mock<ISonarrV3Api>();

            _service = new QualityProfileSelectionService(
                _radarrSettings.Object,
                _radarr4KSettings.Object,
                _sonarrSettings.Object,
                _radarrApi.Object,
                _sonarrApi.Object,
                Mock.Of<ILogger<QualityProfileSelectionService>>());
        }

        [Test]
        public async Task Zero_Is_Valid_As_No_Request_Override()
        {
            Assert.That(await _service.IsValidRadarrProfile(0, false), Is.True);
            Assert.That(await _service.IsValidSonarrProfile(0), Is.True);

            _radarrApi.Verify(x => x.GetProfiles(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            _sonarrApi.Verify(x => x.GetProfiles(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Test]
        public async Task Negative_Profile_Is_Invalid_Without_Calling_Arr()
        {
            Assert.That(await _service.IsValidRadarrProfile(-1, false), Is.False);
            Assert.That(await _service.IsValidSonarrProfile(-1), Is.False);

            _radarrApi.Verify(x => x.GetProfiles(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            _sonarrApi.Verify(x => x.GetProfiles(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Test]
        public void Sonarr_Profile_Load_Failure_Is_Not_Misreported_As_Empty_Profile_List()
        {
            _sonarrSettings.Setup(x => x.GetSettingsAsync()).ReturnsAsync(new SonarrSettings
            {
                Enabled = true,
                ApiKey = "test",
                Ip = "sonarr",
                Port = 8989
            });
            _sonarrApi
                .Setup(x => x.GetProfiles("test", It.IsAny<string>()))
                .ThrowsAsync(new InvalidOperationException("Sonarr unavailable"));

            Assert.ThrowsAsync<InvalidOperationException>(async () => await _service.GetSonarrProfiles());
        }
    }
}
