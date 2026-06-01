using CityStoryMod.Storyteller;
using FluentAssertions;
using Xunit;

namespace CityStoryMod.Tests
{
    public class MapCoordsTests
    {
        // The transform is a near-identity per-axis scale: recentered x → game
        // east, recentered y → game north, no swap, no sign flip. These checks
        // pin the contract (axis mapping + the documented scale constants) so a
        // future refactor can't silently swap or flip an axis.

        [Fact]
        public void Origin_MapsToOrigin()
        {
            MapCoords.RecenteredToWorld(0, 0, out double wx, out double wz);
            wx.Should().Be(0);
            wz.Should().Be(0);
        }

        [Fact]
        public void PositiveCoords_StayPositive_NoAxisSwap()
        {
            // x is east, y is north — must land on worldX / worldZ respectively.
            MapCoords.RecenteredToWorld(820, 1140, out double wx, out double wz);
            wx.Should().BeApproximately(820 / MapCoords.XScale, 1e-6);
            wz.Should().BeApproximately(1140 / MapCoords.ZScale, 1e-6);
            // Sanity: both within ~0.5% of the input (near-unit scale).
            wx.Should().BeApproximately(820, 5);
            wz.Should().BeApproximately(1140, 5);
        }

        [Fact]
        public void NegativeCoords_PreserveSign()
        {
            MapCoords.RecenteredToWorld(-30, -1200, out double wx, out double wz);
            wx.Should().BeLessThan(0);
            wz.Should().BeLessThan(0);
            wx.Should().BeApproximately(-30 / MapCoords.XScale, 1e-6);
            wz.Should().BeApproximately(-1200 / MapCoords.ZScale, 1e-6);
        }
    }
}
