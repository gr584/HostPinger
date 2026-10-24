using HostPinger.Components.Pages;

namespace HostPinger.Test
{
    /// <summary>
    /// How the graph page tells an address it is still on from one a navigation is taking it to.
    /// Blazor hands a live page its query again as it leaves, and the page reads only for its own.
    /// </summary>
    public class PingGraphAddressTests
    {
        [TestCase("graph")]
        [TestCase("graph/3")]
        [TestCase("graph/1,2,5")]
        [TestCase("graph?window=3600")]
        [TestCase("graph/3?start=2026-10-01T00:00:00Z&end=2026-10-02T00:00:00Z")]
        [TestCase("Graph/3")]
        public void IsGraphAddress_TakesTheGraphsOwnRoutes(string address) =>
            Assert.That(PingGraph.IsGraphAddress(address), Is.True);

        [TestCase("")]
        [TestCase("resolver-errors")]
        [TestCase("configuration?graph=1")]
        [TestCase("graphs")]
        [TestCase("about#graph")]
        public void IsGraphAddress_RefusesEveryOtherPage(string address) =>
            Assert.That(PingGraph.IsGraphAddress(address), Is.False);
    }
}
