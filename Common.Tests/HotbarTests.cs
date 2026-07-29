namespace Demiurge.CommonTests;

public class HotbarTests
{
    [Fact]
    public void ScrollingWrapsAcrossAllThreeSlots()
    {
        Assert.Equal(
            HotbarSlot.Grenade,
            HotbarConfig.Scroll(HotbarSlot.Primary, -1));
        Assert.Equal(
            HotbarSlot.Primary,
            HotbarConfig.Scroll(HotbarSlot.Grenade, 1));
    }
}
